using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging;
using Moq;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Focused tests for the Trip Editor metadata mutation endpoint.
/// </summary>
public sealed class TripEditorMetadataControllerTests : TestBase
{
    /// <summary>The obsolete full draft cannot mutate any scalar, privacy, freshness or cache effect.</summary>
    [Fact]
    public async Task PatchMetadataRejectsCompleteDraftWithoutMutation()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user", isPublic: true, shareProgressEnabled: true);
        var original = db.Entry(trip).CurrentValues.Clone();
        var thumbnails = new Mock<ITripMapThumbnailGenerator>();
        var warmup = new Mock<ICacheWarmupScheduler>();
        var controller = BuildController(db, thumbnails, warmup);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await PatchMetadata(controller, trip.Id, ValidMetadataJson(isPublic: "false"), CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        foreach (var property in original.Properties)
            Assert.Equal(original[property], db.Entry(trip).CurrentValues[property]);
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Empty(thumbnails.Invocations);
        Assert.Empty(warmup.Invocations);
    }

    private static void ConfigureControllerWithUserRole(ControllerBase controller, string userId, string role = "User")
    {
        var httpContext = BuildHttpContextWithUser(userId, role);
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("example.test");

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    private static TripEditorController BuildController(
        ApplicationDbContext db,
        Mock<ITripMapThumbnailGenerator>? thumbnailMock = null,
        Mock<ICacheWarmupScheduler>? warmupMock = null)
    {
        var environment = BuildEnvironment();
        var controller = new TripEditorController(
            db,
            environment,
            new IconColorProvider(environment),
            thumbnailMock?.Object ?? Mock.Of<ITripMapThumbnailGenerator>(),
            warmupMock?.Object ?? Mock.Of<ICacheWarmupScheduler>(),
            Mock.Of<ITripTagService>(),
            new TripEditorRegionMutationService(db),
            new TripEditorPlaceMutationService(db, environment, new IconColorProvider(environment), new ReverseGeocodingService(new HttpClient(), Mock.Of<ILogger<BaseApiController>>())),
            new TripEditorAreaMutationService(db),
            new TripEditorSegmentMutationService(db),
            Mock.Of<ILogger<TripEditorController>>());

        return controller;
    }

    private static async Task<IActionResult> PatchMetadata(
        TripEditorController controller,
        Guid tripId,
        JsonElement request,
        CancellationToken cancellationToken) =>
        await PatchMetadata(controller, tripId, request.GetRawText(), cancellationToken);

    private static async Task<IActionResult> PatchMetadata(
        TripEditorController controller,
        Guid tripId,
        string requestBody,
        CancellationToken cancellationToken)
    {
        var httpContext = controller.ControllerContext.HttpContext ?? new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var body = Encoding.UTF8.GetBytes(requestBody);
        httpContext.Request.Body = new MemoryStream(body);
        httpContext.Request.ContentLength = body.Length;
        httpContext.Request.ContentType = "application/json";

        return await controller.PatchMetadata(tripId, cancellationToken);
    }

    private static IWebHostEnvironment BuildEnvironment()
    {
        var mock = new Mock<IWebHostEnvironment>();
        mock.SetupGet(e => e.WebRootPath).Returns(Path.GetTempPath());
        return mock.Object;
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement ValidMetadataJson(
        string name = "\"New Trip\"",
        string notesHtml = "\"<p>Notes</p>\"",
        string isPublic = "true",
        string coverImage = """{ "rawUrl": "https://cdn.example.test/cover.jpg" }""",
        string center = """{ "latitude": 10, "longitude": 20 }""",
        string zoom = "9") =>
        Json($$"""
        {
          "name": {{name}},
          "notesHtml": {{notesHtml}},
          "isPublic": {{isPublic}},
          "coverImage": {{coverImage}},
          "center": {{center}},
          "zoom": {{zoom}}
        }
        """);

    private static Trip SeedTrip(
        ApplicationDbContext db,
        string userId,
        bool isPublic = false,
        bool shareProgressEnabled = false)
    {
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Test Trip",
            Notes = "Old notes",
            UpdatedAt = DateTime.UtcNow,
            IsPublic = isPublic,
            ShareProgressEnabled = shareProgressEnabled
        };

        db.Trips.Add(trip);
        db.SaveChanges();
        return trip;
    }
}
