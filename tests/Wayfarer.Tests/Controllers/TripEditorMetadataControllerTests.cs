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
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Focused tests for the Trip Editor metadata mutation endpoint.
/// </summary>
public sealed class TripEditorMetadataControllerTests : TestBase
{
    [Fact]
    public async Task PatchMetadataForOwnerUpdatesMetadataAndReturnsMetadataOnlyEnvelope()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var thumbnailMock = new Mock<ITripMapThumbnailGenerator>();
        var warmupMock = new Mock<ICacheWarmupScheduler>();
        var controller = BuildController(db, thumbnailMock, warmupMock);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await PatchMetadata(controller, trip.Id, Json("""
            {
              "name": " Updated Trip ",
              "notesHtml": "<p>Hello <img src=\"https://cdn.example.test/a.jpg\"></p>",
              "isPublic": true,
              "coverImage": { "rawUrl": " https://cdn.example.test/cover.jpg " },
              "center": { "latitude": 12.5, "longitude": 23.5 },
              "zoom": 8
            }
            """), CancellationToken.None);

        var envelope = AssertMutation(result);
        Assert.True(envelope.Success);
        Assert.Equal("Updated Trip", envelope.Data.Name);
        Assert.True(envelope.Data.IsPublic);
        Assert.Equal("https://cdn.example.test/cover.jpg", envelope.Data.CoverImage!.RawUrl);
        Assert.Equal(12.5, envelope.Data.Center!.Latitude);
        Assert.Equal(23.5, envelope.Data.Center.Longitude);
        Assert.Equal(8, envelope.Data.Zoom);
        Assert.Same(envelope.Data, envelope.Affected.Metadata);
        Assert.Empty(envelope.Affected.Regions);
        Assert.Null(envelope.Affected.RegionOrder);
        Assert.Empty(envelope.Affected.Places);
        Assert.Empty(envelope.Affected.Areas);
        Assert.Empty(envelope.Affected.Segments);
        Assert.Null(envelope.Affected.VisitProgress);
        Assert.Empty(envelope.DeletedIds.Regions);
        Assert.Empty(envelope.Warnings);
        thumbnailMock.Verify(t => t.InvalidateThumbnails(trip.Id, It.IsAny<DateTime>()), Times.Once);
        warmupMock.Verify(w => w.ScheduleWarmupAsync(trip.Id, true), Times.Once);
    }

    [Fact]
    public async Task PatchMetadataNullNotesStoresEmptyString()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await PatchMetadata(controller, trip.Id, ValidMetadataJson(notesHtml: "null"), CancellationToken.None);

        var envelope = AssertMutation(result);
        Assert.Equal(string.Empty, envelope.Data.NotesHtml);
        Assert.Equal(string.Empty, db.Trips.Single(t => t.Id == trip.Id).Notes);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{ "rawUrl": null }""")]
    [InlineData("""{ "rawUrl": "   " }""")]
    public async Task PatchMetadataClearsCoverImage(string coverImageJson)
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        trip.CoverImageUrl = "https://cdn.example.test/old.jpg";
        db.SaveChanges();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await PatchMetadata(controller, trip.Id, ValidMetadataJson(coverImage: coverImageJson), CancellationToken.None);

        var envelope = AssertMutation(result);
        Assert.Null(envelope.Data.CoverImage);
        Assert.Null(db.Trips.Single(t => t.Id == trip.Id).CoverImageUrl);
    }

    [Fact]
    public async Task PatchMetadataClearsCenterAndZoom()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        trip.CenterLat = 1;
        trip.CenterLon = 2;
        trip.Zoom = 10;
        db.SaveChanges();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await PatchMetadata(controller, trip.Id, ValidMetadataJson(center: "null", zoom: "null"), CancellationToken.None);

        var envelope = AssertMutation(result);
        var stored = db.Trips.Single(t => t.Id == trip.Id);
        Assert.Null(envelope.Data.Center);
        Assert.Null(envelope.Data.Zoom);
        Assert.Null(stored.CenterLat);
        Assert.Null(stored.CenterLon);
        Assert.Null(stored.Zoom);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PatchMetadataPrivateSaveDisablesShareProgressWarningOnlyWhenApplicable(bool wasShareProgressEnabled)
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user", isPublic: true, shareProgressEnabled: wasShareProgressEnabled);
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await PatchMetadata(controller, trip.Id, ValidMetadataJson(isPublic: "false"), CancellationToken.None);

        var envelope = AssertMutation(result);
        Assert.False(envelope.Data.IsPublic);
        Assert.False(envelope.Data.ShareProgressEnabled);
        Assert.Equal(wasShareProgressEnabled ? 1 : 0, envelope.Warnings.Count);
        if (wasShareProgressEnabled)
        {
            var warning = envelope.Warnings.Single();
            Assert.Equal("share-progress-disabled", warning.Code);
            Assert.Equal("Share progress was disabled because the trip is private.", warning.Message);
            Assert.Equal("trip", warning.EntityType);
            Assert.Equal(trip.Id.ToString(), warning.EntityId);
        }
    }

    [Fact]
    public async Task PatchMetadataUpdatesUpdatedAt()
    {
        using var db = CreateDbContext();
        var oldTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var trip = SeedTrip(db, "owner-user");
        trip.UpdatedAt = oldTime;
        db.SaveChanges();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await PatchMetadata(controller, trip.Id, ValidMetadataJson(), CancellationToken.None);

        var envelope = AssertMutation(result);
        Assert.True(envelope.Data.UpdatedAt > oldTime);
        Assert.True(db.Trips.Single(t => t.Id == trip.Id).UpdatedAt > oldTime);
    }

    [Fact]
    public async Task PatchMetadataWarmupUsesNonImmediateWhenImagesAlreadyKnown()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        trip.CoverImageUrl = "https://cdn.example.test/existing.jpg";
        db.SaveChanges();
        var warmupMock = new Mock<ICacheWarmupScheduler>();
        var controller = BuildController(db, warmupMock: warmupMock);
        ConfigureControllerWithUserRole(controller, "owner-user");

        await PatchMetadata(
            controller,
            trip.Id,
            ValidMetadataJson(coverImage: """{ "rawUrl": "https://cdn.example.test/existing.jpg" }"""),
            CancellationToken.None);

        warmupMock.Verify(w => w.ScheduleWarmupAsync(trip.Id, false), Times.Once);
    }

    private static void ConfigureControllerWithUserRole(ControllerBase controller, string userId, string role = "User")
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = BuildHttpContextWithUser(userId, role)
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
            Mock.Of<ILogger<TripEditorController>>());

        var url = new Mock<IUrlHelper>();
        url.Setup(u => u.Action(It.IsAny<UrlActionContext>()))
            .Returns((UrlActionContext context) =>
            {
                var id = context.Values?.GetType().GetProperty("id")?.GetValue(context.Values);
                var progress = context.Values?.GetType().GetProperty("progress")?.GetValue(context.Values);
                return progress == null
                    ? $"https://example.test/Public/Trips/{id}"
                    : $"https://example.test/Public/Trips/{id}?progress={progress}";
            });
        controller.Url = url.Object;
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

    private static EditorMutationResult<EditorTripMetadataDto> AssertMutation(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<EditorMutationResult<EditorTripMetadataDto>>(ok.Value);
    }

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
