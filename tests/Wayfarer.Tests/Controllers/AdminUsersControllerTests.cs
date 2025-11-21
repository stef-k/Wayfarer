using System.Security.Claims;
using System.Threading.Tasks;
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
/// Admin UsersController tests (password change guards).
/// </summary>
public class AdminUsersControllerTests : TestBase
{
    [Fact]
    public async Task ChangePassword_Post_PreventsSelfChange()
    {
        // Arrange
        var db = CreateDbContext();
        var admin = TestDataFixtures.CreateUser(id: "admin", username: "admin");
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(admin);
        userManager.Setup(m => m.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(admin);
        userManager.Setup(m => m.FindByIdAsync(admin.Id)).ReturnsAsync(admin);

        var controller = BuildController(db, userManager.Object);
        var model = new ChangePasswordViewModel
        {
            UserId = admin.Id,
            UserName = admin.UserName,
            NewPassword = "P@ssw0rd!",
            ConfirmPassword = "P@ssw0rd!"
        };

        // Act
        var result = await controller.ChangePassword(model);

        // Assert
        var view = Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(controller.ModelState, kvp => kvp.Value!.Errors.Any());
    }

    [Fact]
    public async Task ChangePassword_Post_AllowsChangingOtherUser()
    {
        // Arrange
        var db = CreateDbContext();
        var admin = TestDataFixtures.CreateUser(id: "admin", username: "admin");
        var target = TestDataFixtures.CreateUser(id: "target", username: "bob");
        db.Users.AddRange(admin, target);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(admin);
        userManager.Setup(m => m.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(admin);
        userManager.Setup(m => m.FindByIdAsync(target.Id)).ReturnsAsync(target);
        userManager.Setup(m => m.RemovePasswordAsync(target)).ReturnsAsync(IdentityResult.Success);
        userManager.Setup(m => m.AddPasswordAsync(target, It.IsAny<string>())).ReturnsAsync(IdentityResult.Success);
        userManager.Setup(m => m.UpdateSecurityStampAsync(target)).ReturnsAsync(IdentityResult.Success);

        var controller = BuildController(db, userManager.Object);
        var model = new ChangePasswordViewModel
        {
            UserId = target.Id,
            UserName = target.UserName,
            NewPassword = "P@ssw0rd!",
            ConfirmPassword = "P@ssw0rd!"
        };

        // Act
        var result = await controller.ChangePassword(model);

        // Assert
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Users", redirect.ControllerName);
    }

    private static UsersController BuildController(ApplicationDbContext db, UserManager<ApplicationUser> userManager)
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

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "admin"),
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, ApplicationRoles.Admin)
            }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());
        return controller;
    }

    private static Mock<UserManager<ApplicationUser>> MockUserManager(ApplicationUser user)
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new Mock<UserManager<ApplicationUser>>(store.Object, null, null, null, null, null, null, null, null);
    }

    private static RoleManager<IdentityRole> MockRoleManager()
    {
        var store = new Mock<IRoleStore<IdentityRole>>();
        return new RoleManager<IdentityRole>(store.Object, Array.Empty<IRoleValidator<IdentityRole>>(), null, null, null);
    }

    private static SignInManager<ApplicationUser> MockSignInManager(UserManager<ApplicationUser> userManager)
    {
        var contextAccessor = new Mock<IHttpContextAccessor>();
        var claimsFactory = new Mock<IUserClaimsPrincipalFactory<ApplicationUser>>();
        return new SignInManager<ApplicationUser>(userManager, contextAccessor.Object, claimsFactory.Object, null, null, null, null);
    }
}
