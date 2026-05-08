using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Focused tests for Trip Editor region mutations.
/// </summary>
public sealed class TripEditorRegionControllerTests : TestBase
{
    [Fact]
    public async Task CreateRegionForOwnerAppendsNormalRegionAndReturnsAffectedSlices()
    {
        using var db = CreateDbContext();
        var trip = SeedTripWithRegions(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.CreateRegion(trip.Id, CancellationToken.None), ValidRegionBody("Created Region"));

        var envelope = AssertMutation<EditorRegionDto>(result);
        Assert.True(envelope.Success);
        Assert.Equal("Created Region", envelope.Data.Name);
        Assert.Equal("<p>Notes</p>", envelope.Data.NotesHtml);
        Assert.Equal("https://cdn.example.test/cover.jpg", envelope.Data.CoverImage!.RawUrl);
        Assert.Equal(10, envelope.Data.Center!.Latitude);
        Assert.Equal(20, envelope.Data.Center.Longitude);
        Assert.Equal(new[] { envelope.Data.Id }, envelope.Affected.Regions.Select(r => r.Id));
        Assert.Equal(Array.Empty<Guid>(), envelope.Affected.PlaceOrdersByRegionId[envelope.Data.Id]);
        Assert.Equal(Array.Empty<Guid>(), envelope.Affected.AreaOrdersByRegionId[envelope.Data.Id]);
        Assert.Contains(envelope.Data.Id, envelope.Affected.RegionOrder!);
        Assert.Empty(envelope.DeletedIds.Regions);
        Assert.Equal(3, db.Regions.Single(r => r.Id == envelope.Data.Id).DisplayOrder);
    }

    [Fact]
    public async Task UpdateRegionForOwnerSavesCompleteDraftAndReturnsRegionOnlySlice()
    {
        using var db = CreateDbContext();
        var trip = SeedTripWithRegions(db, "owner-user");
        var region = trip.Regions.Single(r => r.Name == "Athens");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.UpdateRegion(trip.Id, region.Id, CancellationToken.None), ValidRegionBody("Updated Region"));

