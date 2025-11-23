using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Basic info endpoint coverage.
/// </summary>
public class ApiUsersBasicControllerTests : TestBase
{
    [Fact]
    public async Task GetBasic_ReturnsNotFound_WhenMissing()
    {
        var controller = BuildController(CreateDbContext(), role: "User");

        var result = await controller.GetBasic("missing", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetBasic_ReturnsUser_WhenExists()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice", displayName: "Alice");
        db.Users.Add(user);
        db.SaveChanges();
        var controller = BuildController(db, role: "Manager");

        var result = await controller.GetBasic("u1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("alice", ok.Value?.GetType().GetProperty("userName")?.GetValue(ok.Value));
    }

    private UsersController BuildController(ApplicationDbContext db, string role)
    {
        var controller = new UsersController(db, NullLogger<UsersController>.Instance, new LocationStatsService(db));
        var ctx = new DefaultHttpContext();
        ctx.User = BuildPrincipal("u-current", role);
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return controller;
    }
}
