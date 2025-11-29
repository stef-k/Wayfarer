using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Admin.Controllers;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
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

    [Fact]
    public async Task Index_ReturnsView_WhenUserExists()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        db.SaveChanges();
        var controller = BuildController(db);

        var result = await controller.Index("u1");

        Assert.IsType<ViewResult>(result);
    }

    private ApiTokenController BuildController(ApplicationDbContext db)
    {
        var userStore = new Mock<IUserStore<ApplicationUser>>();
        var userManager = new Mock<UserManager<ApplicationUser>>(userStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        userManager.Setup(m => m.FindByIdAsync(It.IsAny<string>())).ReturnsAsync((string id) => db.Users.SingleOrDefault(u => u.Id == id));

        var svc = new ApiTokenService(db, userManager.Object);
        var controller = new ApiTokenController(NullLogger<ApiTokenController>.Instance, db, svc);
        var httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());
        return controller;
    }
}
