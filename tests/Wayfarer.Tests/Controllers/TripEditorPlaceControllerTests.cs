using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Verifies successful Trip Editor place mutations and their persisted side effects.
/// </summary>
public sealed class TripEditorPlaceControllerTests : TripEditorPlaceControllerTestBase
{
    [Fact]
    public async Task CreatePlaceForOwnerAppendsPlaceAndReturnsAffectedSlices()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var region = trip.Regions.Single(r => r.Name == "Athens");
        var existingPlaceId = region.Places.Single().Id;
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.CreatePlace(trip.Id, region.Id, CancellationToken.None), ValidCreateBody("Created"));

        var envelope = AssertMutation<EditorPlaceDto>(result);
        Assert.True(envelope.Success);
        Assert.Equal("Created", envelope.Data.Name);
        Assert.Equal(region.Id, envelope.Data.RegionId);
        Assert.Equal(2, envelope.Data.DisplayOrder);
        Assert.Equal(new[] { envelope.Data.Id }, envelope.Affected.Places.Select(p => p.Id));
        Assert.Equal(new[] { existingPlaceId, envelope.Data.Id }, envelope.Affected.PlaceOrdersByRegionId[region.Id]);
        Assert.NotNull(envelope.Affected.VisitProgress);
        Assert.Empty(envelope.DeletedIds.Places);
    }

    [Fact]
    public async Task UpdatePlaceMoveAppendsToNewRegionAndReindexesOldAndNewOrders()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var oldRegion = trip.Regions.Single(r => r.Name == "Athens");
        var newRegion = trip.Regions.Single(r => r.Name == "Thessaloniki");
        var moved = oldRegion.Places.Single();
        var existingNewRegionPlaceId = newRegion.Places.Single().Id;
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.UpdatePlace(trip.Id, moved.Id, CancellationToken.None), ValidUpdateBody(newRegion.Id, "Moved"));

        var envelope = AssertMutation<EditorPlaceDto>(result);
        Assert.Equal(newRegion.Id, envelope.Data.RegionId);
        Assert.Empty(envelope.Affected.PlaceOrdersByRegionId[oldRegion.Id]);
        Assert.Equal(new[] { existingNewRegionPlaceId, moved.Id }, envelope.Affected.PlaceOrdersByRegionId[newRegion.Id]);
        Assert.Equal(1, db.Places.Single(p => p.Id == existingNewRegionPlaceId).DisplayOrder);
        Assert.Equal(2, db.Places.Single(p => p.Id == moved.Id).DisplayOrder);
    }

    [Fact]
    public async Task DeletePlaceDeletesEndpointSegmentsAndReturnsDeletedIds()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var deleted = trip.Regions.Single(r => r.Name == "Athens").Places.Single();
        var deletedSegmentIds = trip.Segments.Where(s => s.FromPlaceId == deleted.Id || s.ToPlaceId == deleted.Id).Select(s => s.Id).OrderBy(id => id).ToArray();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await controller.DeletePlace(trip.Id, deleted.Id, CancellationToken.None);

        var envelope = AssertMutation<EditorPlaceDeleteResult>(result);
        Assert.Equal(deleted.Id, envelope.Data.PlaceId);
        Assert.Equal(new[] { deleted.Id }, envelope.DeletedIds.Places);
        Assert.Equal(deletedSegmentIds, envelope.DeletedIds.Segments.OrderBy(id => id));
        Assert.Empty(envelope.Affected.PlaceOrdersByRegionId[deleted.RegionId]);
        Assert.Single(envelope.Affected.SegmentOrder!);
        Assert.Empty(db.Segments.Where(s => deletedSegmentIds.Contains(s.Id)));
    }

    [Fact]
    public async Task OrderPlacesPersistsCompleteRegionOrder()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var region = trip.Regions.Single(r => r.Name == "Thessaloniki");
        var first = region.Places.First();
        var second = new Place { Id = Guid.NewGuid(), UserId = "owner-user", Region = region, RegionId = region.Id, Name = "Second", DisplayOrder = 2 };
        db.Places.Add(second);
        db.SaveChanges();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.OrderPlaces(trip.Id, region.Id, CancellationToken.None), $$"""
        { "placeIds": [ "{{second.Id}}", "{{first.Id}}" ] }
        """);

        var envelope = AssertMutation<EditorPlaceOrderResult>(result);
        Assert.Equal(new[] { second.Id, first.Id }, envelope.Data.PlaceOrder);
        Assert.Equal(envelope.Data.PlaceOrder, envelope.Affected.PlaceOrdersByRegionId[region.Id]);
        Assert.Equal(1, db.Places.Single(p => p.Id == second.Id).DisplayOrder);
        Assert.Equal(2, db.Places.Single(p => p.Id == first.Id).DisplayOrder);
    }

    [Fact]
    public async Task CoordinateUpdateRewritesEndpointRoutesAndClearingLocationClearsRoutes()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var place = trip.Regions.Single(r => r.Name == "Athens").Places.Single();
        var segment = trip.Segments.Single(s => s.FromPlaceId == place.Id);
        segment.RouteGeometry = new LineString(new[] { new Coordinate(1, 2), new Coordinate(3, 4), new Coordinate(5, 6) }) { SRID = 4326 };
        db.SaveChanges();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var moved = await SendJson(controller, c => c.UpdatePlace(trip.Id, place.Id, CancellationToken.None), ValidUpdateBody(place.RegionId, "Moved", 11, 22));
        var movedEnvelope = AssertMutation<EditorPlaceDto>(moved);
        var movedRoute = db.Segments.Single(s => s.Id == segment.Id).RouteGeometry!;
        Assert.Equal(22, movedRoute.Coordinates[0].X);
        Assert.Equal(11, movedRoute.Coordinates[0].Y);
        Assert.Single(movedEnvelope.Affected.Segments);

        var cleared = await SendJson(controller, c => c.UpdatePlace(trip.Id, place.Id, CancellationToken.None), ValidUpdateBody(place.RegionId, "Cleared", null, null));
        Assert.Single(AssertMutation<EditorPlaceDto>(cleared).Affected.Segments);
        Assert.Null(db.Segments.Single(s => s.Id == segment.Id).RouteGeometry);
    }

    [Fact]
    public async Task InvalidPersistedRouteClearsToNullDuringEndpointRewrite()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var place = trip.Regions.Single(r => r.Name == "Athens").Places.Single();
        var segment = trip.Segments.Single(s => s.FromPlaceId == place.Id);
        segment.RouteGeometry = new LineString(Array.Empty<Coordinate>()) { SRID = 4326 };
        db.SaveChanges();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.UpdatePlace(trip.Id, place.Id, CancellationToken.None), ValidUpdateBody(place.RegionId, "Moved", 11, 22));

        Assert.Single(AssertMutation<EditorPlaceDto>(result).Affected.Segments);
        Assert.Null(db.Segments.Single(s => s.Id == segment.Id).RouteGeometry);
    }

    [Fact]
    public async Task ReverseGeocodeUnavailableSavesManualAddressAndReturnsWarning()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var region = trip.Regions.Single(r => r.Name == "Athens");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.CreatePlace(trip.Id, region.Id, CancellationToken.None), ValidCreateBody("Geo", reverseGeocode: true));

        var envelope = AssertMutation<EditorPlaceDto>(result);
        Assert.Equal("Manual address", envelope.Data.Address);
        var warning = Assert.Single(envelope.Warnings);
        Assert.Equal("reverse-geocode-unavailable", warning.Code);
    }

    [Fact]
    public async Task ReverseGeocodeExceptionSavesManualAddressAndReturnsWarning()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var region = trip.Regions.Single(r => r.Name == "Athens");
        db.ApiTokens.Add(new ApiToken
        {
            Name = "Mapbox",
            Token = "mapbox-token",
            UserId = "owner-user",
            User = new ApplicationUser { Id = "owner-user", UserName = "owner@example.test", DisplayName = "Owner" }
        });
        db.SaveChanges();
        var controller = BuildController(db, new ReverseGeocodingService(
            new HttpClient(new ThrowingReverseGeocodeHandler()),
            NullLogger<BaseApiController>.Instance));
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.CreatePlace(trip.Id, region.Id, CancellationToken.None), ValidCreateBody("Geo", reverseGeocode: true));

        var envelope = AssertMutation<EditorPlaceDto>(result);
        Assert.Equal("Manual address", envelope.Data.Address);
        Assert.Equal("Manual address", db.Places.Single(p => p.Id == envelope.Data.Id).Address);
        var warning = Assert.Single(envelope.Warnings);
        Assert.Equal("reverse-geocode-unavailable", warning.Code);
        Assert.Contains(db.Places, p => p.Id == envelope.Data.Id && p.Name == "Geo");
    }
}
