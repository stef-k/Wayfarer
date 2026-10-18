using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Real persisted content/membership edits while the production lock path waits invalidate approval.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class RegionDeletionConcurrencyPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Same IDs/counts cannot conceal authored, route, destination or ordering changes.</summary>
    [PostgresTheory]
    [InlineData("place")]
    [InlineData("area")]
    [InlineData("region")]
    [InlineData("segment")]
    [InlineData("insert")]
    [InlineData("move")]
    [InlineData("delete")]
    [InlineData("order")]
    [InlineData("destination-order")]
    [InlineData("waypoint")]
    [InlineData("destination-create")]
    public async Task PersistedChangeDuringLockWait_IsStaleAndMakesNoPartialChanges(string change)
    {
        var g = await RegionDeletionTestGraph.SeedAsync(fixture, change != "destination-create");
        var gate = new LockGate();
        await using var db = fixture.CreateContext(gate);
        var service = new PlaceRegionLifecycleService(db, RegionDeletionTestGraph.Confirmation());
        var preview = await service.PreviewRegionDeletionAsync(g.TripId, g.SourceId, g.UserId, new("keep", "keep"), default);
        var attempt = service.ConfirmRegionDeletionAsync(g.TripId, g.SourceId, g.UserId, new("keep", "keep", preview.Confirmation.Token), default);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            await using var writer = fixture.CreateContext();
            switch (change)
            {
                case "place": await writer.Places.Where(p => p.Id == g.PlaceIds[0]).ExecuteUpdateAsync(s => s.SetProperty(p => p.Notes, "edited note")); break;
                case "area":
                    var area = await writer.Areas.SingleAsync(a => a.Id == g.AreaIds[0]);
                    area.Geometry = (NetTopologySuite.Geometries.Polygon)area.Geometry.Copy();
                    area.Geometry.ExteriorRing.CoordinateSequence.SetOrdinate(1, NetTopologySuite.Geometries.Ordinate.Y, 7);
                    area.Geometry.GeometryChanged();
                    await writer.SaveChangesAsync();
                    break;
                case "region": await writer.Regions.Where(r => r.Id == g.SourceId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Notes, "edited region")); break;
                case "segment": await writer.Segments.Where(s => s.Id == g.SurvivorIds[0]).ExecuteUpdateAsync(s => s.SetProperty(s => s.RouteGeometry, new NetTopologySuite.Geometries.LineString([new(1,1),new(2,2),new(3,3),new(5,5)]) { SRID = 4326 })); break;
                case "insert": writer.Areas.Add(new Area { Id = Guid.NewGuid(), RegionId = g.SourceId, Name = "Inserted", Geometry = (await writer.Areas.AsNoTracking().FirstAsync(a => a.RegionId == g.SourceId)).Geometry }); await writer.SaveChangesAsync(); break;
                case "move": await writer.Places.Where(p => p.Id == g.OutsidePlaceId).ExecuteUpdateAsync(s => s.SetProperty(p => p.RegionId, g.SourceId)); break;
                case "delete": await writer.Areas.Where(a => a.Id == g.AreaIds[0]).ExecuteDeleteAsync(); break;
                case "order": await writer.Regions.Where(r => r.TripId == g.TripId && r.Id != g.SourceId).ExecuteUpdateAsync(s => s.SetProperty(r => r.DisplayOrder, 22)); break;
                case "destination-order": await writer.Areas.Where(a => a.RegionId == g.DestinationId).ExecuteUpdateAsync(s => s.SetProperty(a => a.DisplayOrder, 29)); break;
                case "waypoint": await writer.Set<SegmentWaypoint>().Where(w => w.SegmentId == g.SurvivorIds[0]).ExecuteUpdateAsync(s => s.SetProperty(w => w.RouteVertexIndex, 1)); break;
                case "destination-create": writer.Regions.Add(new Region { Id = Guid.NewGuid(), TripId = g.TripId, UserId = g.UserId, Name = "Unassigned Places" }); await writer.SaveChangesAsync(); break;
            }
        }
        finally { gate.Release.TrySetResult(); }
        var error = await Assert.ThrowsAsync<RegionDeletionRejectedException>(() => attempt);
        Assert.Equal("region-deletion-preview-stale", error.Code);
        await using var verify = fixture.CreateContext();
        Assert.True(await verify.Regions.AnyAsync(r => r.Id == g.SourceId));
        Assert.Equal(2, await verify.Places.CountAsync(p => g.PlaceIds.Contains(p.Id) && p.RegionId == g.SourceId));
        Assert.Equal(4, await verify.Segments.CountAsync(s => s.TripId == g.TripId));
    }

    /// <summary>A stale tracked Trip is never stamped over an independent persisted metadata edit.</summary>
    [PostgresFact]
    public async Task IndependentTripMetadataEdit_SurvivesNarrowTimestampSave()
    {
        var g = await RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext();
        await db.Trips.SingleAsync(t => t.Id == g.TripId);
        var service = new PlaceRegionLifecycleService(db, RegionDeletionTestGraph.Confirmation());
        var preview = await service.PreviewRegionDeletionAsync(g.TripId, g.SourceId, g.UserId, new("keep", "keep"), default);
        await using (var writer = fixture.CreateContext()) await writer.Trips.Where(t => t.Id == g.TripId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Name, "New independent title").SetProperty(t => t.Notes, "New independent notes"));
        var result = await service.ConfirmRegionDeletionAsync(g.TripId, g.SourceId, g.UserId, new("keep", "keep", preview.Confirmation.Token), default);
        Assert.Equal("New independent title", result.Affected.Metadata!.Name);
        await using var verify = fixture.CreateContext();
        Assert.Equal("New independent notes", (await verify.Trips.SingleAsync(t => t.Id == g.TripId)).Notes);
    }

    /// <summary>Pauses before a real parent lock so an insertion can commit before final rediscovery.</summary>
    [PostgresFact]
    public async Task AreaInsertionAtFinalRegionBarrier_IsDetected()
    {
        var g = await RegionDeletionTestGraph.SeedAsync(fixture);
        var gate = new LockGate("\"Regions\"");
        await using var db = fixture.CreateContext(gate);
        var service = new PlaceRegionLifecycleService(db, RegionDeletionTestGraph.Confirmation());
        var preview = await service.PreviewRegionDeletionAsync(g.TripId, g.SourceId, g.UserId, new("keep", "keep"), default);
        var attempt = service.ConfirmRegionDeletionAsync(g.TripId, g.SourceId, g.UserId, new("keep", "keep", preview.Confirmation.Token), default);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            await using var writer = fixture.CreateContext();
            writer.Areas.Add(new Area { Id = Guid.NewGuid(), RegionId = g.SourceId, Name = "Final barrier insertion",
                Geometry = (await writer.Areas.AsNoTracking().FirstAsync(a => a.RegionId == g.SourceId)).Geometry });
            await writer.SaveChangesAsync();
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal("region-deletion-preview-stale", (await Assert.ThrowsAsync<RegionDeletionRejectedException>(() => attempt)).Code);
        Assert.Equal(3, await db.Areas.CountAsync(a => a.RegionId == g.SourceId));
    }

    private sealed class LockGate(string? table = null) : DbCommandInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<int> result, CancellationToken token = default)
        {
            if (!Entered.Task.IsCompleted && command.CommandText.Contains("FOR UPDATE") && (table == null || command.CommandText.Contains(table)))
            { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
            return result;
        }
    }
}
