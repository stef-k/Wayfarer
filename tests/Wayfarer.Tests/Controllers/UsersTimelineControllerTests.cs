using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Public users timeline visibility checks.
/// </summary>
public class UsersTimelineControllerTests : TestBase
{
    [Fact]
    public async Task Index_ReturnsNotFound_WhenTimelineNotPublic()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        user.IsTimelinePublic = false;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Index("alice");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Index_ReturnsBadRequest_WhenUsernameMissing()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.Index(string.Empty);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Index_ReturnsView_ForPublicTimeline()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice", displayName: "Alice");
        user.IsTimelinePublic = true;
        user.PublicTimelineTimeThreshold = "1d";
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Index("alice");

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Timeline", view.ViewName);
        Assert.Equal("alice", controller.ViewData["Username"]);
        Assert.Equal("Timeline of Alice", controller.ViewData["TimelineTitle"]);
    }

    [Fact]
    public async Task Index_UsesCustomTimelineTitle_ForPublicTimeline()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice", displayName: "Alice");
        user.IsTimelinePublic = true;
        user.PublicTimelineTimeThreshold = "1d";
        user.TimelineTitle = "Alice's public map";
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Index("alice");

        Assert.IsType<ViewResult>(result);
        Assert.Equal("Alice's public map", controller.ViewData["TimelineTitle"]);
    }

    [Fact]
    public async Task Embed_ReturnsView_ForPublicTimeline()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice", displayName: "Alice");
        user.IsTimelinePublic = true;
        user.PublicTimelineTimeThreshold = "1d";
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Embed("alice");

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Embed", view.ViewName);
        var isEmbed = controller.ViewBag.IsEmbed;
        Assert.True(isEmbed == null || (bool)isEmbed);
        Assert.Equal("Timeline of Alice", controller.ViewData["TimelineTitle"]);
    }

    [Fact]
    public async Task Embed_ReturnsNotFound_WhenUserIsMissingOrTimelineIsNotPublic()
    {
        var db = CreateDbContext();
        var privateUser = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        privateUser.TimelineTitle = "Private title";
        db.Users.Add(privateUser);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        Assert.IsType<NotFoundObjectResult>(await controller.Embed("alice"));
        Assert.IsType<NotFoundObjectResult>(await controller.Embed("missing"));
    }

    [Fact]
    public async Task Embed_UsesCustomTimelineTitle_ForPublicTimeline()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice", displayName: "Alice");
        user.IsTimelinePublic = true;
        user.PublicTimelineTimeThreshold = "1d";
        user.TimelineTitle = "Alice's embedded map";
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Embed("alice");

        Assert.IsType<ViewResult>(result);
        Assert.Equal("Alice's embedded map", controller.ViewData["TimelineTitle"]);
    }

    [Fact]
    public async Task GetPublicStats_ReturnsBadRequest_WhenUsernameMissing()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.GetPublicStats(string.Empty);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetPublicStats_ReturnsNotFound_WhenTimelineNotPublic()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(username: "alice");
        user.IsTimelinePublic = false;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.GetPublicStats("alice");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetPublicStats_ReturnsStats_WhenTimelinePublic()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        user.IsTimelinePublic = true;
        user.PublicTimelineTimeThreshold = "1d";
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var statsService = new StubStatsService();
        var controller = new UsersTimelineController(
            NullLogger<BaseController>.Instance,
            db,
            new LocationService(db),
            statsService);

        var result = await controller.GetPublicStats("alice");

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<UserLocationStatsDto>(ok.Value);
        Assert.Equal(99, dto.TotalLocations);
    }

    [Fact]
    public async Task GetPublicTimeline_ReturnsNotFound_WhenUserMissingOrPrivate()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);
        var request = new LocationFilterRequest
        {
            Username = "missing",
            MinLatitude = -1,
            MinLongitude = -1,
            MaxLatitude = 1,
            MaxLongitude = 1,
            ZoomLevel = 5
        };

        var result = await controller.GetPublicTimeline(request);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad")]
    [InlineData("1z")]
    public async Task PublicRoutes_FailClosed_ForInvalidStoredThreshold(string? threshold)
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        user.IsTimelinePublic = true;
        user.PublicTimelineTimeThreshold = threshold;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        Assert.IsType<NotFoundObjectResult>(await controller.Index("alice"));
        Assert.IsType<NotFoundObjectResult>(await controller.Embed("alice"));
        Assert.IsType<NotFoundObjectResult>(await controller.GetPublicStats("alice"));
        Assert.IsType<NotFoundObjectResult>(await controller.GetPublicTimeline(CreateRequest("alice")));
    }

    [Theory]
    [InlineData("1d", false)]
    [InlineData("1.5w", false)]
    [InlineData("now", true)]
    public async Task PublicShells_ExposeValidatedLiveState(string threshold, bool isLive)
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        user.IsTimelinePublic = true;
        user.PublicTimelineTimeThreshold = threshold;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        Assert.IsType<ViewResult>(await controller.Index("alice"));
        Assert.Equal(isLive, controller.ViewData["TimelineLive"]);
        Assert.IsType<ViewResult>(await controller.Embed("alice"));
        Assert.Equal(isLive, controller.ViewData["TimelineLive"]);
    }

    [Fact]
    public async Task GetPublicTimeline_ReturnsDataEnvelope_ForValidatedCustomThreshold()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        user.IsTimelinePublic = true;
        user.PublicTimelineTimeThreshold = "1.5w";
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.GetPublicTimeline(CreateRequest("alice"));

        Assert.IsType<OkObjectResult>(result);
    }

    private static LocationFilterRequest CreateRequest(string username) => new()
    {
        Username = username,
        MinLatitude = -1,
        MinLongitude = -1,
        MaxLatitude = 1,
        MaxLongitude = 1,
        ZoomLevel = 5
    };

    private static UsersTimelineController BuildController(ApplicationDbContext db)
    {
        // locationService and statsService are unused in these actions; keep defaults.
        return new UsersTimelineController(
            NullLogger<BaseController>.Instance,
            db,
            new LocationService(db),
            new LocationStatsService(db));
    }

    private sealed class StubStatsService : ILocationStatsService
    {
        public Task<UserLocationStatsDto> GetStatsForUserAsync(string userId) =>
            Task.FromResult(new UserLocationStatsDto { TotalLocations = 99 });

        public Task<UserLocationStatsDto> GetStatsForDateRangeAsync(string userId, DateTime startDate, DateTime endDate) =>
            Task.FromResult(new UserLocationStatsDto());

        public Task<UserLocationStatsDetailedDto> GetDetailedStatsForUserAsync(string userId) =>
            Task.FromResult(new UserLocationStatsDetailedDto());

        public Task<UserLocationStatsDetailedDto> GetDetailedStatsForDateRangeAsync(string userId, DateTime startDate, DateTime endDate) =>
            Task.FromResult(new UserLocationStatsDetailedDto());
    }
}
