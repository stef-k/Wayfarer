using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// Covers Trip Editor tag replacement and share-progress mutation contracts.
/// </summary>
public sealed class TripEditorTagsShareProgressControllerTests : TestBase
{
    [Fact]
    public async Task PutTagsReplacesCompleteSetAndReturnsAffectedTags()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        AddTag(db, trip, "Beta", "beta");
        AddTag(db, trip, "Old", "old");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.PutTags(trip.Id, CancellationToken.None), """{ "tags": [ "Alpha", "Beta" ] }""");

        var envelope = AssertMutation<IReadOnlyList<EditorTagDto>>(result);
        Assert.Equal(new[] { "Alpha", "Beta" }, envelope.Data.Select(t => t.Name));
        Assert.Equal(new[] { "alpha", "beta" }, envelope.Affected.TagOrder);
        Assert.Equal(new[] { "old" }, envelope.DeletedIds.Tags);
        Assert.Equal(new[] { "alpha", "beta" }, db.Trips.Single(t => t.Id == trip.Id).Tags.OrderBy(t => t.Name).Select(t => t.Slug));
    }

    [Fact]
    public async Task PutTagsClearsAllTags()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        AddTag(db, trip, "Old", "old");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.PutTags(trip.Id, CancellationToken.None), """{ "tags": [] }""");

        var envelope = AssertMutation<IReadOnlyList<EditorTagDto>>(result);
        Assert.Empty(envelope.Data);
        Assert.Empty(envelope.Affected.Tags);
        Assert.Empty(envelope.Affected.TagOrder!);
        Assert.Equal(new[] { "old" }, envelope.DeletedIds.Tags);
    }

    [Fact]
    public async Task PutTagsCollapsesDuplicateNormalizedSlugsWithFirstDisplayName()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.PutTags(trip.Id, CancellationToken.None), """{ "tags": [ "Cafe", "Café", "Trail" ] }""");

        var envelope = AssertMutation<IReadOnlyList<EditorTagDto>>(result);
        Assert.Equal(new[] { "Cafe", "Trail" }, envelope.Data.Select(t => t.Name));
        Assert.Equal(new[] { "cafe", "trail" }, envelope.Affected.TagOrder);
    }

    [Theory]
    [InlineData("""{ "tags": [ "" ] }""", "tags[0]")]
    [InlineData("""{ "tags": [ 3 ] }""", "tags[0]")]
    [InlineData("""{ "tags": [ "bad/tag" ] }""", "tags[0]")]
    [InlineData("""{ "tags": null }""", "tags")]
    [InlineData("""{ "name": "missing" }""", "tags")]
    [InlineData("""[]""", "request")]
    public async Task PutTagsReturnsValidationKeys(string body, string key)
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.PutTags(trip.Id, CancellationToken.None), body);

        Assert.Contains(key, AssertValidationProblem(result).Errors.Keys);
    }

    [Fact]
    public async Task PutTagsReturnsTagsValidationKeyWhenTagCountExceedsOptionsLimit()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");
        var tags = Enumerable.Range(1, 26).Select(index => $"Tag {index}");
        var body = "{ \"tags\": [ " + string.Join(", ", tags.Select(tag => $"\"{tag}\"")) + " ] }";

        var result = await SendJson(controller, c => c.PutTags(trip.Id, CancellationToken.None), body);

        Assert.Contains("tags", AssertValidationProblem(result).Errors.Keys);
    }

    [Fact]
    public async Task PutTagsChecksOwnershipBeforeBodyParsing()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "other-user");

        var result = await SendJson(controller, c => c.PutTags(trip.Id, CancellationToken.None), "{");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task PatchShareProgressEnablesForPublicTrip()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user", isPublic: true);
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.PatchShareProgress(trip.Id, CancellationToken.None), """{ "enabled": true }""");

        var envelope = AssertMutation<EditorTripMetadataDto>(result);
        Assert.True(envelope.Data.ShareProgressEnabled);
        Assert.NotNull(envelope.Data.ProgressPublicUrl);
        Assert.Same(envelope.Data, envelope.Affected.Metadata);
        Assert.True(db.Trips.Single(t => t.Id == trip.Id).ShareProgressEnabled);
    }

    [Fact]
    public async Task PatchShareProgressDisablesForPrivateTrip()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user", isPublic: false, shareProgressEnabled: true);
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.PatchShareProgress(trip.Id, CancellationToken.None), """{ "enabled": false }""");

        var envelope = AssertMutation<EditorTripMetadataDto>(result);
        Assert.False(envelope.Data.ShareProgressEnabled);
        Assert.Null(envelope.Data.ProgressPublicUrl);
    }

    [Fact]
    public async Task PatchShareProgressRejectsEnableForPrivateTrip()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user", isPublic: false);
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.PatchShareProgress(trip.Id, CancellationToken.None), """{ "enabled": true }""");

        Assert.Contains("shareProgressEnabled", AssertValidationProblem(result).Errors.Keys);
        Assert.False(db.Trips.Single(t => t.Id == trip.Id).ShareProgressEnabled);
    }

    [Theory]
    [InlineData("", "request")]
    [InlineData("{", "request")]
    [InlineData("[]", "request")]
    [InlineData("""{ "enabled": null }""", "enabled")]
    [InlineData("""{ "name": "missing" }""", "enabled")]
    public async Task PatchShareProgressReturnsValidationKeys(string body, string key)
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await SendJson(controller, c => c.PatchShareProgress(trip.Id, CancellationToken.None), body);

        Assert.Contains(key, AssertValidationProblem(result).Errors.Keys);
    }

    [Fact]
    public async Task PatchShareProgressChecksOwnershipBeforeBodyParsing()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, "other-user");

        var result = await SendJson(controller, c => c.PatchShareProgress(trip.Id, CancellationToken.None), "{");

        Assert.IsType<NotFoundResult>(result);
    }

    private static TripEditorController BuildController(ApplicationDbContext db)
    {
        var environment = BuildEnvironment();
        var controller = new TripEditorController(
            db,
            environment,
            new IconColorProvider(environment),
            Mock.Of<ITripMapThumbnailGenerator>(),
            Mock.Of<ICacheWarmupScheduler>(),
            new TripTagService(db, NullLogger<TripTagService>.Instance),
            new TripEditorRegionMutationService(db),
            new TripEditorPlaceMutationService(db, environment, new IconColorProvider(environment), new ReverseGeocodingService(new HttpClient(), Mock.Of<ILogger<BaseApiController>>())),
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

    private static void ConfigureControllerWithUserRole(ControllerBase controller, string userId, string role = "User")
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = BuildHttpContextWithUser(userId, role)
        };
    }

    private static EditorMutationResult<T> AssertMutation<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<EditorMutationResult<T>>(ok.Value);
    }

    private static ValidationProblemDetails AssertValidationProblem(IActionResult result)
    {
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("application/problem+json", badRequest.ContentTypes);
        return Assert.IsType<ValidationProblemDetails>(badRequest.Value);
    }

    private static void AddTag(ApplicationDbContext db, Trip trip, string name, string slug)
    {
        var tag = new Tag { Id = Guid.NewGuid(), Name = name, Slug = slug };
        trip.Tags.Add(tag);
        db.Tags.Add(tag);
        db.SaveChanges();
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

    private static IWebHostEnvironment BuildEnvironment()
    {
        var mock = new Mock<IWebHostEnvironment>();
        mock.SetupGet(e => e.WebRootPath).Returns(Path.GetTempPath());
        return mock.Object;
    }
}
