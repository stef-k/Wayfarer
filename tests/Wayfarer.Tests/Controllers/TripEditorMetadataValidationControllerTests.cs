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

    /// <summary>Empty, malformed and server-owned payloads cannot enter an unconditional metadata save.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"userId\":\"other-user\",\"name\":\"Bypass\"}")]
    public async Task PatchMetadataForOwnedTripRejectsWithoutParsingOrMutation(string requestBody)
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");
        var result = await PatchMetadata(controller, trip.Id, requestBody, CancellationToken.None);
        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(db.ChangeTracker.HasChanges());
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
            new TripEditorAreaMutationService(db),
            new TripEditorSegmentMutationService(db),
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
