using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Manager.Controllers;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Smoke for manager users controller.
/// </summary>
public class ManagerUsersControllerSmokeTests : TestBase
{
    [Fact]
    public async Task Index_ReturnsView()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);

        var result = await controller.Index(search: null, page: 1);

        Assert.IsType<ViewResult>(result);
    }

    private UsersController BuildController(ApplicationDbContext db)
    {
        var userManager = MockUserManager(TestDataFixtures.CreateUser(id: "mgr", username: "mgr"));
        var apiTokenService = new ApiTokenService(db, userManager.Object);
        var controller = new UsersController(
            userManager.Object,
            MockRoleManager().Object,
            NullLogger<UsersController>.Instance,
            db,
            apiTokenService);
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser("mgr", "Manager") };
        return controller;
    }

    private static Mock<UserManager<ApplicationUser>> MockUserManager(ApplicationUser user)
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        var mgr = new Mock<UserManager<ApplicationUser>>(store.Object, null, null, null, null, null, null, null, null);
        mgr.Setup(m => m.FindByIdAsync(It.IsAny<string>())).ReturnsAsync(user);
        return mgr;
    }

    private static Mock<RoleManager<IdentityRole>> MockRoleManager()
    {
        var store = new Mock<IRoleStore<IdentityRole>>();
        return new Mock<RoleManager<IdentityRole>>(store.Object, null, null, null, null);
    }
}
