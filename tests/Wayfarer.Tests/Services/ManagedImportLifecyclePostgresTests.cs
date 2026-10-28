using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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

    private async Task<ManagedImportTestScope> ScopeAsync()
    {
        var scope = new ManagedImportTestScope(fixture);
        await scope.InitializeAsync();
        return scope;
    }
}
