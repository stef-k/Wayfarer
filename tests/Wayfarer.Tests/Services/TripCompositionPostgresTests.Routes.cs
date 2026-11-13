using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

public sealed partial class TripCompositionPostgresTests
{
    /// <summary>Shared approved Places are flushed before route staging; exact measurements/provenance survive compatible copying.</summary>
    [PostgresFact]
    public async Task SelectedRoutesShareApprovedDependenciesAndPreserveExactSavedMeasurements()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext();
        var second = new Segment { Id = Guid.NewGuid(), TripId = b.Id, UserId = b.UserId, Title = "Alternative", Mode = "walk",
            FromPlaceId = b.Segments.Single().FromPlaceId, ToPlaceId = b.Segments.Single().ToPlaceId,
            RouteGeometry = (LineString)b.Segments.Single().RouteGeometry!.Copy(), EstimatedDistanceKm = 4.123456789,
            EstimatedDuration = TimeSpan.FromTicks(7777777), EstimatedDurationSource = EstimatedDurationSource.Manual };
        db.Segments.Add(second); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        b = await Load(db, b.Id);
        var created = await StartSelection(db, a, b); var c = await Load(db, created.TripId); var region = c.Regions.Single();
        var placement = new TripCompositionPlacement(region.Places.OrderBy(p => p.DisplayOrder.HasValue ? 0 : 1).ThenBy(p => p.DisplayOrder).ThenBy(p => p.Id).Select(p => new TripCompositionOrderOriginal(p.Id, p.DisplayOrder)).ToArray());
        var copies = b.Regions.Single().Places.Where(p => p.Location != null).Select(p => new TripCompositionItem("place", p.Id, "copy", RegionId: region.Id,
            Name: p.Name + " B", Placement: placement)).ToList();
        var segmentPlacement = new TripCompositionPlacement(c.Segments.OrderBy(s => s.DisplayOrder).ThenBy(s => s.Id).Select(s => new TripCompositionOrderOriginal(s.Id, s.DisplayOrder)).ToArray(), "start");
        foreach (var s in b.Segments)
        {
            var anchors = s.Waypoints.Select(w => w.PlaceId).Concat(new[] { s.FromPlaceId, s.ToPlaceId }.OfType<Guid>()).Distinct()
                .Select(id => new TripCompositionAnchor(id, null, null, null)).ToArray();
            copies.Add(new("segment", s.Id, "copy", Name: s.Title + " B", Anchors: anchors, Placement: segmentPlacement));
        }
        await Select(db, a.UserId, created, "b", copies);
        c = await Load(db, c.Id);
        var copied = c.Segments.Where(s => s.Title!.EndsWith(" B")).ToArray(); Assert.Equal(2, copied.Length);
        Assert.Single(copied.Select(s => s.FromPlaceId).Distinct()); Assert.Single(copied.Select(s => s.ToPlaceId).Distinct());
        foreach (var s in b.Segments)
        {
            var saved = copied.Single(x => x.Title == s.Title + " B");
            Assert.Equal(s.EstimatedDuration, saved.EstimatedDuration); Assert.Equal(s.EstimatedDistanceKm, saved.EstimatedDistanceKm);
            Assert.Equal(s.RouteGeometry!.AsText(), saved.RouteGeometry!.AsText()); Assert.Equal(s.EstimatedDurationSource, saved.EstimatedDurationSource);
        }
        Assert.Single(copied.Single(s => s.Title == "Precise route B").Waypoints);
    }

    [PostgresTheory]
    [InlineData(false, 0.00001, false)]
    [InlineData(true, 0.00001, false)]
    [InlineData(false, 0.00000001, true)]
    [InlineData(true, 0.00000001, false)]
    public async Task ZeroViaCustomAndFallbackMappingCompatibilityIsSeparateFromSemanticDistance(bool fallback, double offset, bool compatible)
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext();
        var sourceSegment = b.Segments.Single();
        await db.Set<SegmentWaypoint>().Where(w => w.SegmentId == sourceSegment.Id).ExecuteDeleteAsync();
        if (fallback) await db.Segments.Where(s => s.Id == sourceSegment.Id).ExecuteUpdateAsync(s => s.SetProperty(s => s.RouteGeometry, (LineString?)null));
        var created = await StartSelection(db, a, b); var c = await Load(db, created.TripId);
        var target = c.Regions.Single().Places.Single(p => p.Name == "Start");
        await db.Places.Where(p => p.Id == target.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Location, new Point(12 + offset, 40) { SRID = 4326 }));
        c = await Load(db, c.Id); var places = c.Regions.Single().Places.ToDictionary(p => p.Name);
        var anchors = b.Regions.Single().Places.Where(p => p.Id == sourceSegment.FromPlaceId || p.Id == sourceSegment.ToPlaceId)
            .Select(p => new TripCompositionAnchor(p.Id, places[p.Name].Id, places[p.Name].RegionId, [places[p.Name].Location!.X, places[p.Name].Location!.Y])).ToArray();
        var before = await db.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == created.CompositionId);
        var attempt = Select(db, a.UserId, created, "b", [new("segment", sourceSegment.Id, "copy", Name: "Offset",
            Anchors: anchors, Placement: new(c.Segments.OrderBy(s => s.DisplayOrder).ThenBy(s => s.Id).Select(s => new TripCompositionOrderOriginal(s.Id, s.DisplayOrder)).ToArray()))]);
        if (compatible)
        {
            await attempt;
            Assert.Equal(sourceSegment.EstimatedDuration, (await Load(db, c.Id)).Segments.Single(s => s.Title == "Offset").EstimatedDuration);
            return;
        }
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => attempt);
        Assert.Single((await Load(db, c.Id)).Segments); Assert.Equal(before.Mappings, (await db.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == created.CompositionId)).Mappings);
    }

    /// <summary>A late injected route failure proves rollback of dependency rows, credits, mappings and order.</summary>
    [PostgresFact]
    public async Task SelectiveLateRouteFailureRollsBackWholeAction()
    {
        var a = await Seed(); var b = await Seed();
        TripConstructionResult created;
        await using (var setup = fixture.CreateContext()) created = await StartSelection(setup, a, b);
        await using var db = fixture.CreateContext(new FailSelectedRoute());
        var c = await Load(db, created.TripId); var region = c.Regions.Single();
        var placement = new TripCompositionPlacement(region.Places.OrderBy(p => p.DisplayOrder.HasValue ? 0 : 1).ThenBy(p => p.DisplayOrder).ThenBy(p => p.Id).Select(p => new TripCompositionOrderOriginal(p.Id, p.DisplayOrder)).ToArray());
        var copies = b.Regions.Single().Places.Where(p => p.Location != null)
            .Select(p => new TripCompositionItem("place", p.Id, "copy", RegionId: region.Id, Name: p.Name + " B", Placement: placement)).ToList();
        copies.Add(new("segment", b.Segments.Single().Id, "copy", Name: "Late", Anchors: b.Regions.Single().Places.Where(p => p.Location != null)
            .Select(p => new TripCompositionAnchor(p.Id, null, null, null)).ToArray(), Placement: new(c.Segments.OrderBy(s => s.DisplayOrder).ThenBy(s => s.Id).Select(s => new TripCompositionOrderOriginal(s.Id, s.DisplayOrder)).ToArray())));
        var workspace = await db.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == created.CompositionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Select(db, a.UserId, created, "b", copies));
        var after = await Load(db, c.Id);
        Assert.Equal(c.Regions.Single().Places.Count, after.Regions.Single().Places.Count); Assert.Single(after.Segments);
        Assert.Equal(c.Credits, after.Credits); Assert.Equal(c.UpdatedAt, after.UpdatedAt);
        Assert.Equal(workspace.Mappings, (await db.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == created.CompositionId)).Mappings);
    }

    private sealed class FailSelectedRoute : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Segment>().Any(e => e.State == EntityState.Added))
                throw new InvalidOperationException("Injected late selected route failure");
            return ValueTask.FromResult(result);
        }
    }
}
