using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Manager.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Smoke for manager users controller.
/// </summary>
public class ManagerUsersControllerSmokeTests : TestBase
{
    /// <summary>An Identity policy rejection must not remove the existing password or change its security stamp.</summary>
    [Fact]
    public async Task ChangePassword_IdentityRejectionDoesNotRemoveCredential()
    {
        var user = TestDataFixtures.CreateUser(id: "target", username: "alice");
        var manager = MockUserManager(user);
        var rejected = IdentityResult.Failed(new IdentityErrorDescriber().PasswordTooShort(15));
        manager.Setup(m => m.IsInRoleAsync(user, "User")).ReturnsAsync(true);
        manager.Setup(m => m.RemovePasswordAsync(user)).ReturnsAsync(IdentityResult.Success);
        manager.Setup(m => m.AddPasswordAsync(user, "Admin2!")).ReturnsAsync(rejected);
        manager.Setup(m => m.GeneratePasswordResetTokenAsync(user)).ReturnsAsync("fixture-reset-token");
        manager.Setup(m => m.ResetPasswordAsync(user, "fixture-reset-token", "Admin2!")).ReturnsAsync(rejected);
        var controller = BuildController(CreateDbContext(), manager.Object);
        var model = new ChangePasswordViewModel
        {
            UserId = user.Id, NewPassword = "Admin2!", ConfirmPassword = "Admin2!"
        };

        var view = Assert.IsType<ViewResult>(await controller.ChangePassword(model));

        Assert.Same(model, view.Model);
        Assert.False(controller.ModelState.IsValid);
        manager.Verify(m => m.RemovePasswordAsync(It.IsAny<ApplicationUser>()), Times.Never);
        manager.Verify(m => m.AddPasswordAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
        manager.Verify(m => m.ResetPasswordAsync(user, "fixture-reset-token", "Admin2!"), Times.Once);
        manager.Verify(m => m.UpdateSecurityStampAsync(It.IsAny<ApplicationUser>()), Times.Never);
    }

    [Fact]
    public async Task Index_ReturnsView()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);

        var result = await controller.Index(search: null!, page: 1);

        Assert.IsType<ViewResult>(result);
    }

    /// <summary>Builds the production controller with a supplied Identity boundary or the default smoke fixture.</summary>
    private UsersController BuildController(ApplicationDbContext db, UserManager<ApplicationUser>? suppliedManager = null)
    {
        var userManager = suppliedManager ?? MockUserManager(TestDataFixtures.CreateUser(id: "mgr", username: "mgr")).Object;
        var apiTokenService = new ApiTokenService(db, userManager);
        var controller = new UsersController(
            userManager,
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
        var mgr = new Mock<UserManager<ApplicationUser>>(store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        mgr.Setup(m => m.FindByIdAsync(It.IsAny<string>())).ReturnsAsync(user);
        return mgr;
    }

    private static Mock<RoleManager<IdentityRole>> MockRoleManager()
    {
        var store = new Mock<IRoleStore<IdentityRole>>();
        return new Mock<RoleManager<IdentityRole>>(store.Object, null!, null!, null!, null!);
    }
}
