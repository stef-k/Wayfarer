using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Focused validation, authorization, and ownership tests for Trip Editor metadata mutations.
/// </summary>
public sealed class TripEditorMetadataValidationControllerTests : TestBase
{
    [Fact]
    public async Task PatchMetadataWithoutAuthenticatedUserReturnsUnauthorized()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);

        var result = await PatchMetadata(controller, trip.Id, ValidMetadataJson(), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task PatchMetadataWithoutUserRoleReturnsForbidden()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user", "Manager");

        var result = await PatchMetadata(controller, trip.Id, ValidMetadataJson(), CancellationToken.None);

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }

    [Fact]
    public async Task PatchMetadataForNonOwnerOrMissingTripReturnsNotFound()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "other-user");

        var nonOwner = await PatchMetadata(controller, trip.Id, ValidMetadataJson(), CancellationToken.None);
        var missing = await PatchMetadata(controller, Guid.NewGuid(), ValidMetadataJson(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(nonOwner);
        Assert.IsType<NotFoundResult>(missing);
    }

    /// <summary>
    /// Verifies missing trips are hidden before complete-draft validation runs.
    /// </summary>
    [Fact]
    public async Task PatchMetadataForMissingTripWithIncompleteDraftReturnsNotFound()
    {
        using var db = CreateDbContext();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await PatchMetadata(controller, Guid.NewGuid(), IncompleteMetadataJson(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    /// <summary>
    /// Verifies non-owned trips are hidden before complete-draft validation runs.
    /// </summary>
    [Fact]
    public async Task PatchMetadataForNonOwnedTripWithIncompleteDraftReturnsNotFound()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "other-user");

        var result = await PatchMetadata(controller, trip.Id, IncompleteMetadataJson(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    /// <summary>
    /// Verifies missing and non-owned trips are hidden before body JSON parsing runs.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("{")]
    public async Task PatchMetadataForMissingOrNonOwnedTripWithInvalidBodyReturnsNotFound(string requestBody)
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "other-user");

        var nonOwner = await PatchMetadata(controller, trip.Id, requestBody, CancellationToken.None);
        var missing = await PatchMetadata(controller, Guid.NewGuid(), requestBody, CancellationToken.None);

        Assert.IsType<NotFoundResult>(nonOwner);
        Assert.IsType<NotFoundResult>(missing);
    }

    /// <summary>
    /// Verifies owned trips return stable request-keyed validation errors for invalid bodies.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    public async Task PatchMetadataForOwnedTripWithInvalidBodyReturnsRequestValidationProblem(string requestBody)
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await PatchMetadata(controller, trip.Id, requestBody, CancellationToken.None);

        var problem = AssertValidationProblem(result);
        Assert.Contains("request", problem.Errors.Keys);
    }

    /// <summary>
    /// Verifies owned trips still validate complete metadata drafts after ownership is confirmed.
    /// </summary>
    [Fact]
    public async Task PatchMetadataRequiresCompleteDraftTopLevelFields()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await PatchMetadata(controller, trip.Id, Json("""{ "name": "Only name" }"""), CancellationToken.None);

        var problem = AssertValidationProblem(result);
        Assert.Contains("notesHtml", problem.Errors.Keys);
        Assert.Contains("isPublic", problem.Errors.Keys);
        Assert.Contains("coverImage", problem.Errors.Keys);
        Assert.Contains("center", problem.Errors.Keys);
        Assert.Contains("zoom", problem.Errors.Keys);
    }

    [Fact]
    public async Task PatchMetadataReturnsValidationProblemForInvalidFields()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await PatchMetadata(controller, trip.Id, Json("""
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

    /// <summary>
    /// Verifies server-owned top-level fields are rejected and cannot change persisted metadata.
    /// </summary>
    [Theory]
    [InlineData("shareProgressEnabled", "false")]
    [InlineData("publicUrl", "\"https://attacker.example.test/trip\"")]
    [InlineData("progressPublicUrl", "\"https://attacker.example.test/progress\"")]
    [InlineData("updatedAt", "\"2030-01-01T00:00:00Z\"")]
    public async Task PatchMetadataRejectsServerOwnedField(string fieldName, string fieldValue)
    {
        using var db = CreateDbContext();
        var oldTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var trip = SeedTrip(db, "owner-user", isPublic: true, shareProgressEnabled: true);
        trip.UpdatedAt = oldTime;
        db.SaveChanges();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await PatchMetadata(controller, trip.Id, ValidMetadataBody($"""
          "{fieldName}": {fieldValue}
        """), CancellationToken.None);

        var problem = AssertValidationProblem(result);
        Assert.Contains(fieldName, problem.Errors.Keys);
        var stored = db.Trips.Single(t => t.Id == trip.Id);
        Assert.Equal("Test Trip", stored.Name);
        Assert.True(stored.IsPublic);
        Assert.True(stored.ShareProgressEnabled);
        Assert.Equal(oldTime, stored.UpdatedAt);
    }

    /// <summary>
    /// Verifies multiple server-owned fields produce deterministic field-keyed errors together.
    /// </summary>
    [Fact]
    public async Task PatchMetadataRejectsMultipleServerOwnedFields()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await PatchMetadata(controller, trip.Id, ValidMetadataBody("""
          "shareProgressEnabled": true,
          "publicUrl": "https://attacker.example.test/trip",
          "progressPublicUrl": "https://attacker.example.test/progress",
          "updatedAt": "2030-01-01T00:00:00Z"
        """), CancellationToken.None);

        var problem = AssertValidationProblem(result);
        Assert.Contains("shareProgressEnabled", problem.Errors.Keys);
        Assert.Contains("publicUrl", problem.Errors.Keys);
        Assert.Contains("progressPublicUrl", problem.Errors.Keys);
        Assert.Contains("updatedAt", problem.Errors.Keys);
    }

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
            Mock.Of<ITripTagService>(),
            new TripEditorRegionMutationService(db),
            new TripEditorPlaceMutationService(db, environment, new IconColorProvider(environment), new ReverseGeocodingService(new HttpClient(), Mock.Of<ILogger<BaseApiController>>())),
            Mock.Of<ILogger<TripEditorController>>());
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

    private static string ValidMetadataBody(string extraTopLevelFields) =>
        $$"""
        {
          "name": "Attempted Change",
          "notesHtml": "<p>Changed</p>",
          "isPublic": false,
          "coverImage": { "rawUrl": "https://cdn.example.test/changed.jpg" },
          "center": { "latitude": 10, "longitude": 20 },
          "zoom": 9,
        {{extraTopLevelFields}}
        }
        """;

    private static JsonElement ValidMetadataJson() =>
        Json("""
        {
          "name": "New Trip",
          "notesHtml": "<p>Notes</p>",
          "isPublic": true,
          "coverImage": { "rawUrl": "https://cdn.example.test/cover.jpg" },
          "center": { "latitude": 10, "longitude": 20 },
          "zoom": 9
        }
        """);

    private static JsonElement IncompleteMetadataJson() => Json("""{ "name": "Only name" }""");

    private static ValidationProblemDetails AssertValidationProblem(IActionResult result)
    {
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("application/problem+json", badRequest.ContentTypes);
        return Assert.IsType<ValidationProblemDetails>(badRequest.Value);
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
