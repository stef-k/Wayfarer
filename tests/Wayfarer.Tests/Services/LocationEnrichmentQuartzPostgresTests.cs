using System.Collections.Specialized;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Quartz;
using Quartz.Impl;
using Quartz.Spi;
using Moq;
using Wayfarer.Jobs;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves workflow projection survives a real PostgreSQL Quartz restart without replay.</summary>
[Collection(PostgresEnvironmentEvidenceTestCollection.Name)]
public sealed class LocationEnrichmentQuartzPostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresFact(Timeout = 30_000)]
    public async Task OverdueProductionOneShotExecutesOnceAndDoesNotReplayAfterRestart()
    {
        fixture.RequireAvailable();
        await using var admin = fixture.CreateConnection();
        var persistedConnectionString = admin.ConnectionString;
        await admin.OpenAsync();
        var schema = $"quartz_507_{Guid.NewGuid():N}";
        await ExecuteAsync(admin, $"CREATE SCHEMA {schema}");
        IScheduler? first = null;
        IScheduler? restarted = null;
        IScheduler? reconstructed = null;
        ApplicationUser? user = null;
        Exception? primary = null;
        var schedulerName = $"Wayfarer507-{Guid.NewGuid():N}";
        try
        {
            await ExecuteAsync(admin, $"SET search_path TO {schema}");
            await QuartzSchemaInstaller.EnsureQuartzTablesExistAsync(admin, CancellationToken.None);
            var builder = new NpgsqlConnectionStringBuilder(persistedConnectionString) { SearchPath = schema };
            first = await new StdSchedulerFactory(Properties(builder.ConnectionString, schedulerName)).GetScheduler();
            user = await fixture.CreateUserAsync();
            var workflow = LocationEnrichmentWorkflow.Create(user.Id, DateTime.UtcNow);
            workflow.Start(DateTime.UtcNow);
            workflow.ContinueAs(LocationEnrichmentState.BackingOff,
                LocationEnrichmentOutcome.RetryableFailure, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow);
            await using (var domain = fixture.CreateContext())
            { domain.Add(workflow); await domain.SaveChangesAsync(); }

            await new LocationEnrichmentScheduler(first).EnsureScheduledAsync(workflow);
            var persisted = Assert.IsAssignableFrom<ISimpleTrigger>(await first.GetTrigger(
                LocationEnrichmentScheduler.TriggerKey(workflow.SchedulerId, workflow.Epoch)));
            Assert.Equal(MisfireInstruction.SimpleTrigger.FireNow, persisted.MisfireInstruction);
            Assert.Equal(workflow.SchedulerId.ToString("N"),
                (await first.GetJobDetail(LocationEnrichmentScheduler.JobKey(workflow.SchedulerId)))!
                    .JobDataMap.GetString("workflowId"));
            var jobMap = (await first.GetJobDetail(LocationEnrichmentScheduler.JobKey(workflow.SchedulerId)))!.JobDataMap;
            Assert.Equal(["schema", "workflowId"], jobMap.Keys.Cast<string>().OrderBy(item => item));
            Assert.All(jobMap.Values.Cast<object>(), value => Assert.IsType<string>(value));
            Assert.Equal("1", jobMap.GetString("schema"));
            Assert.Equal("1", persisted.JobDataMap.GetString("epoch"));
            Assert.All(persisted.JobDataMap.Values.Cast<object>(), value => Assert.IsType<string>(value));
            await first.Shutdown(false);

            restarted = await new StdSchedulerFactory(Properties(builder.ConnectionString, schedulerName)).GetScheduler();
            var executions = 0;
            var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new Mock<ILocationEnrichmentWorker>();
            worker.Setup(item => item.RunBatchAsync(user.Id, workflow.Epoch, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => { Interlocked.Increment(ref executions); fired.TrySetResult();
                    return LocationEnrichmentWorkerOutcome.Completed; });
            restarted.JobFactory = new ProductionJobFactory(fixture, worker.Object);
            await restarted.Start();
            Assert.Same(fired.Task, await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(10))));
            Assert.Equal(1, executions);
            await WaitUntilAsync(async () => !await restarted.CheckExists(
                LocationEnrichmentScheduler.TriggerKey(workflow.SchedulerId, workflow.Epoch)), TimeSpan.FromSeconds(5));
            await restarted.Shutdown(true);
            Assert.Equal(1, executions);

            reconstructed = await new StdSchedulerFactory(Properties(builder.ConnectionString, schedulerName)).GetScheduler();
            var replayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var replayWorker = new Mock<ILocationEnrichmentWorker>();
            replayWorker.Setup(item => item.RunBatchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => { replayed.TrySetResult(); return LocationEnrichmentWorkerOutcome.Completed; });
            reconstructed.JobFactory = new ProductionJobFactory(fixture, replayWorker.Object);
            await reconstructed.Start();
            Assert.NotSame(replayed.Task, await Task.WhenAny(replayed.Task, Task.Delay(TimeSpan.FromSeconds(2))));
            replayWorker.Verify(item => item.RunBatchAsync(It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()), Times.Never);
            await reconstructed.Shutdown(true);
            Assert.Equal(1, executions);
        }
        catch (Exception exception)
        {
            primary = exception;
        }
        finally
        {
            var cleanupScheduler = await new StdSchedulerFactory(Properties(
                new NpgsqlConnectionStringBuilder(persistedConnectionString) { SearchPath = schema }.ConnectionString,
                schedulerName)).GetScheduler();
            await FailureIndependentCleanup.CompleteAsync(primary,
            [
                ("first scheduler shutdown", () => first?.Shutdown(false) ?? Task.CompletedTask),
                ("restarted scheduler shutdown", () => restarted?.Shutdown(false) ?? Task.CompletedTask),
                ("reconstructed scheduler shutdown", () => reconstructed?.Shutdown(false) ?? Task.CompletedTask),
                ("Quartz projection cleanup", async () => { await cleanupScheduler.Clear(); await cleanupScheduler.Shutdown(true); }),
                ("first scheduler disposal", () => DisposeSchedulerAsync(first)),
                ("restarted scheduler disposal", () => DisposeSchedulerAsync(restarted)),
                ("reconstructed scheduler disposal", () => DisposeSchedulerAsync(reconstructed)),
                ("cleanup scheduler disposal", () => DisposeSchedulerAsync(cleanupScheduler)),
                ("relational fixture cleanup", async () =>
                {
                    if (user is null) return;
                    await using var cleanup = fixture.CreateContext();
                    await cleanup.Users.Where(item => item.Id == user.Id).ExecuteDeleteAsync();
                    Assert.Equal(0, await cleanup.LocationEnrichmentWorkflows.CountAsync(item => item.UserId == user.Id));
                    Assert.Equal(0, await cleanup.LocationEnrichmentAttempts.CountAsync(item => item.UserId == user.Id));
                }),
                ("Quartz residue verification", () => AssertQuartzResidueAsync(admin, schema)),
                ("fixture schema removal", async () =>
                {
                    await ExecuteAsync(admin, "SET search_path TO public");
                    await ExecuteAsync(admin, $"DROP SCHEMA IF EXISTS {schema} CASCADE");
                    await AssertSchemaRemovedAsync(admin, schema);
                })
            ]);
        }
    }

    [PostgresFact(Timeout = 30_000)]
    public async Task OverdueStaleAuthorityFinalizesDurablyWithoutWorkerEntryAcrossRestarts()
    {
        fixture.RequireAvailable();
        await using var admin = fixture.CreateConnection();
        var persistedConnectionString = admin.ConnectionString;
        await admin.OpenAsync();
        var schema = $"quartz_510_{Guid.NewGuid():N}";
        await ExecuteAsync(admin, $"CREATE SCHEMA {schema}");
        var schedulers = new List<IScheduler>();
        ApplicationUser? user = null;
        Exception? primary = null;
        var schedulerName = $"Wayfarer510-{Guid.NewGuid():N}";
        try
        {
            await ExecuteAsync(admin, $"SET search_path TO {schema}");
            await QuartzSchemaInstaller.EnsureQuartzTablesExistAsync(admin, CancellationToken.None);
            var builder = new NpgsqlConnectionStringBuilder(persistedConnectionString) { SearchPath = schema };
            var writer = await new StdSchedulerFactory(Properties(builder.ConnectionString, schedulerName)).GetScheduler();
            schedulers.Add(writer);
            user = await fixture.CreateUserAsync();
            var workflow = LocationEnrichmentWorkflow.Create(user.Id, DateTime.UtcNow);
            workflow.Start(DateTime.UtcNow);
            workflow.ContinueAs(LocationEnrichmentState.BackingOff,
                LocationEnrichmentOutcome.RetryableFailure, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow);
            await using (var domain = fixture.CreateContext())
            { domain.Add(workflow); await domain.SaveChangesAsync(); }
            await new LocationEnrichmentScheduler(writer).EnsureScheduledAsync(workflow);
            var staleKey = LocationEnrichmentScheduler.TriggerKey(workflow.SchedulerId, workflow.Epoch);
            var staleJob = (await writer.GetJobDetail(
                LocationEnrichmentScheduler.JobKey(workflow.SchedulerId)))!;
            Assert.Equal(["schema", "workflowId"], staleJob.JobDataMap.Keys.Cast<string>().OrderBy(item => item));
            Assert.All(staleJob.JobDataMap.Values.Cast<object>(), value => Assert.IsType<string>(value));
            var staleTrigger = (await writer.GetTrigger(staleKey))!;
            Assert.Equal(["epoch"], staleTrigger.JobDataMap.Keys.Cast<string>());
            Assert.All(staleTrigger.JobDataMap.Values.Cast<object>(), value => Assert.IsType<string>(value));
            await writer.Shutdown(false);
            await using (var domain = fixture.CreateContext())
            {
                var current = await domain.LocationEnrichmentWorkflows.SingleAsync(item => item.UserId == user.Id);
                current.Pause(DateTime.UtcNow);
                await domain.SaveChangesAsync();
            }

            var worker = new Mock<ILocationEnrichmentWorker>();
            for (var restart = 0; restart < 2; restart++)
            {
                var current = await new StdSchedulerFactory(Properties(builder.ConnectionString, schedulerName)).GetScheduler();
                schedulers.Add(current);
                current.JobFactory = new ProductionJobFactory(fixture, worker.Object);
                await current.Start();
                await WaitUntilAsync(async () => !await current.CheckExists(staleKey), TimeSpan.FromSeconds(10));
                await current.Shutdown(true);
                worker.Verify(item => item.RunBatchAsync(It.IsAny<string>(), It.IsAny<int>(),
                    It.IsAny<CancellationToken>()), Times.Never);
            }
        }
        catch (Exception exception)
        {
            primary = exception;
        }
        finally
        {
            var cleanupScheduler = await new StdSchedulerFactory(Properties(
                new NpgsqlConnectionStringBuilder(persistedConnectionString) { SearchPath = schema }.ConnectionString,
                schedulerName)).GetScheduler();
            var cleanupSteps = schedulers.Select((current, index) =>
                ($"scheduler {index} shutdown", (Func<Task>)(() => current.Shutdown(false)))).ToList();
            cleanupSteps.AddRange(
            [
                ("Quartz projection cleanup", async () => { await cleanupScheduler.Clear(); await cleanupScheduler.Shutdown(true); }),
                .. schedulers.Select((current, index) =>
                    ($"scheduler {index} disposal", (Func<Task>)(() => DisposeSchedulerAsync(current)))),
                ("cleanup scheduler disposal", () => DisposeSchedulerAsync(cleanupScheduler)),
                ("relational fixture cleanup", async () =>
                {
                    if (user is null) return;
                    await using var cleanup = fixture.CreateContext();
                    await cleanup.Users.Where(item => item.Id == user.Id).ExecuteDeleteAsync();
                    Assert.Equal(0, await cleanup.LocationEnrichmentWorkflows.CountAsync(item => item.UserId == user.Id));
                    Assert.Equal(0, await cleanup.LocationEnrichmentAttempts.CountAsync(item => item.UserId == user.Id));
                }),
                ("Quartz residue verification", () => AssertQuartzResidueAsync(admin, schema)),
                ("fixture schema removal", async () =>
                {
                    await ExecuteAsync(admin, "SET search_path TO public");
                    await ExecuteAsync(admin, $"DROP SCHEMA IF EXISTS {schema} CASCADE");
                    await AssertSchemaRemovedAsync(admin, schema);
                })
            ]);
            await FailureIndependentCleanup.CompleteAsync(primary, cleanupSteps);
        }
    }

    private sealed class ProductionJobFactory(PostgresImportTestFixture fixture, ILocationEnrichmentWorker worker)
        : IJobFactory
    {
        private readonly List<ApplicationDbContext> contexts = [];

        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
        {
            var db = fixture.CreateContext();
            contexts.Add(db);
            return new LocationEnrichmentJob(db, worker);
        }

        public void ReturnJob(IJob job)
        {
            foreach (var db in contexts) db.Dispose();
            contexts.Clear();
        }
    }

    private static NameValueCollection Properties(string connectionString, string schedulerName) => new()
    {
        ["quartz.scheduler.instanceName"] = schedulerName,
        ["quartz.scheduler.instanceId"] = "AUTO",
        ["quartz.jobStore.type"] = "Quartz.Impl.AdoJobStore.JobStoreTX, Quartz",
        ["quartz.jobStore.driverDelegateType"] = "Quartz.Impl.AdoJobStore.PostgreSQLDelegate, Quartz",
        ["quartz.jobStore.tablePrefix"] = "qrtz_",
        ["quartz.jobStore.useProperties"] = "true",
        ["quartz.jobStore.dataSource"] = "default",
        ["quartz.dataSource.default.provider"] = "Npgsql",
        ["quartz.dataSource.default.connectionString"] = connectionString,
        ["quartz.serializer.type"] = "Quartz.Simpl.JsonObjectSerializer, Quartz.Serialization.Json"
    };

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!await condition())
        {
            cancellation.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private static async Task DisposeSchedulerAsync(IScheduler? scheduler)
    {
        if (scheduler is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
        else if (scheduler is IDisposable disposable) disposable.Dispose();
    }

    private static async Task AssertQuartzResidueAsync(NpgsqlConnection connection, string schema)
    {
        foreach (var table in new[] { "qrtz_job_details", "qrtz_triggers", "qrtz_fired_triggers", "qrtz_scheduler_state" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM {schema}.{table}";
            var count = (long)(await command.ExecuteScalarAsync())!;
            Assert.True(count == 0, $"Expected no fixture-owned rows in {table}, found {count}.");
        }
    }

    private static async Task AssertSchemaRemovedAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM pg_namespace WHERE nspname = @schema";
        command.Parameters.AddWithValue("schema", schema);
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }
}
