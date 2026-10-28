using System.Collections.Specialized;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Exercises shared Web/bearer controls, terminal recovery and actual native parser/worker seams.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class ManagedImportLifecyclePostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresFact]
    public async Task LostStartReplyAfterFastCompletion_RecoversSameEpochAndStaleStopCannotStopResume()
    {
        await using var scope = await ScopeAsync();
        var row = await scope.AcceptAsync();
        var id = row.ImportId!.Value;
        Assert.True(await scope.Lifecycle.StartAsync(scope.User.Id, id, 1, scope.Credential, default));
        await scope.DueAsync(row, ImportStatus.Stopped);
        Assert.False(await scope.Lifecycle.StartAsync(scope.User.Id, id, 1, scope.Credential, default));
        Assert.Equal(1, (await scope.ReadAsync(row.Id)).Import!.ExecutionEpoch);
        Assert.True(await scope.Lifecycle.StartAsync(scope.User.Id, id, 2, scope.Credential, default));
        var stale = await Assert.ThrowsAsync<ManagedImportException>(() => scope.Lifecycle.StopAsync(scope.User.Id, id, 1,
            scope.Credential, default));
        Assert.Equal("execution_epoch_conflict", stale.Code);
        Assert.Equal(ImportStatus.InProgress, (await scope.ReadAsync(row.Id)).Import!.Status);
        await scope.DueAsync(row, ImportStatus.Completed);
        Assert.False(await scope.Lifecycle.StartAsync(scope.User.Id, id, 2, scope.Credential, default));
        await Assert.ThrowsAsync<ManagedImportException>(() => scope.Lifecycle.StartAsync(scope.User.Id, id, 3,
            scope.Credential, default));
        Assert.False(await scope.Lifecycle.StopAsync(scope.User.Id, id, 2, scope.Credential, default));
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedWebStartAndCleanup_RespectWinningCustodyClaim(bool startWins)
    {
        await using var scope = await ScopeAsync();
        var row = await scope.AcceptAsync();
        await scope.DueAsync(row, ImportStatus.Completed);
        var shared = new LocationImportLifecycle(scope.Files, scope.Factory, scope.Scheduler.Object,
            NullLogger<LocationImportLifecycle>.Instance, managedLifecycle: scope.Lifecycle);
        if (startWins)
        {
            Assert.True((await shared.StartAsync(scope.User.Id, row.ImportId!.Value)).Succeeded);
            await scope.Cleanup.ProcessAsync(row.Id, default);
            Assert.Equal(ManagedImportFileState.Retained, (await scope.ReadAsync(row.Id)).FileState);
            Assert.Equal(ImportStatus.InProgress, (await scope.ReadAsync(row.Id)).Import!.Status);
        }
        else
        {
            await scope.Cleanup.ProcessAsync(row.Id, default);
            Assert.False((await shared.StartAsync(scope.User.Id, row.ImportId!.Value)).Succeeded);
            Assert.Equal(ManagedImportFileState.Removed, (await scope.ReadAsync(row.Id)).FileState);
            Assert.True((await shared.DeleteAsync(scope.User.Id, row.ImportId.Value)).Succeeded);
            var retired = await scope.ReadAsync(row.Id);
            Assert.NotNull(retired.RetiredAtUtc);
            Assert.Equal(410, ManagedImportStatus.HttpStatus(retired));
            Assert.Null(retired.ImportId);
        }
    }

    [PostgresTheory]
    [InlineData("Stopped")]
    [InlineData("Failed")]
    public async Task MaintenanceRetainsResumableFileCheckpointAndPendingHandoff(string state)
    {
        await using var scope = await ScopeAsync();
        var row = await scope.AcceptAsync();
        await scope.DueAsync(row, new ImportStatus(state));
        await using (var db = fixture.CreateContext())
        {
            var import = await db.LocationImports.FindAsync(row.ImportId);
            import!.LastProcessedIndex = 17;
            import.TotalRecords = 51;
            import.EnrichmentHandoffPending = true;
            await db.SaveChangesAsync();
        }
        await scope.Cleanup.MaintainAsync(default);
        var retained = await scope.ReadAsync(row.Id);
        Assert.Equal(ManagedImportFileState.Retained, retained.FileState);
        Assert.Equal(17, retained.Import!.LastProcessedIndex);
        Assert.True(retained.Import.EnrichmentHandoffPending);
        Assert.True(await scope.Lifecycle.StartAsync(scope.User.Id, row.ImportId!.Value, 1, scope.Credential, default));
        Assert.Equal(17, (await scope.ReadAsync(row.Id)).Import!.LastProcessedIndex);
        await scope.DueAsync(row, ImportStatus.Completed);
        await scope.Cleanup.ProcessAsync(row.Id, default);
        Assert.Equal(ManagedImportFileState.Removed, (await scope.ReadAsync(row.Id)).FileState);
        Assert.False(await scope.Lifecycle.DeleteAsync(scope.User.Id, row.ImportId.Value, scope.Credential, default));
        Assert.True((await scope.ReadAsync(row.Id)).Import!.EnrichmentHandoffPending);
    }

    [PostgresTheory]
    [InlineData("csv", "Latitude,Longitude,TimestampUtc,IdempotencyKey\n1,2,2026-10-01T00:00:00Z,498b2e37-856b-426a-bde6-481414d3dd4e\n")]
    [InlineData("wayfarer-geojson", "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[2,1]},\"properties\":{\"TimestampUtc\":\"2026-10-01T00:00:00Z\",\"IdempotencyKey\":\"498b2e37-856b-426a-bde6-481414d3dd4e\"}}]}")]
    public async Task ManagedNativeFiles_ProcessThroughExistingImporterAndFileOnlyCleanupPreservesLocations(string format, string text)
    {
        await using var scope = await ScopeAsync();
        var row = await scope.AcceptAsync(text, format);
        await scope.Lifecycle.StartAsync(scope.User.Id, row.ImportId!.Value, 1, scope.Credential, default);
        var worker = new LocationImportService(scope.Files, scope.Factory,
            new ReverseGeocodingService(new HttpClient(), NullLogger<Wayfarer.Areas.Api.Controllers.BaseApiController>.Instance),
            NullLogger<LocationImportService>.Instance, new LocationDataParserFactory(NullLoggerFactory.Instance), new SseService());
        await worker.ProcessImport(row.ImportId.Value, 1, default);
        Assert.Equal(ImportStatus.Completed, (await scope.ReadAsync(row.Id)).Import!.Status);
        await scope.DueAsync(row);
        await scope.Cleanup.ProcessAsync(row.Id, default);
        await using var db = fixture.CreateContext();
        var location = Assert.Single(await db.Locations.Where(item => item.UserId == scope.User.Id).ToListAsync());
        Assert.Equal(Guid.Parse("498b2e37-856b-426a-bde6-481414d3dd4e"), location.IdempotencyKey);
        Assert.Equal(ManagedImportFileState.Removed, (await scope.ReadAsync(row.Id)).FileState);
        Assert.Equal(1, (await scope.ReadAsync(row.Id)).Import!.LastProcessedIndex);
    }

    /// <summary>Pauses cleanup after its stopped-state read, then lets a real scheduler accept Start before cleanup continues.</summary>
    [PostgresFact]
    public async Task CleanupStaleQuiescence_MustPreserveNewStartProjection()
    {
        await using var scope = new ManagedImportTestScope(fixture);
        await scope.InitializeAsync();
        var row = await scope.AcceptAsync();
        await scope.DueAsync(row, ImportStatus.Stopped);
        var real = await new StdSchedulerFactory(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = "ManagedImportRace-" + Guid.NewGuid().ToString("N"),
            ["quartz.threadPool.threadCount"] = "1",
            ["quartz.jobStore.type"] = "Quartz.Simpl.RAMJobStore, Quartz"
        }).GetScheduler();
        // Standby is intentional: a queued Start is a supported scheduler state and cannot complete before the race.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proxy = new Mock<IScheduler>();
        proxy.Setup(s => s.GetCurrentlyExecutingJobs(It.IsAny<CancellationToken>())).Returns(async (CancellationToken token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return await real.GetCurrentlyExecutingJobs(token);
        });
        proxy.Setup(s => s.GetJobKeys(It.IsAny<GroupMatcher<JobKey>>(), It.IsAny<CancellationToken>()))
            .Returns((GroupMatcher<JobKey> group, CancellationToken token) => real.GetJobKeys(group, token));
        proxy.Setup(s => s.DeleteJob(It.IsAny<JobKey>(), It.IsAny<CancellationToken>()))
            .Returns((JobKey key, CancellationToken token) => real.DeleteJob(key, token));
        var cleanup = new ManagedImportCleanup(scope.Factory, scope.Files, scope.Gate, proxy.Object, NullLogger<ManagedImportCleanup>.Instance);
        var lifecycle = new ManagedImportLifecycle(scope.Factory, scope.Files, real, NullLogger<LocationImportLifecycle>.Instance, cleanup);
        var removal = cleanup.MaintainAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(await lifecycle.StartAsync(scope.User.Id, row.ImportId!.Value, 1, scope.Credential, default));
            var key = LocationImportSchedulerKeys.Job(row.ImportId.Value, 1);
            Assert.True(await real.CheckExists(key));
            Assert.False((await scope.ReadAsync(row.Id)).Import!.ProjectionPending);
            release.TrySetResult();
            await removal;
            Assert.True(await real.CheckExists(key), "Cleanup must preserve the winning Start before any repair.");
            var current = await scope.ReadAsync(row.Id);
            var reconciler = new LocationImportReconciler(scope.Files, scope.Factory, real, NullLogger<LocationImportReconciler>.Instance, managedCleanup: cleanup);
            await reconciler.ReconcileAsync(managedOnly: true);
            Assert.Equal(ImportStatus.InProgress, current.Import.Status);
            Assert.Equal(ManagedImportFileState.Retained, current.FileState);
            Assert.True(await real.CheckExists(key));
            // A genuinely lost scheduler projection can be recovered without allocating a second epoch.
            await real.DeleteJob(key);
            Assert.False(await lifecycle.StartAsync(scope.User.Id, row.ImportId.Value, 1, scope.Credential, default));
            Assert.True(await real.CheckExists(key));
            Assert.Equal(1, (await scope.ReadAsync(row.Id)).Import!.ExecutionEpoch);
            // Cleanup winning custody prevents both Web-style restart and any new scheduler projection.
            await scope.DueAsync(row, ImportStatus.Completed);
            await cleanup.ProcessAsync(row.Id, default);
            var denied = await Assert.ThrowsAsync<ManagedImportException>(() =>
                lifecycle.StartAsync(scope.User.Id, row.ImportId.Value, null, null, default));
            Assert.Equal("file_cleanup_pending_or_removed", denied.Code);
            Assert.Equal(ManagedImportFileState.Removed, (await scope.ReadAsync(row.Id)).FileState);
            Assert.False(await real.CheckExists(key));
        }
        finally { release.TrySetResult(); await removal; await real.Shutdown(); }
    }

    private async Task<ManagedImportTestScope> ScopeAsync()
    {
        var scope = new ManagedImportTestScope(fixture);
        await scope.InitializeAsync();
        return scope;
    }
}
