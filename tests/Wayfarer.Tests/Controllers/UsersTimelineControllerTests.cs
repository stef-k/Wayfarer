using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Models;
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
        Assert.Equal("Timeline", view.ViewName);
        Assert.True((bool)controller.ViewBag.IsEmbed);
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
}
