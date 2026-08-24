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
        try
        {
            await ExecuteAsync(admin, $"SET search_path TO {schema}");
            await QuartzSchemaInstaller.EnsureQuartzTablesExistAsync(admin, CancellationToken.None);
            var builder = new NpgsqlConnectionStringBuilder(persistedConnectionString) { SearchPath = schema };
            var schedulerName = $"Wayfarer507-{Guid.NewGuid():N}";
            first = await new StdSchedulerFactory(Properties(builder.ConnectionString, schedulerName)).GetScheduler();
            user = await fixture.CreateUserAsync();
            var workflow = LocationEnrichmentWorkflow.Create(user.Id, DateTime.UtcNow);
            workflow.Start(DateTime.UtcNow);
            await using (var domain = fixture.CreateContext())
            { domain.Add(workflow); await domain.SaveChangesAsync(); }

            await new LocationEnrichmentScheduler(first).EnsureScheduledAsync(workflow);
            var overdue = TriggerBuilder.Create()
                .WithIdentity(LocationEnrichmentScheduler.TriggerKey(workflow.SchedulerId, workflow.Epoch))
                .ForJob(LocationEnrichmentScheduler.JobKey(workflow.SchedulerId))
                .UsingJobData("epoch", workflow.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .StartAt(DateTimeOffset.UtcNow.AddMinutes(-1))
                .WithSimpleSchedule(schedule => schedule.WithRepeatCount(0)
                    .WithMisfireHandlingInstructionFireNow()).Build();
            await first.RescheduleJob(overdue.Key, overdue);
            await first.Shutdown(false);
            first = null;

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
            await restarted.Shutdown(true);
            restarted = null;

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
        }
        finally
        {
            if (first is not null) await first.Shutdown(false);
            if (restarted is not null) await restarted.Shutdown(false);
            if (reconstructed is not null) await reconstructed.Shutdown(false);
            if (user is not null)
            {
                await using var cleanup = fixture.CreateContext();
                await cleanup.LocationEnrichmentWorkflows.Where(item => item.UserId == user.Id).ExecuteDeleteAsync();
            }
            await ExecuteAsync(admin, "SET search_path TO public");
            await ExecuteAsync(admin, $"DROP SCHEMA IF EXISTS {schema} CASCADE");
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
}
