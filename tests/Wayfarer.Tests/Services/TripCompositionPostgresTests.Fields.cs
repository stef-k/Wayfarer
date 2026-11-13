using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

public sealed partial class TripCompositionPostgresTests
{
    /// <summary>Address source authority is captured; authored edits cannot supply provider claims.</summary>
    [PostgresFact]
    public async Task SelectiveAddressGeometryAndTripMetadataHaveIndependentOriginals()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext(); var sourcePlace = b.Regions.Single().Places.First();
        await db.Places.Where(p => p.Id == sourcePlace.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Address, "Captured address")
            .SetProperty(p => p.ResolvedFeatureName, "Building").SetProperty(p => p.ResolvedFeatureType, "building")
            .SetProperty(p => p.AddressEnrichmentProvider, "geoapify").SetProperty(p => p.AddressEnrichmentStorageMode, "persistent")
            .SetProperty(p => p.AddressEnrichedAt, DateTimeOffset.Parse("2026-01-01T00:00:00Z")));
        var created = await StartSelection(db, a, b); var c = await Load(db, created.TripId);
        var target = c.Regions.Single().Places.Single(p => p.Name == "Start");
        await Select(db, a.UserId, created, "b", [new("place", sourcePlace.Id, "reuse", DestinationId: target.Id, ExpectedParentId: target.RegionId,
            Fields: new(Address: new("useSource", E(TripCompositionFieldValues.Current(target, "address"))))) ]);
        var saved = await db.Places.AsNoTracking().SingleAsync(p => p.Id == target.Id);
        Assert.Equal("geoapify", saved.AddressEnrichmentProvider); Assert.Equal("Building", saved.ResolvedFeatureName);
        Assert.Equal(target.Location!.AsText(), saved.Location!.AsText());
        var forged = new TripCompositionItem("place", sourcePlace.Id, "reuse", DestinationId: target.Id, ExpectedParentId: target.RegionId,
            ExpectedDecision: new(target.Id, "reuse"), Fields: new(Address: new("edit", E(TripCompositionFieldValues.Current(saved, "address")),
                E(new { address = "Forged", addressEnrichmentProvider = "geoapify" }))));
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => Select(db, a.UserId, created, "b", [forged]));
        await Select(db, a.UserId, created, "b", [forged with { Fields = new(Address: new("edit", E(TripCompositionFieldValues.Current(saved, "address")), E(new { address = "Manual" }))) }]);
        saved = await db.Places.AsNoTracking().SingleAsync(p => p.Id == target.Id);
        Assert.Equal("Manual", saved.Address); Assert.Null(saved.AddressEnrichedAt); Assert.Null(saved.AddressEnrichmentProvider);
        c = await Load(db, c.Id);
        await Select(db, a.UserId, created, "b", [new("trip", b.Id, "reuse", DestinationId: c.Id,
            Fields: new(Notes: new("edit", E(c.Notes), E("<p>Independent Notes</p>")),
                Tags: new("useSource", E(c.Tags.OrderBy(t => t.Slug).Select(t => t.Slug).ToArray())),
                Viewport: new("edit", E(TripCompositionFieldValues.Current(c, "viewport")), E(new { centerLat = (double?)25, centerLon = (double?)121, zoom = (int?)10 }))))]);
        c = await Load(db, c.Id); Assert.Equal("<p>Independent Notes</p>", c.Notes); Assert.Equal(b.Tags.Single().Id, c.Tags.Single().Id);
        Assert.Equal(25, c.CenterLat); Assert.Equal(121, c.CenterLon); Assert.Equal(10, c.Zoom);
    }

    /// <summary>Selected route adoption reuses ordinary route-original evidence and preserves unrelated Segment title/Notes/position.</summary>
    [PostgresFact]
    public async Task SelectedRouteAndTitleAreIndependentAndChangedAnchorsRefuse()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext(); var created = await StartSelection(db, a, b);
        var c = await Load(db, created.TripId); var current = c.Segments.Single();
        var canonical = await SegmentRouteReconciler.LoadAggregateAsync(db, current.Id) ?? throw new Exception();
        var tokens = new SegmentAggregateTokenService(selectionProtection);
        var proof = tokens.IssueRoute(a.UserId, c.Id, canonical); db.ChangeTracker.Clear();
        var sourceSegment = b.Segments.Single();
        var places = c.Regions.Single().Places.ToDictionary(p => p.Name);
        var anchors = b.Regions.Single().Places.Where(p => p.Location != null).Select(p => new TripCompositionAnchor(p.Id,
            places[p.Name].Id, places[p.Name].RegionId, [places[p.Name].Location!.X, places[p.Name].Location!.Y])).ToArray();
        var item = new TripCompositionItem("segment", sourceSegment.Id, "reuse", DestinationId: current.Id, ConditionToken: proof,
            Anchors: anchors, Fields: new(Route: new("useSource", E(TripCompositionFieldValues.Route(current)))));
        await Select(db, a.UserId, created, "b", [item]);
        var saved = (await Load(db, c.Id)).Segments.Single();
        Assert.Equal(current.Title, saved.Title); Assert.Equal(current.Notes, saved.Notes); Assert.Equal(current.DisplayOrder, saved.DisplayOrder);
        Assert.Equal(sourceSegment.EstimatedDuration, saved.EstimatedDuration);
        await Select(db, a.UserId, created, "b", [new("segment", sourceSegment.Id, "reuse", DestinationId: current.Id,
            ExpectedDecision: new(current.Id, "reuse"), Fields: new(Title: new("clear", E(saved.Title))))]);
        saved = (await Load(db, c.Id)).Segments.Single(); Assert.Null(saved.Title); Assert.Equal(current.RouteGeometry!.AsText(), saved.RouteGeometry!.AsText());
        await db.Places.Where(p => p.Id == anchors[0].DestinationId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Location, new Point(12.01, 40) { SRID = 4326 }));
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => Select(db, a.UserId, created, "b", [item with { ExpectedDecision = new(current.Id, "reuse") }]));
        Assert.Null((await Load(db, c.Id)).Segments.Single().Title);
    }

    [PostgresFact]
    public async Task PlanningOnlyAdoptionDoesNotPublishSourceNoticesOrAdvanceFreshness()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext();
        var sourceRow = await db.Trips.SingleAsync(t => t.Id == b.Id);
        sourceRow.Credits = [new("", "Private planning source", "Other terms")];
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var created = await StartSelection(db, a, b); var c = await Load(db, created.TripId);
        var target = c.Regions.Single().Places.Single(p => p.Name == "Start");
        await Select(db, a.UserId, created, "b", [new("place", b.Regions.Single().Places.First().Id, "reuse", DestinationId: target.Id,
            ExpectedParentId: target.RegionId, Fields: new(Planning: new("useSource", E(target.Planning))))]);
        var after = await Load(db, c.Id);
        Assert.Equal(c.UpdatedAt, after.UpdatedAt); Assert.Equal(c.Credits, after.Credits);
        Assert.DoesNotContain(after.Credits, credit => credit.SourceTitle == "Private planning source");
    }

    [PostgresFact]
    public async Task TagsOnlyAdoptionAdvancesPublicFreshness()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext(); var created = await StartSelection(db, a, b); var c = await Load(db, created.TripId);
        await Select(db, a.UserId, created, "b", [new("trip", b.Id, "reuse", DestinationId: c.Id,
            Fields: new(Tags: new("useSource", E(c.Tags.OrderBy(t => t.Slug).Select(t => t.Slug).ToArray()))))]);
        var after = await Load(db, c.Id);
        Assert.Equal(b.Tags.Single().Id, after.Tags.Single().Id);
        Assert.True(after.UpdatedAt > c.UpdatedAt, "A tags-only public content change must advance freshness.");
    }

    [PostgresFact]
    public async Task ClosedDeletedAndRevokedCallerActionsNeverRecreateWorkspace()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext(); var created = await StartSelection(db, a, b);
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => Select(db, a.UserId, created, "a", [new("trip", a.Id, "remove",
            DestinationId: created.TripId, ExpectedDecision: new(created.TripId, "included"))]));
        var skip = new TripCompositionItem("place", b.Regions.Single().Places.First().Id, "skip");
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => Select(db, a.UserId, created, "b", [skip], _ => Task.FromResult(false)));
        await new TripCompositionService(db).FinishAsync(a.UserId, created.CompositionId!.Value);
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => Select(db, a.UserId, created, "b", [skip]));
        Assert.Null((await db.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == created.CompositionId)).Mappings);
        await TripCompositionLifecycle.DeleteAsync(db, created.TripId, a.UserId, default);
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => Select(db, a.UserId, created, "b", [skip]));
        Assert.False(await db.Set<TripComposition>().AnyAsync(w => w.Id == created.CompositionId));
    }
}
