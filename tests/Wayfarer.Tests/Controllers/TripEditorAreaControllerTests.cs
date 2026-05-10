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
/// Focused tests for Trip Editor area mutations.
/// </summary>
public sealed class TripEditorAreaControllerTests : TestBase
{
    [Fact]
    public async Task CreateUpdateGeometryDeleteAndOrderAreasForOwnerReturnDeterministicSlices()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var region = trip.Regions.Single(r => r.Name == "Athens");
        var existing = region.Areas.Single();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var created = await SendJson(controller, c => c.CreateArea(trip.Id, region.Id, CancellationToken.None), ValidCreateBody(" ", null));
        var createEnvelope = AssertMutation<EditorAreaDto>(created);
        Assert.Equal("Area", createEnvelope.Data.Name);
        Assert.Equal("#ff6600", createEnvelope.Data.FillHex);
        Assert.Equal(new[] { existing.Id, createEnvelope.Data.Id }, createEnvelope.Affected.AreaOrdersByRegionId[region.Id]);
        Assert.Empty(createEnvelope.DeletedIds.Areas);

        var updated = await SendJson(controller, c => c.UpdateArea(trip.Id, createEnvelope.Data.Id, CancellationToken.None), ValidUpdateBody("Updated", "#ABCDEF"));
        var updateEnvelope = AssertMutation<EditorAreaDto>(updated);
        Assert.Equal("Updated", updateEnvelope.Data.Name);
        Assert.Equal("#abcdef", updateEnvelope.Data.FillHex);
        Assert.Single(updateEnvelope.Affected.Areas);

        var geometry = await SendJson(controller, c => c.UpdateAreaGeometry(trip.Id, createEnvelope.Data.Id, CancellationToken.None), $$"""{ "geometry": {{PolygonJson(5)}} }""");
        var geometryEnvelope = AssertMutation<EditorAreaDto>(geometry);
        Assert.Equal(5, geometryEnvelope.Data.Geometry.GetProperty("coordinates")[0][0][0].GetDouble());

        var order = await SendJson(controller, c => c.OrderAreas(trip.Id, region.Id, CancellationToken.None), $$"""
        { "areaIds": [ "{{createEnvelope.Data.Id}}", "{{existing.Id}}" ] }
        """);
        var orderEnvelope = AssertMutation<EditorAreaOrderResult>(order);
        Assert.Equal(new[] { createEnvelope.Data.Id, existing.Id }, orderEnvelope.Data.AreaOrder);
        Assert.Equal(1, db.Areas.Single(a => a.Id == createEnvelope.Data.Id).DisplayOrder);
        Assert.Equal(2, db.Areas.Single(a => a.Id == existing.Id).DisplayOrder);

