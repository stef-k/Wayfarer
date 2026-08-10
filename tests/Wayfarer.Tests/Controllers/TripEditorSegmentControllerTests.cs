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
/// Focused success and ownership tests for Trip Editor segment mutations.
/// </summary>
public sealed class TripEditorSegmentControllerTests : TestBase
{
    [Fact]
    public async Task OwnerCreateUpdateDeleteAndOrderSegmentsReturnDeterministicSlices()
    {
        using var db = CreateDbContext();
        var graph = SeedTripGraph(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var created = await SendJson(controller, c => c.CreateSegment(graph.Trip.Id, CancellationToken.None), ValidBody(graph.FirstPlace.Id, graph.SecondPlace.Id, "WALK", RouteJson(1)));
        var createEnvelope = AssertMutation<EditorSegmentDto>(created);
        Assert.Equal("walk", createEnvelope.Data.Mode);
        Assert.Equal(new[] { graph.FirstSegment.Id, graph.SecondSegment.Id, createEnvelope.Data.Id }, createEnvelope.Affected.SegmentOrder);
        Assert.Empty(createEnvelope.DeletedIds.Segments);

        var updated = await SendJson(controller, c => c.UpdateSegment(graph.Trip.Id, createEnvelope.Data.Id, CancellationToken.None), ValidBody(null, graph.FirstPlace.Id, null, "null"));
        var updateEnvelope = AssertMutation<EditorSegmentDto>(updated);
        Assert.Null(updateEnvelope.Data.FromPlaceId);
        Assert.Equal(string.Empty, updateEnvelope.Data.Mode);
        Assert.Null(updateEnvelope.Data.Route);
        Assert.Null(updateEnvelope.Affected.SegmentOrder);

        var order = await SendJson(controller, c => c.OrderSegments(graph.Trip.Id, CancellationToken.None), $$"""
        { "segmentIds": [ "{{createEnvelope.Data.Id}}", "{{graph.SecondSegment.Id}}", "{{graph.FirstSegment.Id}}" ] }
        """);
        var orderEnvelope = AssertMutation<EditorSegmentOrderResult>(order);
        Assert.Equal(new[] { createEnvelope.Data.Id, graph.SecondSegment.Id, graph.FirstSegment.Id }, orderEnvelope.Data.SegmentOrder);
        Assert.Equal(1, db.Segments.Single(s => s.Id == createEnvelope.Data.Id).DisplayOrder);
        Assert.Equal(3, db.Segments.Single(s => s.Id == graph.FirstSegment.Id).DisplayOrder);

        var deleted = await controller.DeleteSegment(graph.Trip.Id, createEnvelope.Data.Id, CancellationToken.None);
        var deleteEnvelope = AssertMutation<EditorSegmentDeleteResult>(deleted);
        Assert.Equal(createEnvelope.Data.Id, deleteEnvelope.Data.SegmentId);
        Assert.Equal(new[] { createEnvelope.Data.Id }, deleteEnvelope.DeletedIds.Segments);
        Assert.Equal(new[] { graph.SecondSegment.Id, graph.FirstSegment.Id }, deleteEnvelope.Affected.SegmentOrder);
    }

    [Fact]
    public async Task RouteSaveAndClearPersistOnlyExplicitSubmittedRoute()
    {
        using var db = CreateDbContext();
        var graph = SeedTripGraph(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var saved = await SendJson(controller, c => c.UpdateSegment(graph.Trip.Id, graph.FirstSegment.Id, CancellationToken.None), ValidBody(graph.FirstPlace.Id, graph.SecondPlace.Id, "car", RouteJson(10)));
        var savedEnvelope = AssertMutation<EditorSegmentDto>(saved);
        Assert.Equal(10, savedEnvelope.Data.Route!.Value.GetProperty("coordinates")[0][0].GetDouble());
        Assert.NotNull(db.Segments.Single(s => s.Id == graph.FirstSegment.Id).RouteGeometry);

        var cleared = await SendJson(controller, c => c.UpdateSegment(graph.Trip.Id, graph.FirstSegment.Id, CancellationToken.None), ValidBody(graph.FirstPlace.Id, graph.SecondPlace.Id, "car", "null"));
        var clearedEnvelope = AssertMutation<EditorSegmentDto>(cleared);
        Assert.Null(clearedEnvelope.Data.Route);
        Assert.Null(db.Segments.Single(s => s.Id == graph.FirstSegment.Id).RouteGeometry);
    }

    [Fact]
    public async Task AuthAndMissingOwnedResourcesReturnBeforeBodyParsing()
    {
        using var db = CreateDbContext();
        var graph = SeedTripGraph(db, "owner-user");
        var controller = BuildController(db);

        var anonymous = await SendJson(controller, c => c.CreateSegment(graph.Trip.Id, CancellationToken.None), ValidBody(graph.FirstPlace.Id, graph.SecondPlace.Id, "walk", "null"));
        ConfigureControllerWithUserRole(controller, "owner-user", "Manager");
        var wrongRole = await SendJson(controller, c => c.CreateSegment(graph.Trip.Id, CancellationToken.None), ValidBody(graph.FirstPlace.Id, graph.SecondPlace.Id, "walk", "null"));
        ConfigureControllerWithUserRole(controller, "other-user");
        var nonOwnerTrip = await SendJson(controller, c => c.CreateSegment(graph.Trip.Id, CancellationToken.None), "{");
        ConfigureControllerWithUserRole(controller, "owner-user");
        var missingSegment = await SendJson(controller, c => c.UpdateSegment(graph.Trip.Id, Guid.NewGuid(), CancellationToken.None), "{");
        var missingDelete = await controller.DeleteSegment(graph.Trip.Id, Guid.NewGuid(), CancellationToken.None);
        var nonOwnedSegment = await SendJson(controller, c => c.UpdateSegment(Guid.NewGuid(), graph.FirstSegment.Id, CancellationToken.None), "{");

        Assert.IsType<UnauthorizedResult>(anonymous);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(wrongRole).StatusCode);
        Assert.IsType<NotFoundResult>(nonOwnerTrip);
        Assert.IsType<NotFoundResult>(missingSegment);
        Assert.IsType<NotFoundResult>(missingDelete);
        Assert.IsType<NotFoundResult>(nonOwnedSegment);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    public async Task OwnedSegmentMutationsWithInvalidBodyReturnRequestValidationProblem(string body)
    {
        using var db = CreateDbContext();
        var graph = SeedTripGraph(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var create = await SendJson(controller, c => c.CreateSegment(graph.Trip.Id, CancellationToken.None), body);
        var update = await SendJson(controller, c => c.UpdateSegment(graph.Trip.Id, graph.FirstSegment.Id, CancellationToken.None), body);
        var order = await SendJson(controller, c => c.OrderSegments(graph.Trip.Id, CancellationToken.None), body);

        Assert.Contains("request", AssertValidationProblem(create).Errors.Keys);
        Assert.Contains("request", AssertValidationProblem(update).Errors.Keys);
        Assert.Contains("request", AssertValidationProblem(order).Errors.Keys);
    }

    internal static SegmentGraph SeedTripGraph(ApplicationDbContext db, string userId)
    {
        var trip = new Trip { Id = Guid.NewGuid(), UserId = userId, Name = "Trip", UpdatedAt = DateTime.UtcNow };
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = "Region", DisplayOrder = 1 };
        var first = new Place { Id = Guid.NewGuid(), UserId = userId, Region = region, RegionId = region.Id, Name = "Alpha", DisplayOrder = 1, Location = new Point(23, 37) { SRID = 4326 } };
        var second = new Place { Id = Guid.NewGuid(), UserId = userId, Region = region, RegionId = region.Id, Name = "Beta", DisplayOrder = 2, Location = new Point(24, 38) { SRID = 4326 } };
        var firstSegment = new Segment { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, FromPlaceId = first.Id, ToPlaceId = second.Id, Mode = "walk", DisplayOrder = 1, Notes = "" };
        var secondSegment = new Segment { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, FromPlaceId = second.Id, ToPlaceId = first.Id, Mode = "bike", DisplayOrder = 2, Notes = "" };
        region.Places.Add(first);
        region.Places.Add(second);
        trip.Regions.Add(region);
        trip.Segments.Add(firstSegment);
        trip.Segments.Add(secondSegment);
        db.Trips.Add(trip);
        db.SaveChanges();
        return new SegmentGraph(trip, first, second, firstSegment, secondSegment);
    }

    internal static string ValidBody(Guid? fromPlaceId, Guid? toPlaceId, string? mode, string routeJson) =>
        $$"""
        {
          "fromPlaceId": {{GuidJson(fromPlaceId)}},
          "toPlaceId": {{GuidJson(toPlaceId)}},
          "mode": {{StringJson(mode)}},
          "estimatedDistanceKm": 12.5,
          "estimatedDurationMinutes": 35,
          "estimatedDurationSource": "Manual",
          "notesHtml": null,
          "route": {{routeJson}}
        }
        """;

    internal static string RouteJson(int offset) =>
        $$"""{ "type": "LineString", "coordinates": [[{{offset}}, 1], [{{offset + 1}}, 2]] }""";

    internal static string StringJson(string? value) => value == null ? "null" : $"\"{value}\"";

    internal static string GuidJson(Guid? value) => value.HasValue ? $"\"{value.Value}\"" : "null";

    internal static void ConfigureControllerWithUserRole(ControllerBase controller, string userId, string role = "User")
    {
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(userId, role) };
    }

    internal static TripEditorController BuildController(ApplicationDbContext db)
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
            new TripEditorSegmentMutationService(db),
            Mock.Of<ILogger<TripEditorController>>());
    }

    internal static async Task<IActionResult> SendJson(TripEditorController controller, Func<TripEditorController, Task<IActionResult>> action, string requestBody)
    {
        var httpContext = controller.ControllerContext.HttpContext ?? new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        var body = Encoding.UTF8.GetBytes(requestBody);
        httpContext.Request.Body = new MemoryStream(body);
        httpContext.Request.ContentLength = body.Length;
        httpContext.Request.ContentType = "application/json";
        return await action(controller);
    }

    internal static ValidationProblemDetails AssertValidationProblem(IActionResult result)
    {
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        return Assert.IsType<ValidationProblemDetails>(badRequest.Value);
    }

    internal static EditorMutationResult<T> AssertMutation<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<EditorMutationResult<T>>(ok.Value);
    }

    private static IWebHostEnvironment BuildEnvironment()
    {
        var mock = new Mock<IWebHostEnvironment>();
        mock.SetupGet(e => e.WebRootPath).Returns(Path.GetTempPath());
        return mock.Object;
    }

    internal sealed record SegmentGraph(Trip Trip, Place FirstPlace, Place SecondPlace, Segment FirstSegment, Segment SecondSegment);
}
