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

    [Fact]
    public async Task Delete_Get_ReturnsView_WhenFound()
    {
        var db = CreateDbContext();
        var admin = TestDataFixtures.CreateUser(id: "admin", username: "admin");
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        var userManager = MockUserManager(admin);
        userManager.Setup(m => m.FindByIdAsync(admin.Id)).ReturnsAsync(admin);
        var controller = BuildController(db, userManager.Object);

        var result = await controller.Delete(admin.Id);

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<DeleteUserViewModel>(view.Model);
    }

    [Fact]
    public async Task DeleteConfirmed_RemovesUser_WhenNotProtected()
    {
        var db = CreateDbContext();
        var admin = TestDataFixtures.CreateUser(id: "admin", username: "admin");
        var target = TestDataFixtures.CreateUser(id: "delete-me", username: "bob");
        db.Users.AddRange(admin, target);
        await db.SaveChangesAsync();
        var userManager = MockUserManager(admin);
        userManager.Setup(m => m.FindByIdAsync(target.Id)).ReturnsAsync(target);
        userManager.Setup(m => m.DeleteAsync(target))
            .ReturnsAsync(IdentityResult.Success)
            .Callback(() => { db.Users.Remove(target); db.SaveChanges(); });
        userManager.Setup(m => m.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(admin);
        userManager.Setup(m => m.UpdateSecurityStampAsync(target)).ReturnsAsync(IdentityResult.Success);
        userManager.Setup(m => m.UpdateSecurityStampAsync(admin)).ReturnsAsync(IdentityResult.Success);

        var controller = BuildController(db, userManager.Object);

        var result = await controller.DeleteConfirmed(target.Id);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Empty(db.Users.Where(u => u.Id == target.Id));
    }

    [Fact]
    public async Task Edit_Get_ReturnsNotFound_ForMissingUser()
    {
        var db = CreateDbContext();
        var controller = BuildController(db, MockUserManager(TestDataFixtures.CreateUser(id: "admin", username: "admin")).Object);

        var result = await controller.Edit("missing");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Edit_Post_PreventsUsernameChange()
    {
        var db = CreateDbContext();
        var admin = TestDataFixtures.CreateUser(id: "admin", username: "admin");
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.AddRange(admin, user);
        await db.SaveChangesAsync();
        var userManager = MockUserManager(admin);
        userManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        userManager.Setup(m => m.GetRolesAsync(user)).ReturnsAsync(new List<string>());

        var controller = BuildController(db, userManager.Object);
        var model = new EditUserViewModel
        {
            Id = user.Id,
            UserName = "alana", // attempt change
            DisplayName = "Alana",
            IsActive = true,
            IsProtected = false,
            Role = "User"
        };

        var result = await controller.Edit(model);

        var view = Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(controller.ModelState, kvp => kvp.Key == "UserName" && kvp.Value!.Errors.Any());
    }

    [Fact]
    public async Task Index_ListsUsers_WhenNoSearch()
    {
        var db = CreateDbContext();
        var admin = TestDataFixtures.CreateUser(id: "admin", username: "admin");
        var match = TestDataFixtures.CreateUser(id: "u1", username: "bob");
        var miss = TestDataFixtures.CreateUser(id: "u2", username: "charlie");
        db.Users.AddRange(admin, match, miss);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(admin);
        userManager.SetupGet(m => m.Users).Returns(db.Users.AsQueryable());
        userManager.Setup(m => m.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(admin);
        userManager.Setup(m => m.GetRolesAsync(It.IsAny<ApplicationUser>())).ReturnsAsync(new List<string> { "User" });

        var controller = BuildController(db, userManager.Object);

        var result = await controller.Index(search: null);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<IEnumerable<UserViewModel>>(view.Model);
        Assert.Equal(3, model.Count());
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
        contextAccessor.Setup(a => a.HttpContext).Returns(new DefaultHttpContext());
        var claimsFactory = new Mock<IUserClaimsPrincipalFactory<ApplicationUser>>();
        var signInManager = new Mock<SignInManager<ApplicationUser>>(userManager, contextAccessor.Object, claimsFactory.Object, null, null, null, null);
        signInManager.Setup(s => s.SignInAsync(It.IsAny<ApplicationUser>(), It.IsAny<bool>(), null)).Returns(Task.CompletedTask);
        signInManager.Setup(s => s.UpdateExternalAuthenticationTokensAsync(It.IsAny<ExternalLoginInfo>()))
            .ReturnsAsync(IdentityResult.Success);
        return signInManager.Object;
    }
}