        var envelope = AssertMutation<EditorRegionDto>(result);
        Assert.Equal(region.Id, envelope.Data.Id);
        Assert.Equal("Updated Region", envelope.Data.Name);
        Assert.Single(envelope.Affected.Regions);
        Assert.Null(envelope.Affected.RegionOrder);
        Assert.Empty(envelope.DeletedIds.Regions);
        Assert.Equal("Updated Region", db.Regions.Single(r => r.Id == region.Id).Name);
    }

    [Fact]
    public async Task DeleteRegionRemovesChildrenEndpointSegmentsAndReturnsExplicitAffectedSlices()
    {
        using var db = CreateDbContext();
        var trip = SeedTripWithDeleteGraph(db, "owner-user");
        var deletedRegion = trip.Regions.Single(r => r.Name == "Delete Me");
        var keptRegion = trip.Regions.Single(r => r.Name == "Keep Me");
        var deletedPlaceIds = deletedRegion.Places.Select(p => p.Id).OrderBy(id => id).ToArray();
        var deletedAreaIds = deletedRegion.Areas.Select(a => a.Id).OrderBy(id => id).ToArray();
        var deletedSegmentIds = trip.Segments
            .Where(s => deletedPlaceIds.Contains(s.FromPlaceId!.Value) || deletedPlaceIds.Contains(s.ToPlaceId!.Value))
            .Select(s => s.Id)
            .OrderBy(id => id)
            .ToArray();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await controller.DeleteRegion(trip.Id, deletedRegion.Id, CancellationToken.None);

        var envelope = AssertMutation<EditorRegionDto?>(result);
        Assert.Null(envelope.Data);
        Assert.Equal(new[] { deletedRegion.Id }, envelope.DeletedIds.Regions);
        Assert.Equal(deletedPlaceIds, envelope.DeletedIds.Places.OrderBy(id => id));
        Assert.Equal(deletedAreaIds, envelope.DeletedIds.Areas.OrderBy(id => id));
        Assert.Equal(deletedSegmentIds, envelope.DeletedIds.Segments.OrderBy(id => id));
        Assert.Empty(envelope.Affected.Regions);
        Assert.Equal(new[] { trip.Regions.Single(r => r.Name == "Unassigned Places").Id, keptRegion.Id }, envelope.Affected.RegionOrder);
        Assert.Single(envelope.Affected.SegmentOrder!);
        Assert.NotNull(envelope.Affected.VisitProgress);
        Assert.Equal(1, envelope.Affected.VisitProgress!.TotalPlaces);
        Assert.Empty(db.Places.Where(p => deletedPlaceIds.Contains(p.Id)));
        Assert.Empty(db.Areas.Where(a => deletedAreaIds.Contains(a.Id)));
        Assert.Empty(db.Segments.Where(s => deletedSegmentIds.Contains(s.Id)));
    }

    [Fact]
    public async Task OrderRegionsForOwnerPersistsShadowAtZeroAndNormalRegionsFromOne()
    {
        using var db = CreateDbContext();
        var trip = SeedTripWithRegions(db, "owner-user");
        var first = trip.Regions.Single(r => r.Name == "Athens");
        var second = trip.Regions.Single(r => r.Name == "Thessaloniki");
        var shadow = trip.Regions.Single(r => r.Name == "Unassigned Places");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.OrderRegions(trip.Id, CancellationToken.None), $$"""
        { "regionIds": [ "{{second.Id}}", "{{first.Id}}" ] }
        """);

        var envelope = AssertMutation<EditorRegionOrderResult>(result);
        Assert.Equal(new[] { shadow.Id, second.Id, first.Id }, envelope.Data.RegionOrder);
        Assert.Equal(envelope.Data.RegionOrder, envelope.Affected.RegionOrder);
        Assert.Equal(0, db.Regions.Single(r => r.Id == shadow.Id).DisplayOrder);
        Assert.Equal(1, db.Regions.Single(r => r.Id == second.Id).DisplayOrder);
        Assert.Equal(2, db.Regions.Single(r => r.Id == first.Id).DisplayOrder);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    public async Task OwnedRegionMutationsWithInvalidBodyReturnRequestValidationProblem(string body)
    {
        using var db = CreateDbContext();
        var trip = SeedTripWithRegions(db, "owner-user");
        var region = trip.Regions.Single(r => r.Name == "Athens");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var create = await SendJson(controller, c => c.CreateRegion(trip.Id, CancellationToken.None), body);
        var update = await SendJson(controller, c => c.UpdateRegion(trip.Id, region.Id, CancellationToken.None), body);
        var order = await SendJson(controller, c => c.OrderRegions(trip.Id, CancellationToken.None), body);

        Assert.Contains("request", AssertValidationProblem(create).Errors.Keys);
        Assert.Contains("request", AssertValidationProblem(update).Errors.Keys);
        Assert.Contains("request", AssertValidationProblem(order).Errors.Keys);
    }

    [Fact]
    public async Task MissingTripOrRegionReturnsNotFoundBeforeBodyParsing()
    {
        using var db = CreateDbContext();
        var trip = SeedTripWithRegions(db, "owner-user");
        var region = trip.Regions.Single(r => r.Name == "Athens");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "other-user");

        var nonOwnerTrip = await SendJson(controller, c => c.CreateRegion(trip.Id, CancellationToken.None), "{");
        var nonOwnerRegion = await SendJson(controller, c => c.UpdateRegion(trip.Id, region.Id, CancellationToken.None), "{");
        ConfigureControllerWithUserRole(controller, "owner-user");
        var missingRegion = await SendJson(controller, c => c.UpdateRegion(trip.Id, Guid.NewGuid(), CancellationToken.None), "{");

        Assert.IsType<NotFoundResult>(nonOwnerTrip);
        Assert.IsType<NotFoundResult>(nonOwnerRegion);
        Assert.IsType<NotFoundResult>(missingRegion);
    }

    [Fact]
    public async Task RegionMutationAuthFailuresReturnExpectedStatus()
    {
        using var db = CreateDbContext();
        var trip = SeedTripWithRegions(db, "owner-user");
        var controller = BuildController(db);

        var anonymous = await SendJson(controller, c => c.CreateRegion(trip.Id, CancellationToken.None), ValidRegionBody("Blocked"));
        ConfigureControllerWithUserRole(controller, "owner-user", "Manager");
        var wrongRole = await SendJson(controller, c => c.CreateRegion(trip.Id, CancellationToken.None), ValidRegionBody("Blocked"));

        Assert.IsType<UnauthorizedResult>(anonymous);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(wrongRole).StatusCode);
    }

    [Fact]
    public async Task ShadowRegionCannotBeUpdatedDeletedReorderedOrRenamed()
    {
        using var db = CreateDbContext();
        var trip = SeedTripWithRegions(db, "owner-user");
        var shadow = trip.Regions.Single(r => r.Name == "Unassigned Places");
        var normal = trip.Regions.Single(r => r.Name == "Athens");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var updateShadow = await SendJson(controller, c => c.UpdateRegion(trip.Id, shadow.Id, CancellationToken.None), ValidRegionBody("Updated"));
        var deleteShadow = await controller.DeleteRegion(trip.Id, shadow.Id, CancellationToken.None);
        var orderShadow = await SendJson(controller, c => c.OrderRegions(trip.Id, CancellationToken.None), $$"""
        { "regionIds": [ "{{shadow.Id}}", "{{normal.Id}}" ] }
        """);
        var reservedName = await SendJson(controller, c => c.UpdateRegion(trip.Id, normal.Id, CancellationToken.None), ValidRegionBody(" unassigned places "));

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(updateShadow).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(deleteShadow).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(orderShadow).StatusCode);
        Assert.Contains("name", AssertValidationProblem(reservedName).Errors.Keys);
    }

    [Fact]
    public async Task RegionSaveValidationRejectsInvalidFieldsAndServerOwnedFields()
    {
        using var db = CreateDbContext();
        var trip = SeedTripWithRegions(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.CreateRegion(trip.Id, CancellationToken.None), """
        {
          "id": "00000000-0000-0000-0000-000000000001",
          "tripId": "00000000-0000-0000-0000-000000000002",
          "displayOrder": 99,
          "isShadow": true,
          "capabilities": {},
          "name": " ",
          "notesHtml": "<img src=\"   DATA:image/png;base64,abc\">",
          "coverImage": { "rawUrl": "ftp://example.test/a.jpg" },
          "center": { "latitude": 91, "longitude": -181 }
        }
        """);

        var problem = AssertValidationProblem(result);
        Assert.Contains("id", problem.Errors.Keys);
        Assert.Contains("tripId", problem.Errors.Keys);
        Assert.Contains("displayOrder", problem.Errors.Keys);
        Assert.Contains("isShadow", problem.Errors.Keys);
        Assert.Contains("capabilities", problem.Errors.Keys);
        Assert.Contains("name", problem.Errors.Keys);
        Assert.Contains("notesHtml", problem.Errors.Keys);
        Assert.Contains("coverImage.rawUrl", problem.Errors.Keys);
        Assert.Contains("center.latitude", problem.Errors.Keys);
        Assert.Contains("center.longitude", problem.Errors.Keys);
    }

    [Theory]
    [InlineData("""{ "regionIds": [] }""")]
    [InlineData("""{ "regionIds": [ "00000000-0000-0000-0000-000000000001" ] }""")]
    public async Task OrderRegionsRejectsIncompleteOrUnknownIds(string body)
    {
        using var db = CreateDbContext();
        var trip = SeedTripWithRegions(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.OrderRegions(trip.Id, CancellationToken.None), body);

        Assert.Contains("regionIds", AssertValidationProblem(result).Errors.Keys);
    }

    private static Trip SeedTripWithRegions(ApplicationDbContext db, string userId)
    {
        var trip = new Trip { Id = Guid.NewGuid(), UserId = userId, Name = "Trip", UpdatedAt = DateTime.UtcNow };
        trip.Regions.Add(new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = "Unassigned Places", DisplayOrder = 0 });
        trip.Regions.Add(new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = "Athens", DisplayOrder = 1 });
        trip.Regions.Add(new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = "Thessaloniki", DisplayOrder = 2 });
        db.Trips.Add(trip);
        db.SaveChanges();
        return trip;
    }

    private static Trip SeedTripWithDeleteGraph(ApplicationDbContext db, string userId)
    {
        var trip = new Trip { Id = Guid.NewGuid(), UserId = userId, Name = "Trip", UpdatedAt = DateTime.UtcNow };
        var shadow = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = "Unassigned Places", DisplayOrder = 0 };
        var deleteRegion = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = "Delete Me", DisplayOrder = 3 };
        var keepRegion = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = "Keep Me", DisplayOrder = 4 };
        trip.Regions.Add(shadow);
        trip.Regions.Add(deleteRegion);
        trip.Regions.Add(keepRegion);
        var deletePlace = new Place { Id = Guid.NewGuid(), Region = deleteRegion, RegionId = deleteRegion.Id, UserId = userId, Name = "Delete Place", DisplayOrder = 1 };
        var keepPlace = new Place { Id = Guid.NewGuid(), Region = keepRegion, RegionId = keepRegion.Id, UserId = userId, Name = "Keep Place", DisplayOrder = 1 };
        deleteRegion.Places.Add(deletePlace);
        keepRegion.Places.Add(keepPlace);
        deleteRegion.Areas.Add(new Area { Id = Guid.NewGuid(), Region = deleteRegion, RegionId = deleteRegion.Id, Name = "Delete Area", DisplayOrder = 1, Geometry = Square() });
        trip.Segments.Add(new Segment { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, FromPlaceId = deletePlace.Id, ToPlaceId = keepPlace.Id, DisplayOrder = 1 });
        trip.Segments.Add(new Segment { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, FromPlaceId = keepPlace.Id, ToPlaceId = keepPlace.Id, DisplayOrder = 2 });
        db.Trips.Add(trip);
        db.SaveChanges();
        return trip;
    }

    private static Polygon Square()
    {
        var coordinates = new[]
        {
            new Coordinate(0, 0),
            new Coordinate(1, 0),
            new Coordinate(1, 1),
            new Coordinate(0, 0)
        };
        return new Polygon(new LinearRing(coordinates)) { SRID = 4326 };
    }

    private static string ValidRegionBody(string name) =>
        $$"""
        {
          "name": "{{name}}",
          "notesHtml": "<p>Notes</p>",
          "coverImage": { "rawUrl": "https://cdn.example.test/cover.jpg" },
          "center": { "latitude": 10, "longitude": 20 }
        }
        """;

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
        return new TripEditorController(
            db,
            environment,
            new IconColorProvider(environment),
            Mock.Of<ITripMapThumbnailGenerator>(),
            Mock.Of<ICacheWarmupScheduler>(),
            Mock.Of<ILogger<TripEditorController>>());
    }

    private static IWebHostEnvironment BuildEnvironment()
    {
        var mock = new Mock<IWebHostEnvironment>();
        mock.SetupGet(e => e.WebRootPath).Returns(Path.GetTempPath());
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
