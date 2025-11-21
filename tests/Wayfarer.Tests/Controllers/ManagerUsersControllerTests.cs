using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
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
/// Manager UsersController guardrails (password and edit constraints).
/// </summary>
public class ManagerUsersControllerTests : TestBase
{
    [Fact]
    public async Task ChangePassword_ForNonUserRole_ReturnsForbid()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "target-1", username: "alice");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(user);
        userManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        userManager.Setup(m => m.IsInRoleAsync(user, "User")).ReturnsAsync(false);
        var controller = BuildController(db, userManager.Object);

        var result = await controller.ChangePassword(new ChangePasswordViewModel
        {
            UserId = user.Id,
            UserName = user.UserName,
            NewPassword = "New1!",
            ConfirmPassword = "New1!"
        });

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task ChangePassword_ForUserRole_UpdatesPassword()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "target-2", username: "bob");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(user);
        userManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        userManager.Setup(m => m.IsInRoleAsync(user, "User")).ReturnsAsync(true);
        userManager.Setup(m => m.RemovePasswordAsync(user)).ReturnsAsync(IdentityResult.Success);
        userManager.Setup(m => m.AddPasswordAsync(user, It.IsAny<string>())).ReturnsAsync(IdentityResult.Success);
        userManager.Setup(m => m.UpdateSecurityStampAsync(user)).ReturnsAsync(IdentityResult.Success);
        var controller = BuildController(db, userManager.Object);

        var result = await controller.ChangePassword(new ChangePasswordViewModel
        {
            UserId = user.Id,
            UserName = user.UserName,
            NewPassword = "New1!",
            ConfirmPassword = "New1!"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Users", redirect.ControllerName);
    }

    [Fact]
    public async Task Edit_PreventsUsernameChange()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "edit-1", username: "charlie");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(user);
        userManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        userManager.Setup(m => m.GetRolesAsync(user)).ReturnsAsync(new[] { "User" });
        userManager.Setup(m => m.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(TestDataFixtures.CreateUser(id: "mgr", username: "manager"));
        var controller = BuildController(db, userManager.Object);

        var result = await controller.Edit(new EditUserViewModel
        {
            Id = user.Id,
            UserName = "changed",
            DisplayName = "disp",
            IsActive = true,
            IsProtected = false,
            Role = "User"
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey("UserName"));
    }

    private static UsersController BuildController(ApplicationDbContext db, UserManager<ApplicationUser> userManager)
    {
        var roleStore = new Mock<IRoleStore<IdentityRole>>();
        var roleManager = new RoleManager<IdentityRole>(roleStore.Object, null, null, null, null);
        var controller = new UsersController(
            userManager,
            roleManager,
            NullLogger<BaseController>.Instance,
            db,
            new ApiTokenService(db, userManager));

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "manager-actor"),
                new Claim(ClaimTypes.Name, "manager-actor"),
                new Claim(ClaimTypes.Role, ApplicationRoles.Manager)
            }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
        return controller;
    }

    private static Mock<UserManager<ApplicationUser>> MockUserManager(ApplicationUser user)
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        var mgr = new Mock<UserManager<ApplicationUser>>(store.Object, null, null, null, null, null, null, null, null);
        mgr.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        return mgr;
    }
}
