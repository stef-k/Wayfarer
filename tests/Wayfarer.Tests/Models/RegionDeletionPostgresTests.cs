using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Qualifies the four-way contract through the real PostgreSQL lifecycle, including projection and recovery.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class RegionDeletionPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>All combinations preserve exact authored identities/content or apply canonical route reconciliation.</summary>
    [PostgresTheory]
    [InlineData("keep", "keep")]
    [InlineData("keep", "delete")]
    [InlineData("delete", "keep")]
    [InlineData("delete", "delete")]
    public async Task MixedGraph_AllFourOutcomes(string places, string areas)
    {
        var graph = await RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext();
        var service = new PlaceRegionLifecycleService(db, RegionDeletionTestGraph.Confirmation());
        var beforeSegments = await db.Segments.AsNoTracking().Include(s => s.Waypoints).Where(s => s.TripId == graph.TripId).ToArrayAsync();
        var beforePlaces = await db.Places.AsNoTracking().Where(p => graph.PlaceIds.Contains(p.Id)).ToArrayAsync();
        var beforeAreas = await db.Areas.AsNoTracking().Where(a => graph.AreaIds.Contains(a.Id)).ToArrayAsync();
        var preview = await service.PreviewRegionDeletionAsync(graph.TripId, graph.SourceId, graph.UserId, new(places, areas), default);
        Assert.Equal(graph.DestinationId, preview.Destination.RegionId ?? graph.DestinationId);
        Assert.False(db.ChangeTracker.HasChanges());
        var result = await service.ConfirmRegionDeletionAsync(graph.TripId, graph.SourceId, graph.UserId,
            new(places, areas, preview.Confirmation.Token), default);
        await using var verify = fixture.CreateContext();
        Assert.False(await verify.Regions.AnyAsync(r => r.Id == graph.SourceId));
        Assert.Equal(places == "keep" ? graph.PlaceIds.Order() : [], result.Data.MovedPlaceIds);
        Assert.Equal(areas == "keep" ? graph.AreaIds.Order() : [], result.Data.MovedAreaIds);
        Assert.Equal(places == "delete" ? graph.PlaceIds.Order() : [], result.DeletedIds.Places);
        Assert.Equal(areas == "delete" ? graph.AreaIds.Order() : [], result.DeletedIds.Areas);
        Assert.Equal(places == "delete" ? graph.EndpointIds.Order() : [], result.DeletedIds.Segments);
        if (places == "keep")
        {
            foreach (var original in beforePlaces)
            {
                var saved = await verify.Places.AsNoTracking().SingleAsync(p => p.Id == original.Id);
                Assert.Equal(graph.DestinationId, saved.RegionId);
                Assert.Equal(original.Name, saved.Name);
                Assert.Equal(original.Notes, saved.Notes);
                Assert.Equal(original.Address, saved.Address);
                Assert.Equal(original.IconName, saved.IconName);
                Assert.Equal(original.MarkerColor, saved.MarkerColor);
                Assert.Equal(original.Location!.AsBinary(), saved.Location!.AsBinary());
            }
            Assert.Equal(new[] { 1, 2, 3 }, await verify.Places.Where(p => p.RegionId == graph.DestinationId)
                .OrderBy(p => p.DisplayOrder).Select(p => p.DisplayOrder!.Value).ToArrayAsync());
        }
        if (areas == "keep")
        {
            foreach (var original in beforeAreas)
            {
                var saved = await verify.Areas.AsNoTracking().SingleAsync(a => a.Id == original.Id);
                Assert.Equal(graph.DestinationId, saved.RegionId); Assert.Equal(original.Geometry.AsBinary(), saved.Geometry.AsBinary());
                Assert.Equal(4326, saved.Geometry.SRID); Assert.Equal(1, saved.Geometry.NumInteriorRings);
                Assert.Equal(original.Notes, saved.Notes); Assert.Equal(original.FillHex, saved.FillHex); Assert.Equal(original.Name, saved.Name);
            }
            Assert.Equal(new[] { 1, 2, 3 }, await verify.Areas.Where(a => a.RegionId == graph.DestinationId).OrderBy(a => a.DisplayOrder).Select(a => a.DisplayOrder!.Value).ToArrayAsync());
        }
        foreach (var original in beforeSegments)
        {
            var saved = await verify.Segments.AsNoTracking().Include(s => s.Waypoints).SingleOrDefaultAsync(s => s.Id == original.Id);
            if (places == "keep")
            {
                Assert.NotNull(saved); Assert.Equal(original.RowVersion, saved.RowVersion);
                Assert.Equal(original.RouteGeometry?.AsBinary(), saved.RouteGeometry?.AsBinary());
                Assert.Equal(original.EstimatedDistanceKm, saved.EstimatedDistanceKm); Assert.Equal(original.EstimatedDuration, saved.EstimatedDuration);
                Assert.Equal(original.RouteProvider, saved.RouteProvider); Assert.Equal(original.RouteInstructionsJson, saved.RouteInstructionsJson);
                Assert.Equal(original.Waypoints.OrderBy(w => w.Position).Select(w => (w.PlaceId, w.Position, w.RouteVertexIndex)),
                    saved.Waypoints.OrderBy(w => w.Position).Select(w => (w.PlaceId, w.Position, w.RouteVertexIndex)));
            }
            else if (graph.EndpointIds.Contains(original.Id)) Assert.Null(saved);
            else { Assert.NotNull(saved); Assert.Empty(saved.Waypoints); Assert.Equal(original.RouteGeometry?.AsBinary(), saved.RouteGeometry?.AsBinary()); }
        }
        Assert.Equal(new[] { 0, 1 }, await verify.Regions.Where(r => r.TripId == graph.TripId).OrderBy(r => r.DisplayOrder).Select(r => r.DisplayOrder).ToArrayAsync());
        var visit = await verify.PlaceVisitEvents.SingleAsync(v => v.TripIdSnapshot == graph.TripId);
        Assert.Equal(places == "keep" ? graph.PlaceIds[0] : (Guid?)null, visit.PlaceId);
        Assert.Equal("Visit notes", visit.NotesHtml); Assert.Equal("Delete source", visit.RegionNameSnapshot);
        Assert.NotNull(result.Affected.VisitProgress); Assert.NotNull(result.Affected.Metadata);
    }

    /// <summary>Zero children still need a token; absent destinations are created only for actual kept children at commit.</summary>
    [PostgresTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ChildShapes_AbsentDestinationAndMandatoryConfirmation(bool places, bool areas)
    {
        var g = await RegionDeletionTestGraph.SeedAsync(fixture, false, places, areas);
        await using var db = fixture.CreateContext();
        var service = new PlaceRegionLifecycleService(db, RegionDeletionTestGraph.Confirmation());
        var required = await Assert.ThrowsAsync<RegionDeletionRejectedException>(() => service.ConfirmRegionDeletionAsync(g.TripId, g.SourceId, g.UserId, new("keep", "keep", null), default));
        Assert.Equal("region-deletion-confirmation-required", required.Code);
        var preview = required.Preview!;
        Assert.Equal(places || areas ? "create" : "not-required", preview.Destination.Mode);
        Assert.False(await db.Regions.AnyAsync(r => r.TripId == g.TripId && r.Name == "Unassigned Places"));
        var result = await service.ConfirmRegionDeletionAsync(g.TripId, g.SourceId, g.UserId, new("keep", "keep", preview.Confirmation.Token), default);
        Assert.Equal(preview.Destination.RegionId, result.Data.DestinationRegionId);
        Assert.Equal(places || areas, await db.Regions.AnyAsync(r => r.TripId == g.TripId && r.Name == "Unassigned Places"));
    }

    /// <summary>A failure after SQL rolls back all rows and leaves caller-owned pending work safe for a later save.</summary>
    [PostgresFact]
    public async Task PostSqlFailure_RollsBackMovesRoutesOrdersDestinationAndTracker()
    {
        var g = await RegionDeletionTestGraph.SeedAsync(fixture, false);
        var failure = new PostSqlFailure();
        await using var db = fixture.CreateContext(failure);
        var trip = await db.Trips.SingleAsync(t => t.Id == g.TripId);
        var pending = new Tag { Id = Guid.NewGuid(), Name = "pending-" + Guid.NewGuid().ToString("N"), Slug = "pending-" + Guid.NewGuid().ToString("N") };
        fixture.RegisterTag(pending); db.Tags.Add(pending);
        var service = new PlaceRegionLifecycleService(db, RegionDeletionTestGraph.Confirmation());
        var preview = await service.PreviewRegionDeletionAsync(g.TripId, g.SourceId, g.UserId, new("delete", "keep"), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmRegionDeletionAsync(g.TripId, g.SourceId, g.UserId, new("delete", "keep", preview.Confirmation.Token), default));
        Assert.Same(pending, db.Tags.Local.Single(t => t.Id == pending.Id));
        failure.Enabled = false;
        await db.SaveChangesAsync();
        await using var verify = fixture.CreateContext();
        Assert.True(await verify.Regions.AnyAsync(r => r.Id == g.SourceId));
        Assert.False(await verify.Regions.AnyAsync(r => r.Id == preview.Destination.RegionId));
        Assert.Equal(2, await verify.Places.CountAsync(p => p.RegionId == g.SourceId));
        Assert.Equal(2, await verify.Areas.CountAsync(a => a.RegionId == g.SourceId));
        Assert.Equal(4, await verify.Segments.CountAsync(s => s.TripId == g.TripId));
        Assert.Equal(4, await verify.Set<SegmentWaypoint>().CountAsync(w => g.SurvivorIds.Contains(w.SegmentId) || g.EndpointIds.Contains(w.SegmentId)));
        Assert.True(await verify.Tags.AnyAsync(t => t.Id == pending.Id));
    }

    private sealed class PostSqlFailure : SaveChangesInterceptor
    {
        internal bool Enabled = true;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken token = default)
        {
            if (Enabled) throw new InvalidOperationException("Injected pre-commit failure after SQL.");
            return ValueTask.FromResult(result);
        }
    }
}
