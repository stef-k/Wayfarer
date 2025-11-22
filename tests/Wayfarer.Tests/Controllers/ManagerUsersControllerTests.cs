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
    public async Task Index_ListsAll_WhenNoSearch()
    {
        var db = CreateDbContext();
        var active = TestDataFixtures.CreateUser(id: "u1", username: "anna");
        var inactive = TestDataFixtures.CreateUser(id: "u2", username: "brian");
        inactive.IsActive = false;
        db.Users.AddRange(active, inactive);
        AddUserRole(db, active.Id);
        AddUserRole(db, inactive.Id);
        await db.SaveChangesAsync();

        var manager = TestDataFixtures.CreateUser(id: "manager", username: "mgr");
        var userManager = MockUserManager(manager);
        userManager.SetupGet(m => m.Users).Returns(db.Users);
        userManager.Setup(m => m.GetRolesAsync(It.IsAny<ApplicationUser>())).ReturnsAsync(new[] { "User" });
        userManager.Setup(m => m.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(manager);
        var controller = BuildController(db, userManager.Object);

        var result = await controller.Index(search: null);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<IEnumerable<UserViewModel>>(view.Model);
        Assert.Equal(2, model.Count());
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_WhenMissing()
    {
        var db = CreateDbContext();
        var controller = BuildController(db, MockUserManager(TestDataFixtures.CreateUser(id: "mgr", username: "mgr")).Object);

        var result = await controller.Delete("missing");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Delete_ForProtectedUser_ReturnsForbid()
    {
        var db = CreateDbContext();
        var protectedUser = TestDataFixtures.CreateUser(id: "protected", username: "prot");
        protectedUser.IsProtected = true;
        db.Users.Add(protectedUser);
        AddUserRole(db, protectedUser.Id);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(protectedUser);
        userManager.Setup(m => m.GetRolesAsync(protectedUser)).ReturnsAsync(new List<string> { "User" });
        var controller = BuildController(db, userManager.Object);

        var result = await controller.Delete(protectedUser.Id);

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<DeleteUserViewModel>(view.Model);
    }

    [Fact]
    public async Task Delete_RemovesUser_ForNormalUser()
    {
        var db = CreateDbContext();
        var target = TestDataFixtures.CreateUser(id: "del", username: "delete");
        db.Users.Add(target);
        AddUserRole(db, target.Id);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(target);
        userManager.Setup(m => m.DeleteAsync(target)).ReturnsAsync(IdentityResult.Success)
            .Callback(() =>
            {
                db.Users.Remove(target);
                db.SaveChanges();
            });
        var controller = BuildController(db, userManager.Object);

        var result = await controller.DeleteConfirmed(target.Id);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Empty(db.Users.Where(u => u.Id == target.Id));
    }

    [Fact]
    public async Task Delete_ReturnsView_WhenFound()
    {
        var db = CreateDbContext();
        var target = TestDataFixtures.CreateUser(id: "view", username: "viewme");
        db.Users.Add(target);
        AddUserRole(db, target.Id);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(target);
        userManager.Setup(m => m.GetRolesAsync(target)).ReturnsAsync(new List<string> { "User" });
        var controller = BuildController(db, userManager.Object);

        var result = await controller.Delete(target.Id);

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<DeleteUserViewModel>(view.Model);
    }

    [Fact]
    public async Task Edit_Get_ReturnsView_WhenFound()
    {
        var db = CreateDbContext();
        var target = TestDataFixtures.CreateUser(id: "edit", username: "edith");
        db.Users.Add(target);
        AddUserRole(db, target.Id);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(target);
        userManager.Setup(m => m.GetRolesAsync(target)).ReturnsAsync(new[] { "User" });
        userManager.Setup(m => m.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(TestDataFixtures.CreateUser(id: "mgr", username: "manager"));
        var controller = BuildController(db, userManager.Object);

        var result = await controller.Edit(target.Id);

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsAssignableFrom<EditUserViewModel>(view.Model);
    }

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

    private static void AddUserRole(ApplicationDbContext db, string userId)
    {
        if (!db.Roles.Local.Any(r => r.Id == "role-user") && !db.Roles.Any(r => r.Id == "role-user"))
        {
            db.Roles.Add(new IdentityRole { Id = "role-user", Name = "User", NormalizedName = "USER" });
        }
        db.UserRoles.Add(new IdentityUserRole<string> { UserId = userId, RoleId = "role-user" });
    }

    private static Mock<UserManager<ApplicationUser>> MockUserManager(ApplicationUser user)
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        var mgr = new Mock<UserManager<ApplicationUser>>(store.Object, null, null, null, null, null, null, null, null);
        mgr.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        return mgr;
    }
}
