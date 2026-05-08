using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging;
using Moq;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using System.Text.Json;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Focused tests for the read-only Trip Editor API spike.
/// </summary>
public sealed class TripEditorControllerTests : TestBase
{
    [Fact]
    public async Task GetEditorStateWithoutUserReturnsUnauthorized()
    {
        using var db = CreateDbContext();
        var controller = BuildController(db);

        var result = await controller.GetEditorState(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task GetEditorStateForNonOwnerReturnsForbidden()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "other-user");

        var result = await controller.GetEditorState(trip.Id, CancellationToken.None);

        AssertForbiddenStatus(result);
    }

    [Fact]
    public async Task GetEditorStateWithoutUserRoleReturnsForbidden()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user", "Manager");

        var result = await controller.GetEditorState(trip.Id, CancellationToken.None);

        AssertForbiddenStatus(result);
    }

    [Fact]
    public async Task GetEditorStateForMissingOwnedScopeTripReturnsNotFound()
    {
        using var db = CreateDbContext();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await controller.GetEditorState(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetEditorStateForOwnerReturnsCompleteReadState()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await controller.GetEditorState(trip.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var state = Assert.IsType<EditorTripStateDto>(ok.Value);
        Assert.Equal(trip.Id, state.TripId);
        Assert.Equal(trip.Name, state.Metadata.Name);
        Assert.Null(state.Metadata.PublicUrl);
        Assert.Null(state.Metadata.ProgressPublicUrl);
        Assert.Equal(2, state.RegionOrder.Count);
        Assert.Equal(2, state.RegionsById.Count);
        Assert.Single(state.PlacesById);
        Assert.Single(state.AreasById);
        Assert.Single(state.SegmentsById);
        Assert.Single(state.TagsBySlug);
        Assert.Equal(1, state.VisitProgress.TotalPlaces);
        Assert.Equal(1, state.VisitProgress.VisitedPlaces);
        Assert.Equal(100, state.VisitProgress.PercentVisited);
        Assert.Equal(3, state.VisitProgress.HistoryRows.Count);
        Assert.Equal(30, state.VisitProgress.HistoryRows[0].DurationMinutes);
        Assert.Null(state.VisitProgress.HistoryRows[1].DurationMinutes);
        Assert.True(state.VisitProgress.HistoryRows[0].VisitId.CompareTo(state.VisitProgress.HistoryRows[1].VisitId) < 0);
        Assert.Equal("Polygon", state.AreasById.Values.Single().Geometry.GetProperty("type").GetString());
        Assert.NotNull(state.SegmentsById.Values.Single().Route);
        Assert.NotNull(state.Options);
        Assert.True(state.Permissions.CanEditTrip);
        Assert.True(state.Permissions.CanToggleShareProgress);
        Assert.All(state.Permissions.GetType().GetProperties(), property => Assert.True((bool)property.GetValue(state.Permissions)!));
    }

    [Fact]
    public async Task GetEditorStateForPrivateTripReturnsNullPublicUrls()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user", isPublic: false, shareProgressEnabled: true);
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await controller.GetEditorState(trip.Id, CancellationToken.None);

        var metadata = Assert.IsType<EditorTripStateDto>(Assert.IsType<OkObjectResult>(result).Value).Metadata;
        Assert.Null(metadata.PublicUrl);
        Assert.Null(metadata.ProgressPublicUrl);
    }

    [Fact]
    public async Task GetEditorStateForPublicTripWithProgressDisabledReturnsOnlyPublicUrl()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user", isPublic: true, shareProgressEnabled: false);
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await controller.GetEditorState(trip.Id, CancellationToken.None);

        var metadata = Assert.IsType<EditorTripStateDto>(Assert.IsType<OkObjectResult>(result).Value).Metadata;
        Assert.Equal("https://example.test/Public/Trips/" + trip.Id, metadata.PublicUrl);
        Assert.Null(metadata.ProgressPublicUrl);
    }

    [Fact]
    public async Task GetEditorStateForPublicTripWithProgressEnabledReturnsBothPublicUrls()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user", isPublic: true, shareProgressEnabled: true);
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await controller.GetEditorState(trip.Id, CancellationToken.None);

        var metadata = Assert.IsType<EditorTripStateDto>(Assert.IsType<OkObjectResult>(result).Value).Metadata;
        Assert.Equal("https://example.test/Public/Trips/" + trip.Id, metadata.PublicUrl);
        Assert.Equal("https://example.test/Public/Trips/" + trip.Id + "?progress=1", metadata.ProgressPublicUrl);
    }

    [Fact]
    public async Task GetEditorStateMapsCoordinatesAndGeoJsonWithExpectedShapes()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await controller.GetEditorState(trip.Id, CancellationToken.None);

        var state = Assert.IsType<EditorTripStateDto>(Assert.IsType<OkObjectResult>(result).Value);
        var place = state.PlacesById.Values.Single();
        Assert.Equal(37.9715, place.Location!.Latitude, 4);
        Assert.Equal(23.7261, place.Location.Longitude, 4);

        var areaCoordinates = state.AreasById.Values.Single().Geometry.GetProperty("coordinates")[0][0];
        Assert.Equal(23.72, areaCoordinates[0].GetDouble(), 2);
        Assert.Equal(37.97, areaCoordinates[1].GetDouble(), 2);

        var route = state.SegmentsById.Values.Single().Route!.Value;
        Assert.Equal("LineString", route.GetProperty("type").GetString());
        var routeCoordinates = route.GetProperty("coordinates")[0];
        Assert.Equal(23.7261, routeCoordinates[0].GetDouble(), 4);
        Assert.Equal(37.9715, routeCoordinates[1].GetDouble(), 4);
    }

    [Fact]
    public async Task GetEditorStateReturnsShadowRegionCapabilitiesWithoutNameInferenceInFrontendState()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await controller.GetEditorState(trip.Id, CancellationToken.None);

        var state = Assert.IsType<EditorTripStateDto>(Assert.IsType<OkObjectResult>(result).Value);
        var shadow = Assert.Single(state.RegionsById.Values, r => r.IsShadow);
        Assert.Equal(0, shadow.DisplayOrder);
        Assert.False(shadow.Capabilities.CanRename);
        Assert.False(shadow.Capabilities.CanDelete);
        Assert.False(shadow.Capabilities.CanReorder);
        Assert.False(shadow.Capabilities.CanAddChildren);
        Assert.True(shadow.Capabilities.CanTargetForSearchAdd);
    }

    [Fact]
    public async Task GetEditorStateReturnsDeterministicOptions()
    {
        var webRoot = CreateIconWebRoot();
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db, webRoot);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await controller.GetEditorState(trip.Id, CancellationToken.None);

        var options = Assert.IsType<EditorTripStateDto>(Assert.IsType<OkObjectResult>(result).Value).Options;
        Assert.Equal(new[] { "alpha", "zulu" }, options.IconNames);
        Assert.Equal(new[] { "bg-blue", "bg-red" }, options.MarkerColorClasses);
        Assert.Equal(new[] { "color-white", "color-yellow" }, options.GlyphColorClasses);
        Assert.Equal(SegmentTransportModes.Options.Select(m => m.Value), options.TransportModes.Select(m => m.Value));
        Assert.Equal(25, options.Tag.MaxTags);
        Assert.Equal(8, options.Tag.SuggestionTake);
        Assert.Equal(6, options.Limits.NominatimSearchLimit);
        Assert.Equal(1, options.Limits.SidebarSearchMinCharacters);
    }

    [Fact]
    public async Task GetEditorStateColorOptionsMatchIconColorsApiOrder()
    {
        var webRoot = CreateIconWebRoot();
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var editorController = BuildController(db, webRoot);
        var iconsController = new IconsController(db, Mock.Of<ILogger<IconsController>>(), BuildEnvironment(webRoot), new IconColorProvider(BuildEnvironment(webRoot)));
        ConfigureControllerWithUserRole(editorController, "owner-user");

        var editorResult = await editorController.GetEditorState(trip.Id, CancellationToken.None);
        var iconsResult = iconsController.GetAvailableColors();

        var options = Assert.IsType<EditorTripStateDto>(Assert.IsType<OkObjectResult>(editorResult).Value).Options;
        var apiColors = Assert.IsType<OkObjectResult>(iconsResult).Value!;
        var backgrounds = Assert.IsAssignableFrom<IReadOnlyList<string>>(apiColors.GetType().GetProperty("backgrounds")?.GetValue(apiColors));
        var glyphs = Assert.IsAssignableFrom<IReadOnlyList<string>>(apiColors.GetType().GetProperty("glyphs")?.GetValue(apiColors));
        Assert.Equal(backgrounds, options.MarkerColorClasses);
        Assert.Equal(glyphs, options.GlyphColorClasses);
    }

    [Fact]
    public async Task GetEditorStateWithMissingAreaGeometryReturnsProblemDetails()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var area = trip.Regions.SelectMany(r => r.Areas).Single();
        area.Geometry = null!;
        db.SaveChanges();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await controller.GetEditorState(trip.Id, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
        Assert.Contains("application/problem+json", objectResult.ContentTypes);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal("https://wayfarer/errors/editor-invalid-area-geometry", problem.Type);
        Assert.Equal(area.Id, problem.Extensions["areaId"]);
        Assert.Equal(trip.Id, problem.Extensions["tripId"]);
    }

    [Fact]
    public async Task PatchMetadataForOwnerUpdatesMetadataAndReturnsMetadataOnlyEnvelope()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var thumbnailMock = new Mock<ITripMapThumbnailGenerator>();
        var warmupMock = new Mock<ICacheWarmupScheduler>();
        var controller = BuildController(db, thumbnailMock: thumbnailMock, warmupMock: warmupMock);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await controller.PatchMetadata(trip.Id, Json("""
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
    public async Task PatchMetadataWithoutAuthenticatedUserReturnsUnauthorized()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);

        var result = await controller.PatchMetadata(trip.Id, ValidMetadataJson(), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task PatchMetadataWithoutUserRoleReturnsForbidden()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user", "Manager");

        var result = await controller.PatchMetadata(trip.Id, ValidMetadataJson(), CancellationToken.None);

        AssertForbiddenStatus(result);
    }

    [Fact]
    public async Task PatchMetadataForNonOwnerOrMissingTripReturnsNotFound()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "other-user");

        var nonOwner = await controller.PatchMetadata(trip.Id, ValidMetadataJson(), CancellationToken.None);
        var missing = await controller.PatchMetadata(Guid.NewGuid(), ValidMetadataJson(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(nonOwner);
        Assert.IsType<NotFoundResult>(missing);
    }

    [Fact]
    public async Task PatchMetadataRequiresCompleteDraftTopLevelFields()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await controller.PatchMetadata(trip.Id, Json("""{ "name": "Only name" }"""), CancellationToken.None);

        var problem = AssertValidationProblem(result);
        Assert.Contains("notesHtml", problem.Errors.Keys);
        Assert.Contains("isPublic", problem.Errors.Keys);
        Assert.Contains("coverImage", problem.Errors.Keys);
        Assert.Contains("center", problem.Errors.Keys);
        Assert.Contains("zoom", problem.Errors.Keys);
    }

    [Fact]
    public async Task PatchMetadataNullNotesStoresEmptyString()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await controller.PatchMetadata(trip.Id, ValidMetadataJson(notesHtml: "null"), CancellationToken.None);

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

        var result = await controller.PatchMetadata(trip.Id, ValidMetadataJson(coverImage: coverImageJson), CancellationToken.None);

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

        var result = await controller.PatchMetadata(trip.Id, ValidMetadataJson(center: "null", zoom: "null"), CancellationToken.None);

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

        var result = await controller.PatchMetadata(trip.Id, ValidMetadataJson(isPublic: "false"), CancellationToken.None);

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
    public async Task PatchMetadataReturnsValidationProblemForInvalidFields()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await controller.PatchMetadata(trip.Id, Json("""
            {
              "name": " ",
              "notesHtml": "<p><img src=\"data:image/png;base64,abc\"></p>",
              "isPublic": true,
              "coverImage": { "rawUrl": "ftp://example.test/a.jpg" },
              "center": { "latitude": 91, "longitude": -181 },
              "zoom": 3.5
            }
            """), CancellationToken.None);

        var problem = AssertValidationProblem(result);
        Assert.Contains("name", problem.Errors.Keys);
        Assert.Contains("notesHtml", problem.Errors.Keys);
        Assert.Contains("coverImage.rawUrl", problem.Errors.Keys);
        Assert.Contains("center.latitude", problem.Errors.Keys);
        Assert.Contains("center.longitude", problem.Errors.Keys);
        Assert.Contains("zoom", problem.Errors.Keys);
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

        var result = await controller.PatchMetadata(trip.Id, ValidMetadataJson(), CancellationToken.None);

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

        await controller.PatchMetadata(
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

    private static void AssertForbiddenStatus(IActionResult result)
    {
        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }

    private static TripEditorController BuildController(
        ApplicationDbContext db,
        string? webRoot = null,
        Mock<ITripMapThumbnailGenerator>? thumbnailMock = null,
        Mock<ICacheWarmupScheduler>? warmupMock = null)
    {
        var environment = BuildEnvironment(webRoot);
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

    private static ValidationProblemDetails AssertValidationProblem(IActionResult result)
    {
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("application/problem+json", badRequest.ContentTypes);
        return Assert.IsType<ValidationProblemDetails>(badRequest.Value);
    }

    private static IWebHostEnvironment BuildEnvironment(string? webRoot = null)
    {
        var mock = new Mock<IWebHostEnvironment>();
        mock.SetupGet(e => e.WebRootPath).Returns(webRoot ?? Path.GetTempPath());
        return mock.Object;
    }

    private static string CreateIconWebRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var markerDir = Path.Combine(root, "icons", "wayfarer-map-icons", "dist", "marker");
        Directory.CreateDirectory(markerDir);
        File.WriteAllText(Path.Combine(markerDir, "zulu.svg"), "<svg />");
        File.WriteAllText(Path.Combine(markerDir, "alpha.svg"), "<svg />");
        File.WriteAllText(
            Path.Combine(root, "icons", "wayfarer-map-icons", "dist", "wayfarer-map-icons.css"),
            ".bg-red{}\n.bg-blue{}\n.bg-red{}\n.color-yellow{}\n.color-white{}");
        return root;
    }

    private static Trip SeedTrip(ApplicationDbContext db, string userId, bool isPublic = false, bool shareProgressEnabled = false)
    {
        var factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Test Trip",
            UpdatedAt = DateTime.UtcNow,
            IsPublic = isPublic,
            ShareProgressEnabled = shareProgressEnabled,
            Tags = new List<Tag> { new() { Id = Guid.NewGuid(), Name = "Museum", Slug = "museum" } }
        };
        var shadowRegion = new Region
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = userId,
            Name = "Unassigned Places",
            DisplayOrder = 0
        };
        var region = new Region
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = userId,
            Name = "Athens",
            DisplayOrder = 1,
            Center = factory.CreatePoint(new Coordinate(23.7275, 37.9838))
        };
        var place = new Place
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RegionId = region.Id,
            Name = "Acropolis",
            Location = factory.CreatePoint(new Coordinate(23.7261, 37.9715)),
            DisplayOrder = 1,
            IconName = "marker",
            MarkerColor = "bg-blue"
        };
        var area = new Area
        {
            Id = Guid.NewGuid(),
            RegionId = region.Id,
            Name = "Center",
            DisplayOrder = 1,
            FillHex = "#ff6600",
            Geometry = factory.CreatePolygon(new[]
            {
                new Coordinate(23.72, 37.97),
                new Coordinate(23.73, 37.97),
                new Coordinate(23.73, 37.98),
                new Coordinate(23.72, 37.97)
            })
        };
        var segment = new Segment
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = userId,
            FromPlaceId = place.Id,
            ToPlaceId = place.Id,
            Mode = "walk",
            DisplayOrder = 1,
            RouteGeometry = factory.CreateLineString(new[]
            {
                new Coordinate(23.7261, 37.9715),
                new Coordinate(23.7275, 37.9838)
            })
        };
        var visit = new PlaceVisitEvent
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000003"),
            UserId = userId,
            PlaceId = place.Id,
            ArrivedAtUtc = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc),
            EndedAtUtc = new DateTime(2026, 1, 1, 8, 45, 0, DateTimeKind.Utc)
        };
        var latestVisit = new PlaceVisitEvent
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            UserId = userId,
            PlaceId = place.Id,
            ArrivedAtUtc = new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc),
            EndedAtUtc = new DateTime(2026, 1, 2, 9, 30, 59, DateTimeKind.Utc)
        };
        var latestTieVisit = new PlaceVisitEvent
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            UserId = userId,
            PlaceId = place.Id,
            ArrivedAtUtc = new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc),
            EndedAtUtc = null
        };

        trip.Regions.Add(shadowRegion);
        trip.Regions.Add(region);
        trip.Segments.Add(segment);
        region.Places.Add(place);
        region.Areas.Add(area);

        db.Trips.Add(trip);
        db.PlaceVisitEvents.Add(visit);
        db.PlaceVisitEvents.Add(latestVisit);
        db.PlaceVisitEvents.Add(latestTieVisit);
        db.SaveChanges();
        return trip;
    }
}
