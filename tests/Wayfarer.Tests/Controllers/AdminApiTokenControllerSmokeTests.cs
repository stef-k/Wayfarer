using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Admin.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Minimal coverage for Admin ApiToken controller.
/// </summary>
public class AdminApiTokenControllerSmokeTests : TestBase
{
    [Fact]
    public async Task Index_Redirects_WhenUserMissing()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.Index("missing");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Home", redirect.ControllerName);
    }

    [Fact]
    public async Task Create_ReturnsRedirect_WhenNameMissing()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1"));
        db.SaveChanges();
        var controller = BuildController(db);

        var result = await controller.Create("u1", "");

        Assert.IsType<RedirectToActionResult>(result);
    }

    [Fact]
    public async Task Create_CreatesToken_WhenValid()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        db.SaveChanges();
        var controller = BuildController(db);

        var result = await controller.Create("u1", "new-token");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ApiTokenController.Index), redirect.ActionName);
        Assert.Single(db.ApiTokens);
    }

    private ApiTokenController BuildController(ApplicationDbContext db)
    {
        var svc = new ApiTokenService(db, new NullLogger<ApiTokenService>());
        var controller = new ApiTokenController(NullLogger<ApiTokenController>.Instance, db, svc);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }
}
