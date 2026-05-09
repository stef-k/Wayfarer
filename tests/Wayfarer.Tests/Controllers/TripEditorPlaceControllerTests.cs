using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Focused tests for Trip Editor place mutations.
/// </summary>
public sealed class TripEditorPlaceControllerTests : TestBase
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

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    public async Task OwnedPlaceMutationsWithInvalidBodyReturnRequestValidationProblem(string body)
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var region = trip.Regions.Single(r => r.Name == "Athens");
        var place = region.Places.Single();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var create = await SendJson(controller, c => c.CreatePlace(trip.Id, region.Id, CancellationToken.None), body);
        var update = await SendJson(controller, c => c.UpdatePlace(trip.Id, place.Id, CancellationToken.None), body);
        var order = await SendJson(controller, c => c.OrderPlaces(trip.Id, region.Id, CancellationToken.None), body);

        Assert.Contains("request", AssertValidationProblem(create).Errors.Keys);
        Assert.Contains("request", AssertValidationProblem(update).Errors.Keys);
        Assert.Contains("request", AssertValidationProblem(order).Errors.Keys);
    }

    [Fact]
    public async Task MissingOwnedResourcesReturnNotFoundBeforeBodyParsing()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var region = trip.Regions.Single(r => r.Name == "Athens");
        var place = region.Places.Single();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "other-user");

        var nonOwnerTrip = await SendJson(controller, c => c.CreatePlace(trip.Id, region.Id, CancellationToken.None), "{");
        var nonOwnerPlace = await SendJson(controller, c => c.UpdatePlace(trip.Id, place.Id, CancellationToken.None), "{");
        ConfigureControllerWithUserRole(controller, "owner-user");
        var missingRegion = await SendJson(controller, c => c.CreatePlace(trip.Id, Guid.NewGuid(), CancellationToken.None), "{");
        var missingPlace = await SendJson(controller, c => c.UpdatePlace(trip.Id, Guid.NewGuid(), CancellationToken.None), "{");

        Assert.IsType<NotFoundResult>(nonOwnerTrip);
        Assert.IsType<NotFoundResult>(nonOwnerPlace);
        Assert.IsType<NotFoundResult>(missingRegion);
        Assert.IsType<NotFoundResult>(missingPlace);
    }

    [Fact]
    public async Task PlaceMutationAuthFailuresReturnExpectedStatus()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var region = trip.Regions.Single(r => r.Name == "Athens");
        var controller = BuildController(db);

        var anonymous = await SendJson(controller, c => c.CreatePlace(trip.Id, region.Id, CancellationToken.None), ValidCreateBody("Blocked"));
        ConfigureControllerWithUserRole(controller, "owner-user", "Manager");
        var wrongRole = await SendJson(controller, c => c.CreatePlace(trip.Id, region.Id, CancellationToken.None), ValidCreateBody("Blocked"));

        Assert.IsType<UnauthorizedResult>(anonymous);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(wrongRole).StatusCode);
    }

    [Fact]
    public async Task ShadowRegionTargetsReturnForbidden()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var shadow = trip.Regions.Single(r => r.Name == "Unassigned Places");
        var place = trip.Regions.Single(r => r.Name == "Athens").Places.Single();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var create = await SendJson(controller, c => c.CreatePlace(trip.Id, shadow.Id, CancellationToken.None), ValidCreateBody("Blocked"));
        var update = await SendJson(controller, c => c.UpdatePlace(trip.Id, place.Id, CancellationToken.None), ValidUpdateBody(shadow.Id, "Blocked"));
        var order = await SendJson(controller, c => c.OrderPlaces(trip.Id, shadow.Id, CancellationToken.None), """{ "placeIds": [] }""");

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(create).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(update).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(order).StatusCode);
    }

    [Fact]
    public async Task PlaceSaveValidationRejectsInvalidFieldsAndForbiddenFields()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var region = trip.Regions.Single(r => r.Name == "Athens");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.CreatePlace(trip.Id, region.Id, CancellationToken.None), $$"""
        {
          "id": "00000000-0000-0000-0000-000000000001",
          "tripId": "00000000-0000-0000-0000-000000000002",
          "regionId": "{{region.Id}}",
          "displayOrder": 99,
          "visitSummary": {},
          "capabilities": {},
          "name": " ",
          "notesHtml": "<img src=\"   DATA:image/png;base64,abc\">",
          "address": null,
          "location": { "latitude": 91, "longitude": 181 },
          "iconName": "missing",
          "markerColor": "missing",
          "reverseGeocode": true
        }
        """);

        var problem = AssertValidationProblem(result);
        foreach (var key in new[] { "id", "tripId", "regionId", "displayOrder", "visitSummary", "capabilities", "name", "notesHtml", "location.latitude", "location.longitude", "iconName", "markerColor" })
        {
            Assert.Contains(key, problem.Errors.Keys);
        }
    }

    [Fact]
    public async Task ReverseGeocodeWithNullLocationReturnsValidationProblem()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var region = trip.Regions.Single(r => r.Name == "Athens");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.CreatePlace(trip.Id, region.Id, CancellationToken.None), ValidCreateBody("No Location", reverseGeocode: true, latitude: null, longitude: null));

        Assert.Contains("reverseGeocode", AssertValidationProblem(result).Errors.Keys);
    }

    [Theory]
    [InlineData("""{ "placeIds": [] }""")]
    [InlineData("""{ "placeIds": [ "00000000-0000-0000-0000-000000000001" ] }""")]
    public async Task OrderPlacesRejectsIncompleteOrUnknownIds(string body)
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var region = trip.Regions.Single(r => r.Name == "Athens");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.OrderPlaces(trip.Id, region.Id, CancellationToken.None), body);

        Assert.Contains("placeIds", AssertValidationProblem(result).Errors.Keys);
    }

    private static Trip SeedTripGraph(ApplicationDbContext db, string userId)
    {
        var trip = new Trip { Id = Guid.NewGuid(), UserId = userId, Name = "Trip", UpdatedAt = DateTime.UtcNow };
        var shadow = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = "Unassigned Places", DisplayOrder = 0 };
        var athens = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = "Athens", DisplayOrder = 1 };
        var thessaloniki = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = "Thessaloniki", DisplayOrder = 2 };
        var first = new Place { Id = Guid.NewGuid(), UserId = userId, Region = athens, RegionId = athens.Id, Name = "Acropolis", DisplayOrder = 1, Location = new Point(23, 37) { SRID = 4326 } };
        var second = new Place { Id = Guid.NewGuid(), UserId = userId, Region = thessaloniki, RegionId = thessaloniki.Id, Name = "Tower", DisplayOrder = 1, Location = new Point(22, 40) { SRID = 4326 } };
        athens.Places.Add(first);
        thessaloniki.Places.Add(second);
        trip.Regions.Add(shadow);
        trip.Regions.Add(athens);
        trip.Regions.Add(thessaloniki);
        trip.Segments.Add(new Segment { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, FromPlaceId = first.Id, ToPlaceId = second.Id, DisplayOrder = 1 });
        trip.Segments.Add(new Segment { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, FromPlaceId = second.Id, ToPlaceId = second.Id, DisplayOrder = 2 });
        db.Trips.Add(trip);
        db.SaveChanges();
        return trip;
    }

    private static string ValidCreateBody(string name, bool reverseGeocode = false, double? latitude = 10, double? longitude = 20) =>
        $$"""
        {
          "name": "{{name}}",
          "notesHtml": "<p>Notes</p>",
          "address": "Manual address",
          "location": {{LocationJson(latitude, longitude)}},
          "iconName": "marker",
          "markerColor": "bg-blue",
          "reverseGeocode": {{reverseGeocode.ToString().ToLowerInvariant()}}
        }
        """;

    private static string ValidUpdateBody(Guid regionId, string name, double? latitude = 10, double? longitude = 20) =>
        $$"""
        {
          "regionId": "{{regionId}}",
          "name": "{{name}}",
          "notesHtml": "<p>Notes</p>",
          "address": "Manual address",
          "location": {{LocationJson(latitude, longitude)}},
          "iconName": "marker",
          "markerColor": "bg-blue",
          "reverseGeocode": false
        }
        """;

    private static string LocationJson(double? latitude, double? longitude) =>
        latitude.HasValue && longitude.HasValue
            ? $$"""{ "latitude": {{latitude.Value}}, "longitude": {{longitude.Value}} }"""
            : "null";

    private static void ConfigureControllerWithUserRole(ControllerBase controller, string userId, string role = "User")
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = BuildHttpContextWithUser(userId, role)
        };
    }

    private static TripEditorController BuildController(ApplicationDbContext db)
    {
        var environment = BuildEnvironment();
        var iconColorProvider = new IconColorProvider(environment);
        return new TripEditorController(
            db,
            environment,
            iconColorProvider,
            Mock.Of<ITripMapThumbnailGenerator>(),
            Mock.Of<ICacheWarmupScheduler>(),
            new TripEditorRegionMutationService(db),
            new TripEditorPlaceMutationService(db, environment, iconColorProvider, new ReverseGeocodingService(new HttpClient(), Mock.Of<ILogger<BaseApiController>>())),
            Mock.Of<ILogger<TripEditorController>>());
    }

    private static IWebHostEnvironment BuildEnvironment()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), "wayfarer-trip-editor-place-tests", Guid.NewGuid().ToString("N"));
        var markerDir = Path.Combine(webRoot, "icons", "wayfarer-map-icons", "dist", "marker");
        Directory.CreateDirectory(markerDir);
        File.WriteAllText(Path.Combine(markerDir, "marker.svg"), "<svg></svg>");
        File.WriteAllText(Path.Combine(webRoot, "icons", "wayfarer-map-icons", "dist", "wayfarer-map-icons.css"), ".bg-blue{} .color-white{}");
        var mock = new Mock<IWebHostEnvironment>();
        mock.SetupGet(e => e.WebRootPath).Returns(webRoot);
        return mock.Object;
    }

    private static async Task<IActionResult> SendJson(
        TripEditorController controller,
        Func<TripEditorController, Task<IActionResult>> action,
        string requestBody)
    {
        var httpContext = controller.ControllerContext.HttpContext ?? new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        var body = Encoding.UTF8.GetBytes(requestBody);
        httpContext.Request.Body = new MemoryStream(body);
        httpContext.Request.ContentLength = body.Length;
        httpContext.Request.ContentType = "application/json";
        return await action(controller);
    }

    private static ValidationProblemDetails AssertValidationProblem(IActionResult result)
    {
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("application/problem+json", badRequest.ContentTypes);
        return Assert.IsType<ValidationProblemDetails>(badRequest.Value);
    }

    private static EditorMutationResult<T> AssertMutation<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<EditorMutationResult<T>>(ok.Value);
    }
}
