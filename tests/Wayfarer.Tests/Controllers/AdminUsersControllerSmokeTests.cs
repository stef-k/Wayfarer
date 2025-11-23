using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Admin.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
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
        var controller = BuildController(CreateDbContext(), MockUserManager(TestDataFixtures.CreateUser(id: "admin")).Object);

        var result = await controller.ChangePassword("missing");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Create_ReturnsView_OnModelError()
    {
        var db = CreateDbContext();
        var controller = BuildController(db, MockUserManager(TestDataFixtures.CreateUser(id: "admin")).Object);
        controller.ModelState.AddModelError("UserName", "required");

        var result = await controller.Create(new CreateUserViewModel());

        Assert.IsType<ViewResult>(result);
    }

    private UsersController BuildController(ApplicationDbContext db, UserManager<ApplicationUser> userManager)
    {
        var roleManager = MockRoleManager();
        var signInManager = MockSignInManager(userManager);
        var apiTokenService = new ApiTokenService(db, userManager);

        var controller = new UsersController(
            db,
            userManager,
            roleManager,
            signInManager,
            NullLogger<UsersController>.Instance,
            apiTokenService);
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser("admin", "Admin") };
        controller.TempData = new TempDataDictionary(controller.HttpContext, Mock.Of<ITempDataProvider>());
        return controller;
    }

    private static SignInManager<ApplicationUser> MockSignInManager(UserManager<ApplicationUser> userManager)
    {
        var contextAccessor = new Mock<IHttpContextAccessor>();
        var claimsFactory = new Mock<IUserClaimsPrincipalFactory<ApplicationUser>>();
        return new Mock<SignInManager<ApplicationUser>>(
            userManager,
            contextAccessor.Object,
            claimsFactory.Object,
            null, null, null, null).Object;
    }

    private static Mock<UserManager<ApplicationUser>> MockUserManager(ApplicationUser user)
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        var mgr = new Mock<UserManager<ApplicationUser>>(store.Object, null, null, null, null, null, null, null, null);
        mgr.Setup(m => m.FindByIdAsync(It.IsAny<string>())).ReturnsAsync((string id) => id == user.Id ? user : null);
        return mgr;
    }

    private static RoleManager<IdentityRole> MockRoleManager()
    {
        var store = new Mock<IQueryableRoleStore<IdentityRole>>();
        store.As<IQueryableRoleStore<IdentityRole>>().Setup(s => s.Roles).Returns(Array.Empty<IdentityRole>().AsQueryable());
        return new RoleManager<IdentityRole>(store.Object, Array.Empty<IRoleValidator<IdentityRole>>(), null, null, null);
    }
}
