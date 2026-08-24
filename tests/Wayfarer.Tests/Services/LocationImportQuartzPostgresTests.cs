using System.Collections.Specialized;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Quartz;
using Quartz.Impl;
using Quartz.Spi;
using Wayfarer.Jobs;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Exercises import lifecycle reconstruction against the production PostgreSQL Quartz store.</summary>
[Collection(PostgresEnvironmentEvidenceTestCollection.Name)]
public sealed class LocationImportQuartzPostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresFact(Timeout = 30_000)]
    public async Task ActiveMissingProjection_ExecutesOnceAndDoesNotReplay()
    {
        await using var harness = await PersistentHarness.CreateAsync(fixture);
        var seed = await harness.SeedAsync(ImportStatus.InProgress, epoch: 1, projectionPending: true);
        await harness.ReconstructAsync();
        await harness.ReconcileAsync();
        await harness.AssertBoundedPayloadAsync(seed.ImportId, 1);

        await harness.StartAsync(LocationImportExecutionOutcome.Completed);
        await harness.WaitForExecutionsAsync(1);
        await harness.WaitForTriggerFinalizationAsync(seed.ImportId, 1);
        await harness.ShutdownCurrentAsync();

        await harness.ReconstructAsync();
        await harness.ReconcileAsync();
        Assert.False(await harness.Current.CheckExists(LocationImportSchedulerKeys.Job(seed.ImportId, 1)));
        await harness.StartAsync(LocationImportExecutionOutcome.Completed);
        Assert.Equal(1, harness.ExecutionCount);
    }

    [PostgresFact(Timeout = 30_000)]
    public async Task SchedulingFailure_RemainsPendingAndRepairsAfterRestart()
    {
        await using var harness = await PersistentHarness.CreateAsync(fixture);
        var seed = await harness.SeedAsync(ImportStatus.Stopped);
        await using (var db = fixture.CreateContext())
        {
            var failing = new Mock<IScheduler>();
            failing.Setup(item => item.CheckExists(It.IsAny<JobKey>(), default)).ReturnsAsync(false);
            failing.Setup(item => item.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), default))
                .ThrowsAsync(new SchedulerException("fixture projection failure"));
            var result = await new LocationImportLifecycle(db, failing.Object,
                NullLogger<LocationImportLifecycle>.Instance).StartAsync(seed.UserId, seed.ImportId);
            Assert.Equal(LocationImportCommandCode.ProjectionPending, result.Code);
        }
        await using (var verification = fixture.CreateContext())
        {
            var current = await verification.LocationImports.FindAsync(seed.ImportId);
            Assert.Equal(ImportStatus.InProgress, current!.Status);
            Assert.Equal(1, current.ExecutionEpoch);
            Assert.True(current.ProjectionPending);
        }

        await harness.ReconstructAsync();
        await harness.ReconcileAsync();
        await harness.StartAsync(LocationImportExecutionOutcome.Completed);
        await harness.WaitForExecutionsAsync(1);
        await harness.WaitForTriggerFinalizationAsync(seed.ImportId, 1);
        await harness.ShutdownCurrentAsync();
        await harness.ReconstructAsync();
        await harness.ReconcileAsync();
        Assert.False(await harness.Current.CheckExists(LocationImportSchedulerKeys.Job(seed.ImportId, 1)));
        Assert.Equal(1, harness.ExecutionCount);
    }

    [PostgresFact(Timeout = 30_000)]
    public async Task StoppingWithoutExecution_ConvergesWithoutWorkerAndIsIdempotent()
    {
        await using var harness = await PersistentHarness.CreateAsync(fixture);
        var seed = await harness.SeedAsync(ImportStatus.Stopping, epoch: 3, projectionPending: true);
        await harness.ReconstructAsync();
        await harness.ReconcileAsync();
        await harness.ReconcileAsync();
        await using var verification = fixture.CreateContext();
        var current = await verification.LocationImports.FindAsync(seed.ImportId);
        Assert.Equal(ImportStatus.Stopped, current!.Status);
        Assert.False(current.ProjectionPending);
        Assert.Equal(0, harness.ExecutionCount);
    }

    [PostgresFact(Timeout = 30_000)]
    public async Task StaleEpochProjection_IsRemovedBeforeSchedulerStartAndNeverReplays()
    {
        await using var harness = await PersistentHarness.CreateAsync(fixture);
        var seed = await harness.SeedAsync(ImportStatus.InProgress, epoch: 1);
        await harness.ReconstructAsync();
        await harness.Current.ScheduleJob(LocationImportSchedulerKeys.BuildJob(seed.ImportId, 1),
            LocationImportSchedulerKeys.BuildTrigger(seed.ImportId, 1));
        await harness.ShutdownCurrentAsync();
        await using (var db = fixture.CreateContext())
        {
            var current = await db.LocationImports.FindAsync(seed.ImportId);
            current!.ExecutionEpoch = 2;
            current.Status = ImportStatus.Stopped;
            await db.SaveChangesAsync();
        }

        await harness.ReconstructAsync();
        await harness.ReconcileAsync();
        Assert.False(await harness.Current.CheckExists(LocationImportSchedulerKeys.Job(seed.ImportId, 1)));
        await harness.StartAsync(LocationImportExecutionOutcome.Completed);
        Assert.Equal(0, harness.ExecutionCount);
        await harness.ShutdownCurrentAsync();
        await harness.ReconstructAsync();
        await harness.ReconcileAsync();
        Assert.Equal(0, harness.ExecutionCount);
    }

    [PostgresFact(Timeout = 30_000)]
    public async Task PendingDeletion_RemovesProjectionFileAndImportAndSecondPassIsMutationFree()
    {
        await using var harness = await PersistentHarness.CreateAsync(fixture);
        var seed = await harness.SeedAsync(ImportStatus.Completed, epoch: 4, deletionPending: true);
        await harness.ReconstructAsync();
        await harness.Current.AddJob(LocationImportSchedulerKeys.BuildJob(seed.ImportId, 4), false);
        await harness.ShutdownCurrentAsync();
        await harness.ReconstructAsync();
        await harness.ReconcileAsync();
        Assert.False(File.Exists(seed.FilePath));
        await using (var verification = fixture.CreateContext())
        {
            Assert.Null(await verification.LocationImports.FindAsync(seed.ImportId));
            Assert.NotNull(await verification.Users.FindAsync(seed.UserId));
        }
        await harness.ReconcileAsync();
        Assert.Equal(0, harness.ExecutionCount);
    }

    private sealed class PersistentHarness : IAsyncDisposable
    {
        private readonly PostgresImportTestFixture fixture;
        private readonly NpgsqlConnection admin;
        private readonly string schema;
        private readonly string connectionString;
        private readonly string schedulerName;
        private readonly List<IScheduler> schedulers = [];
        private readonly List<string> files = [];
        private readonly Mock<ILocationImportService> service = new();
        private readonly Mock<ILocationImportExecutionService> execution;
        private IScheduler? current;
        private int executionCount;

        private PersistentHarness(PostgresImportTestFixture fixture, NpgsqlConnection admin, string schema,
            string connectionString, string schedulerName)
        {
            this.fixture = fixture;
            this.admin = admin;
            this.schema = schema;
            this.connectionString = connectionString;
            this.schedulerName = schedulerName;
            execution = service.As<ILocationImportExecutionService>();
        }

        internal IScheduler Current => current!;
        internal int ExecutionCount => Volatile.Read(ref executionCount);

        internal static async Task<PersistentHarness> CreateAsync(PostgresImportTestFixture fixture)
        {
            fixture.RequireAvailable();
            var admin = fixture.CreateConnection();
            var persistedConnectionString = admin.ConnectionString;
            await admin.OpenAsync();
            var schema = $"quartz_511_{Guid.NewGuid():N}";
            await ExecuteAsync(admin, $"CREATE SCHEMA {schema}");
            await ExecuteAsync(admin, $"SET search_path TO {schema}");
            await QuartzSchemaInstaller.EnsureQuartzTablesExistAsync(admin, CancellationToken.None);
            var connection = new NpgsqlConnectionStringBuilder(persistedConnectionString) { SearchPath = schema }.ConnectionString;
            return new(fixture, admin, schema, connection, $"Wayfarer511-{Guid.NewGuid():N}");
        }

        internal async Task<(string UserId, int ImportId, string FilePath)> SeedAsync(ImportStatus status,
            int epoch = 0, bool projectionPending = false, bool deletionPending = false)
        {
            var user = await fixture.CreateUserAsync();
            var directory = Path.Combine(Path.GetTempPath(), $"wayfarer-511-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "fixture.csv");
            await File.WriteAllTextAsync(path, "latitude,longitude");
            files.Add(path);
            await using var db = fixture.CreateContext();
            var import = new LocationImport
            {
                UserId = user.Id, FilePath = path, FileType = LocationImportFileType.Csv,
                TotalRecords = 0, LastProcessedIndex = 0, Status = status, ExecutionEpoch = epoch,
                ProjectionPending = projectionPending,
                StopRequestedAtUtc = status == ImportStatus.Stopping ? DateTime.UtcNow : null,
                DeletionRequestedAtUtc = deletionPending ? DateTime.UtcNow : null
            };
            db.LocationImports.Add(import);
            await db.SaveChangesAsync();
            return (user.Id, import.Id, path);
        }

        internal async Task ReconstructAsync()
        {
            current = await new StdSchedulerFactory(Properties(connectionString, schedulerName)).GetScheduler();
            schedulers.Add(current);
        }

        internal async Task ReconcileAsync()
        {
            await new LocationImportReconciler(new FixtureContextFactory(fixture), Current,
                NullLogger<LocationImportReconciler>.Instance).ReconcileAsync();
        }

        internal async Task StartAsync(LocationImportExecutionOutcome outcome)
        {
            execution.Setup(item => item.ProcessImportExecution(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => { Interlocked.Increment(ref executionCount); return outcome; });
            Current.JobFactory = new ImportJobFactory(fixture, service.Object);
            await Current.Start();
        }

        internal async Task AssertBoundedPayloadAsync(int importId, int epoch)
        {
            var map = (await Current.GetJobDetail(LocationImportSchedulerKeys.Job(importId, epoch)))!.JobDataMap;
            Assert.Equal(["epoch", "importId"], map.Keys.Cast<string>().OrderBy(item => item));
            Assert.All(map.Values.Cast<object>(), value => Assert.IsType<string>(value));
            Assert.Equal(importId.ToString(System.Globalization.CultureInfo.InvariantCulture), map.GetString("importId"));
            Assert.Equal(epoch.ToString(System.Globalization.CultureInfo.InvariantCulture), map.GetString("epoch"));
        }

        internal async Task WaitForExecutionsAsync(int expected) => await WaitUntilAsync(
            () => Task.FromResult(ExecutionCount == expected), TimeSpan.FromSeconds(10));

        internal async Task WaitForTriggerFinalizationAsync(int importId, int epoch) => await WaitUntilAsync(
            async () => !await Current.CheckExists(LocationImportSchedulerKeys.Trigger(importId, epoch)), TimeSpan.FromSeconds(10));

        internal async Task ShutdownCurrentAsync()
        {
            if (current is not null && !current.IsShutdown) await current.Shutdown(true);
        }

        public async ValueTask DisposeAsync()
        {
            var steps = new List<(string, Func<Task>)>
            {
                ("Quartz cleanup", () => current is null || current.IsShutdown ? Task.CompletedTask : current.Clear())
            };
            steps.AddRange(schedulers.Select((scheduler, index) =>
                ($"scheduler {index} shutdown", (Func<Task>)(() => scheduler.Shutdown(false)))).ToList());
            steps.AddRange([
                .. schedulers.Select((scheduler, index) => ($"scheduler {index} disposal", (Func<Task>)(() => DisposeSchedulerAsync(scheduler)))),
                ("file cleanup", () => { foreach (var file in files) { if (File.Exists(file)) File.Delete(file); var directory = Path.GetDirectoryName(file)!; if (Directory.Exists(directory)) Directory.Delete(directory); } return Task.CompletedTask; }),
                ("Quartz residue", async () => { foreach (var table in new[] { "qrtz_job_details", "qrtz_triggers", "qrtz_fired_triggers", "qrtz_scheduler_state" }) Assert.Equal(0L, await CountAsync(admin, schema, table)); }),
                ("schema removal", async () => { await ExecuteAsync(admin, "SET search_path TO public"); await ExecuteAsync(admin, $"DROP SCHEMA IF EXISTS {schema} CASCADE"); }),
                ("admin disposal", () => admin.DisposeAsync().AsTask())
            ]);
            await FailureIndependentCleanup.CompleteAsync(null, steps);
        }
    }

    private sealed class FixtureContextFactory(PostgresImportTestFixture fixture)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateContext();
    }

    private sealed class ImportJobFactory(PostgresImportTestFixture fixture, ILocationImportService service) : IJobFactory
    {
        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
        {
            var db = fixture.CreateContext();
            var lifecycle = new LocationImportLifecycle(db, scheduler, NullLogger<LocationImportLifecycle>.Instance);
            return new LocationImportJob(service, NullLogger<LocationImportJob>.Instance, lifecycle);
        }
        public void ReturnJob(IJob job) { }
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
    { await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string schema, string table)
    { await using var command = connection.CreateCommand(); command.CommandText = $"SELECT count(*) FROM {schema}.{table}"; return (long)(await command.ExecuteScalarAsync())!; }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    { using var cancellation = new CancellationTokenSource(timeout); while (!await condition()) { cancellation.Token.ThrowIfCancellationRequested(); await Task.Yield(); } }

    private static async Task DisposeSchedulerAsync(IScheduler scheduler)
    { if (scheduler is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync(); else if (scheduler is IDisposable disposable) disposable.Dispose(); }
}
