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
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Index("alice");

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Timeline", view.ViewName);
        Assert.Equal("alice", controller.ViewData["Username"]);
    }

    [Fact]
    public async Task Embed_ReturnsView_ForPublicTimeline()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice", displayName: "Alice");
        user.IsTimelinePublic = true;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Embed("alice");

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Embed", view.ViewName);
        var isEmbed = controller.ViewBag.IsEmbed;
        Assert.True(isEmbed == null || (bool)isEmbed);
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
