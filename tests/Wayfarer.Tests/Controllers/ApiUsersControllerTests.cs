using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using AppLocation = Wayfarer.Models.Location;
using Wayfarer.Areas.Api.Controllers;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// API Users endpoints: basics, delete locations, stats.
/// </summary>
public class ApiUsersControllerTests : TestBase
{
    [Fact]
    public async Task GetBasic_ReturnsNotFound_WhenUserMissing()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);

        var result = await controller.GetBasic("missing", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetBasic_ReturnsUser_WhenFound()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1", username: "alice", displayName: "Alice"));
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.GetBasic("u1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        Assert.Equal("u1", payload.GetType().GetProperty("id")?.GetValue(payload));
    }

    [Fact]
    public async Task DeleteAllUserLocations_Forbids_WhenCallerDifferent()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "target");
        db.Users.Add(user);
        db.Locations.Add(new AppLocation { UserId = user.Id, Coordinates = TestDataFixtures.CreatePoint(), Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow, TimeZoneId = "UTC" });
        await db.SaveChangesAsync();
        var controller = BuildController(db);
        controller.ControllerContext.HttpContext = CreateHttpContextWithUser("other");

        var result = await controller.DeleteAllUserLocations(user.Id);

        Assert.IsType<ForbidResult>(result);
        Assert.NotEmpty(db.Locations);
    }

    [Fact]
    public async Task DeleteAllUserLocations_RemovesRows_WhenCallerMatches()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "target");
        db.Users.Add(user);
        db.Locations.Add(new AppLocation { UserId = user.Id, Coordinates = TestDataFixtures.CreatePoint(), Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow, TimeZoneId = "UTC" });
        await db.SaveChangesAsync();
        var controller = BuildController(db);
        controller.ControllerContext.HttpContext = CreateHttpContextWithUser(user.Id);

        var result = await controller.DeleteAllUserLocations(user.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(db.Locations);
    }

    [Fact]
    public async Task GetPublicStats_ReturnsNotFound_WhenNoUser()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.GetPublicStats();

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetPublicStats_ReturnsStats_WhenUserPresent()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var statsService = new Mock<ILocationStatsService>();
        statsService.Setup(s => s.GetStatsForUserAsync(user.Id)).ReturnsAsync(new UserLocationStatsDto { TotalLocations = 3 });
        var controller = BuildController(db, statsService.Object);
        controller.ControllerContext.HttpContext = CreateHttpContextWithUser(user.Id);

        var result = await controller.GetPublicStats();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<UserLocationStatsDto>(ok.Value);
        Assert.Equal(3, dto.TotalLocations);
    }

    private UsersController BuildController(ApplicationDbContext db, ILocationStatsService? statsService = null)
    {
        return new UsersController(db, NullLogger<UsersController>.Instance, statsService ?? new LocationStatsService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }
}
