using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

public sealed partial class TripCompositionPostgresTests
{
    /// <summary>Composition coordinates join the same reviewed lifecycle core and atomically reconcile dependent route geometry.</summary>
    [PostgresFact]
    public async Task SelectedCoordinatesUseOrdinaryDependencyReviewAndKeepUnselectedNotes()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext(); var created = await StartSelection(db, a, b);
        var c = await Load(db, created.TripId); var place = c.Regions.Single().Places.Single(p => p.Name == "Start");
        var lifecycle = new PlaceRegionLifecycleService(db, new(selectionProtection));
        var location = new Point(12.001, 40) { SRID = 4326 };
        var request = new PlaceStructuralRequest("coordinates", false, E(new { regionId = place.RegionId, location = PlaceStructuralRequest.Pair(place.Location) }),
            default, null, [], location, null, false);
        var preview = E(await lifecycle.PreparePlaceStructureAsync(c.Id, place.Id, a.UserId, request, new HashSet<string>(), new HashSet<string>(), default));
        var sourcePlace = b.Regions.Single().Places.Single(p => p.Name == "Start");
        await Select(db, a.UserId, created, "b", [new("place", sourcePlace.Id, "reuse", DestinationId: place.Id, ExpectedParentId: place.RegionId,
            ConditionToken: preview.GetProperty("conditionToken").GetString(), Fields: new(Location: new("edit", E(TripCompositionFieldValues.Pair(place.Location)), E(new[] { 12.001, 40.0 }))))]);
        var after = await Load(db, c.Id); var saved = after.Regions.Single().Places.Single(p => p.Id == place.Id);
        Assert.Equal(12.001, saved.Location!.X); Assert.Equal(place.Notes, saved.Notes); Assert.Equal(place.DisplayOrder, saved.DisplayOrder);
        Assert.Equal(12.001, after.Segments.Single().RouteGeometry!.GetCoordinateN(0).X);
        Assert.Single(after.Segments.Single().Waypoints);
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaceAndRegionRemovalUseOrdinaryConsequencesAndPersistCascadedDecisions(bool regionRemoval)
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext(); var created = await StartSelection(db, a, b);
        var c = await Load(db, created.TripId); var lifecycle = new PlaceRegionLifecycleService(db, new(selectionProtection));
        var kind = regionRemoval ? "region" : "place";
        var sourceId = regionRemoval ? a.Regions.Single().Id : a.Regions.Single().Places.Single(p => p.Name == "Start").Id;
        var map = await Mapping(db, created, "a", kind, sourceId);
        var proof = regionRemoval
            ? (await lifecycle.PreviewRegionDeletionAsync(c.Id, map.DestinationId, a.UserId, new("delete", "delete"), default)).Confirmation.Token
            : (await lifecycle.PreviewPlaceDeletionAsync(c.Id, map.DestinationId, a.UserId, default)).Confirmation.Token;
        await Select(db, a.UserId, created, "a", [new(kind, sourceId, "remove", DestinationId: map.DestinationId,
            ExpectedDecision: new(map.DestinationId, "included"), ConditionToken: proof, Places: "delete", Areas: "delete")]);
        var after = await Load(db, c.Id); Assert.Empty(after.Segments); Assert.Equal(c.Credits, after.Credits);
        if (regionRemoval) Assert.Empty(after.Regions); else Assert.Equal(3, after.Regions.Single().Places.Count);
        Assert.Equal("destinationRemoved", (await Mapping(db, created, "a", kind, sourceId)).Decision);
        Assert.Equal("destinationRemoved", (await Mapping(db, created, "a", "segment", a.Segments.Single().Id)).Decision);
        Assert.True(await db.Trips.AnyAsync(t => t.Id == a.Id));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    /// <summary>The action does not reconstruct omitted precision/source facts from rounded transport defaults.</summary>
    [PostgresFact]
    public async Task MissingCapturedRouteAuthorityRefusesWithoutLiveRepair()
    {
        var a = await Seed(); var b = await Seed(a.UserId);
        await using var db = fixture.CreateContext(); var created = await StartSelection(db, a, b);
        var workspace = await db.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == created.CompositionId);
        var root = System.Text.Json.Nodes.JsonNode.Parse(workspace.SnapshotB!)!;
        root["graph"]!["segments"]![0]!.AsObject().Remove("estimatedDurationSource");
        var altered = root.ToJsonString();
        await db.Set<TripComposition>().Where(w => w.Id == workspace.Id).ExecuteUpdateAsync(s => s.SetProperty(w => w.SnapshotB, altered));
        await Assert.ThrowsAsync<TripMetadataMutationRejected>(() => Select(db, a.UserId, created, "b", [new("segment", b.Segments.Single().Id, "skip")]));
        Assert.Single((await Load(db, created.TripId)).Segments);
    }
}
