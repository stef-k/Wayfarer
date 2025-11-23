using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Manager.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
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
        var controller = BuildController(CreateDbContext());

        var result = await controller.Index();

        Assert.IsType<ViewResult>(result);
    }

    private UsersController BuildController(ApplicationDbContext db)
    {
        var apiTokenService = new ApiTokenService(db, new NullLogger<ApiTokenService>());
        var controller = new UsersController(
            db,
            Mock.Of<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>(),
            Mock.Of<Microsoft.AspNetCore.Identity.SignInManager<ApplicationUser>>(MockBehavior.Loose),
            NullLogger<UsersController>.Instance,
            apiTokenService);
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser("mgr", "Manager") };
        return controller;
    }
}
