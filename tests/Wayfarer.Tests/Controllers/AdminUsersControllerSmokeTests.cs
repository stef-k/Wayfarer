using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Admin.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Smoke tests for Admin Users controller.
/// </summary>
public class AdminUsersControllerSmokeTests : TestBase
{
    [Fact]
    public async Task ChangePassword_ReturnsNotFound_WhenMissing()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.ChangePassword("missing");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Create_ReturnsView_OnModelError()
    {
        var controller = BuildController(CreateDbContext());
        controller.ModelState.AddModelError("UserName", "required");

        var result = await controller.Create(new CreateUserViewModel());

        Assert.IsType<ViewResult>(result);
    }

    private UsersController BuildController(ApplicationDbContext db)
    {
        var userStore = new Mock<IUserStore<ApplicationUser>>();
        var userManager = new UserManager<ApplicationUser>(userStore.Object, null, null, null, null, null, null, null, null);
        var roleManager = Mock.Of<RoleManager<IdentityRole>>();
        var signInManager = Mock.Of<SignInManager<ApplicationUser>>(MockBehavior.Loose);
        var apiTokenService = new ApiTokenService(db, new NullLogger<ApiTokenService>());

        var controller = new UsersController(
            db,
            userManager,
            roleManager,
            signInManager,
            NullLogger<UsersController>.Instance,
            apiTokenService);
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser("admin") };
        return controller;
    }
}