        var deleted = await controller.DeleteArea(trip.Id, createEnvelope.Data.Id, CancellationToken.None);
        var deleteEnvelope = AssertMutation<EditorAreaDeleteResult>(deleted);
        Assert.Equal(createEnvelope.Data.Id, deleteEnvelope.Data.AreaId);
        Assert.Equal(new[] { createEnvelope.Data.Id }, deleteEnvelope.DeletedIds.Areas);
        Assert.Equal(new[] { existing.Id }, deleteEnvelope.Affected.AreaOrdersByRegionId[region.Id]);
    }

    [Fact]
    public async Task AreaMutationAuthAndMissingOwnedResourcesReturnBeforeBodyParsing()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var region = trip.Regions.Single(r => r.Name == "Athens");
        var area = region.Areas.Single();
        var controller = BuildController(db);

        var anonymous = await SendJson(controller, c => c.CreateArea(trip.Id, region.Id, CancellationToken.None), ValidCreateBody("Blocked", "#ff6600"));
        ConfigureControllerWithUserRole(controller, "owner-user", "Manager");
        var wrongRole = await SendJson(controller, c => c.CreateArea(trip.Id, region.Id, CancellationToken.None), ValidCreateBody("Blocked", "#ff6600"));
        ConfigureControllerWithUserRole(controller, "other-user");
        var nonOwnerTrip = await SendJson(controller, c => c.CreateArea(trip.Id, region.Id, CancellationToken.None), "{");
        ConfigureControllerWithUserRole(controller, "owner-user");
        var missingRegion = await SendJson(controller, c => c.CreateArea(trip.Id, Guid.NewGuid(), CancellationToken.None), "{");
        var missingArea = await SendJson(controller, c => c.UpdateArea(trip.Id, Guid.NewGuid(), CancellationToken.None), "{");
        var missingDelete = await controller.DeleteArea(trip.Id, Guid.NewGuid(), CancellationToken.None);
        var nonOwnedArea = await SendJson(controller, c => c.UpdateArea(Guid.NewGuid(), area.Id, CancellationToken.None), "{");

        Assert.IsType<UnauthorizedResult>(anonymous);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(wrongRole).StatusCode);
        Assert.IsType<NotFoundResult>(nonOwnerTrip);
        Assert.IsType<NotFoundResult>(missingRegion);
        Assert.IsType<NotFoundResult>(missingArea);
        Assert.IsType<NotFoundResult>(missingDelete);
        Assert.IsType<NotFoundResult>(nonOwnedArea);
    }

    [Fact]
    public async Task ShadowRegionRejectsCreateAndOrderWithForbidden()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var shadow = trip.Regions.Single(r => r.Name == "Unassigned Places");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var create = await SendJson(controller, c => c.CreateArea(trip.Id, shadow.Id, CancellationToken.None), ValidCreateBody("Shadow", "#ff6600"));
        var order = await SendJson(controller, c => c.OrderAreas(trip.Id, shadow.Id, CancellationToken.None), """{ "areaIds": [] }""");

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(create).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(order).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    public async Task OwnedAreaMutationsWithInvalidBodyReturnRequestValidationProblem(string body)
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var region = trip.Regions.Single(r => r.Name == "Athens");
        var area = region.Areas.Single();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var create = await SendJson(controller, c => c.CreateArea(trip.Id, region.Id, CancellationToken.None), body);
        var update = await SendJson(controller, c => c.UpdateArea(trip.Id, area.Id, CancellationToken.None), body);
        var geometry = await SendJson(controller, c => c.UpdateAreaGeometry(trip.Id, area.Id, CancellationToken.None), body);
        var order = await SendJson(controller, c => c.OrderAreas(trip.Id, region.Id, CancellationToken.None), body);

        Assert.Contains("request", AssertValidationProblem(create).Errors.Keys);
        Assert.Contains("request", AssertValidationProblem(update).Errors.Keys);
        Assert.Contains("request", AssertValidationProblem(geometry).Errors.Keys);
        Assert.Contains("request", AssertValidationProblem(order).Errors.Keys);
    }

    [Fact]
    public async Task AreaValidationRejectsForbiddenFieldsInvalidEditableFieldsAndGeometry()
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var area = trip.Regions.Single(r => r.Name == "Athens").Areas.Single();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var update = await SendJson(controller, c => c.UpdateArea(trip.Id, area.Id, CancellationToken.None), """
        {
          "id": "00000000-0000-0000-0000-000000000001",
          "tripId": "00000000-0000-0000-0000-000000000002",
          "regionId": "00000000-0000-0000-0000-000000000003",
          "displayOrder": 99,
          "capabilities": {},
          "name": " ",
          "notesHtml": "<img src=\"   data:image/png;base64,abc\">",
          "fillHex": "orange",
          "geometry": { "type": "LineString", "coordinates": [] }
        }
        """);
        var geometry = await SendJson(controller, c => c.UpdateAreaGeometry(trip.Id, area.Id, CancellationToken.None), $$"""
        {
          "name": "Nope",
          "notesHtml": "",
          "fillHex": "#ffffff",
          "geometry": {{PolygonJson(0)}}
        }
        """);

        var updateProblem = AssertValidationProblem(update);
        foreach (var key in new[] { "id", "tripId", "regionId", "displayOrder", "capabilities", "name", "notesHtml", "fillHex", "geometry" })
        {
            Assert.Contains(key, updateProblem.Errors.Keys);
        }

        var geometryProblem = AssertValidationProblem(geometry);
        foreach (var key in new[] { "name", "notesHtml", "fillHex" })
        {
            Assert.Contains(key, geometryProblem.Errors.Keys);
        }
    }

    [Theory]
    [InlineData("""{ "type": "MultiPolygon", "coordinates": [] }""")]
    [InlineData("""{ "type": "Polygon", "coordinates": [] }""")]
    [InlineData("""{ "type": "Polygon", "coordinates": [[[0,0],[1,0],[0,0]]] }""")]
    [InlineData("""{ "type": "Polygon", "coordinates": [[[0,0],[1,0],[1,1],[0,1]]] }""")]
    [InlineData("""{ "type": "Polygon", "coordinates": [[[181,0],[1,0],[1,1],[181,0]]] }""")]
    [InlineData("""{ "type": "Polygon", "coordinates": [[[0,0,1],[1,0],[1,1],[0,0,1]]] }""")]
    public async Task AreaGeometryValidationRejectsInvalidPolygonCases(string geometryJson)
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var area = trip.Regions.Single(r => r.Name == "Athens").Areas.Single();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.UpdateAreaGeometry(trip.Id, area.Id, CancellationToken.None), $$"""{ "geometry": {{geometryJson}} }""");

        Assert.Contains(AssertValidationProblem(result).Errors.Keys, key => key.StartsWith("geometry", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "areaIds": [] }""")]
    [InlineData("""{ "areaIds": [ "00000000-0000-0000-0000-000000000001" ] }""")]
    public async Task OrderAreasRejectsMissingDuplicateUnknownAndCrossRegionIds(string body)
    {
        using var db = CreateDbContext();
        var trip = SeedTripGraph(db, "owner-user");
        var region = trip.Regions.Single(r => r.Name == "Athens");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.OrderAreas(trip.Id, region.Id, CancellationToken.None), body);

        Assert.Contains("areaIds", AssertValidationProblem(result).Errors.Keys);
    }

    private static Trip SeedTripGraph(ApplicationDbContext db, string userId)
    {
        var trip = new Trip { Id = Guid.NewGuid(), UserId = userId, Name = "Trip", UpdatedAt = DateTime.UtcNow };
        var shadow = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = "Unassigned Places", DisplayOrder = 0 };
        var athens = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = "Athens", DisplayOrder = 1 };
        var other = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = "Other", DisplayOrder = 2 };
        athens.Areas.Add(new Area { Id = Guid.NewGuid(), Region = athens, RegionId = athens.Id, Name = "Alpha", Notes = "", FillHex = "#112233", DisplayOrder = 1, Geometry = Square(0) });
        other.Areas.Add(new Area { Id = Guid.NewGuid(), Region = other, RegionId = other.Id, Name = "Other Area", Notes = "", FillHex = "#445566", DisplayOrder = 1, Geometry = Square(10) });
        trip.Regions.Add(shadow);
        trip.Regions.Add(athens);
        trip.Regions.Add(other);
        db.Trips.Add(trip);
        db.SaveChanges();
        return trip;
    }

    private static string ValidCreateBody(string name, string? fillHex) =>
        $$"""
        {
          "name": "{{name}}",
          "notesHtml": null,
          "fillHex": {{(fillHex == null ? "null" : $"\"{fillHex}\"")}},
          "geometry": {{PolygonJson(1)}}
        }
        """;

    private static string ValidUpdateBody(string name, string fillHex) =>
        $$"""
        {
          "name": "{{name}}",
          "notesHtml": "<p>Notes</p>",
          "fillHex": "{{fillHex}}",
          "geometry": {{PolygonJson(2)}}
        }
        """;

    private static string PolygonJson(int offset) =>
        $$"""{ "type": "Polygon", "coordinates": [[[{{offset}},0],[{{offset + 1}},0],[{{offset + 1}},1],[{{offset}},0]]] }""";

    private static Polygon Square(int offset) =>
        new(new LinearRing(new[] { new Coordinate(offset, 0), new Coordinate(offset + 1, 0), new Coordinate(offset + 1, 1), new Coordinate(offset, 0) })) { SRID = 4326 };

    private static void ConfigureControllerWithUserRole(ControllerBase controller, string userId, string role = "User")
    {
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(userId, role) };
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
            Mock.Of<ITripTagService>(),
            new TripEditorRegionMutationService(db),
            new TripEditorPlaceMutationService(db, environment, iconColorProvider, new ReverseGeocodingService(new HttpClient(), Mock.Of<ILogger<BaseApiController>>())),
            new TripEditorAreaMutationService(db),
            Mock.Of<ILogger<TripEditorController>>());
    }

    private static IWebHostEnvironment BuildEnvironment()
    {
        var mock = new Mock<IWebHostEnvironment>();
        mock.SetupGet(e => e.WebRootPath).Returns(Path.GetTempPath());
        return mock.Object;
    }

    private static async Task<IActionResult> SendJson(TripEditorController controller, Func<TripEditorController, Task<IActionResult>> action, string requestBody)
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
