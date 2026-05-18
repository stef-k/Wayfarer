using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Verifies Trip Editor place validation, authorization, and edge-case responses.
/// </summary>
public sealed class TripEditorPlaceValidationControllerTests : TripEditorPlaceControllerTestBase
{
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
    public async Task ShadowRegionAllowsCreateButRejectsMoveAndOrder()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var shadow = trip.Regions.Single(r => r.Name == "Unassigned Places");
        var place = trip.Regions.Single(r => r.Name == "Athens").Places.Single();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var create = await SendJson(controller, c => c.CreatePlace(trip.Id, shadow.Id, CancellationToken.None), ValidCreateBody("Unassigned"));
        var update = await SendJson(controller, c => c.UpdatePlace(trip.Id, place.Id, CancellationToken.None), ValidUpdateBody(shadow.Id, "Blocked"));
        var order = await SendJson(controller, c => c.OrderPlaces(trip.Id, shadow.Id, CancellationToken.None), """{ "placeIds": [] }""");

        var created = Assert.IsType<OkObjectResult>(create);
        Assert.Equal(StatusCodes.Status200OK, created.StatusCode);
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
}
