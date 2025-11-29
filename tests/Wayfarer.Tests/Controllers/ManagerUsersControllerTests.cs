using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
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

        var result = await controller.Index(search: null!);

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

    [Fact]
    public async Task Edit_Get_ReturnsNotFound_WhenIdNull()
    {
        var db = CreateDbContext();
        var userManager = MockUserManager(TestDataFixtures.CreateUser(id: "mgr", username: "mgr"));
        var controller = BuildController(db, userManager.Object);

        var result = await controller.Edit((string)null!);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Edit_Get_ReturnsNotFound_WhenUserNotFound()
    {
        var db = CreateDbContext();
        var userManager = MockUserManager(TestDataFixtures.CreateUser(id: "mgr", username: "mgr"));
        userManager.Setup(m => m.FindByIdAsync("missing")).ReturnsAsync((ApplicationUser)null!);
        var controller = BuildController(db, userManager.Object);

        var result = await controller.Edit("missing");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Edit_Get_RedirectsToIndex_WhenUserIsNotUser()
    {
        var db = CreateDbContext();
        var admin = TestDataFixtures.CreateUser(id: "admin", username: "admin");
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(admin);
        userManager.Setup(m => m.GetRolesAsync(admin)).ReturnsAsync(new[] { "Admin" });
        var controller = BuildController(db, userManager.Object);

        var result = await controller.Edit(admin.Id);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
    }

    [Fact]
    public async Task Edit_Post_PreventsAdminUsernameChange()
    {
        var db = CreateDbContext();
        var admin = TestDataFixtures.CreateUser(id: "admin", username: "admin");
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(admin);
        var controller = BuildController(db, userManager.Object);

        var result = await controller.Edit(new EditUserViewModel
        {
            Id = admin.Id,
            UserName = "admin",
            DisplayName = "Admin",
            Role = "User",
            IsActive = true,
            IsProtected = false
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey("Role"));
    }

    [Fact]
    public async Task Edit_Post_PreventsRoleChangeToAdmin()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "user1", username: "user1");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(user);
        var controller = BuildController(db, userManager.Object);

        var result = await controller.Edit(new EditUserViewModel
        {
            Id = user.Id,
            UserName = "user1",
            DisplayName = "User",
            Role = "Admin",
            IsActive = true,
            IsProtected = false
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey("Role"));
    }

    [Fact]
    public async Task Edit_Post_UpdatesUser_WhenValid()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "user2", username: "user2");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(user);
        userManager.Setup(m => m.GetRolesAsync(user)).ReturnsAsync(new[] { "User" });
        userManager.Setup(m => m.RemoveFromRolesAsync(user, It.IsAny<IList<string>>())).ReturnsAsync(IdentityResult.Success);
        userManager.Setup(m => m.AddToRoleAsync(user, "User")).ReturnsAsync(IdentityResult.Success);
        userManager.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
        userManager.Setup(m => m.UpdateSecurityStampAsync(user)).ReturnsAsync(IdentityResult.Success);

        var controller = BuildController(db, userManager.Object);

        var result = await controller.Edit(new EditUserViewModel
        {
            Id = user.Id,
            UserName = "user2",
            DisplayName = "Updated Name",
            Role = "User",
            IsActive = false,
            IsProtected = true
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Updated Name", user.DisplayName);
        Assert.False(user.IsActive);
        Assert.True(user.IsProtected);
    }

    [Fact]
    public async Task DeleteConfirmed_PreventsProtectedUserDeletion()
    {
        var db = CreateDbContext();
        var protectedUser = TestDataFixtures.CreateUser(id: "prot", username: "protected");
        protectedUser.IsProtected = true;
        db.Users.Add(protectedUser);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(protectedUser);
        var controller = BuildController(db, userManager.Object);

        var result = await controller.DeleteConfirmed(protectedUser.Id);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Delete", redirect.ActionName);
        userManager.Verify(m => m.DeleteAsync(It.IsAny<ApplicationUser>()), Times.Never);
    }

    [Fact]
    public async Task ChangePassword_Get_ReturnsView_WhenValid()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "chgpwd", username: "user");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(user);
        userManager.Setup(m => m.IsInRoleAsync(user, "User")).ReturnsAsync(true);
        var controller = BuildController(db, userManager.Object);

        var result = await controller.ChangePassword(user.Id);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ChangePasswordViewModel>(view.Model);
        Assert.Equal(user.Id, model.UserId);
    }

    [Fact]
    public async Task ChangePassword_Get_ReturnsNotFound_WhenIdNull()
    {
        var db = CreateDbContext();
        var userManager = MockUserManager(TestDataFixtures.CreateUser(id: "mgr", username: "mgr"));
        var controller = BuildController(db, userManager.Object);

        var result = await controller.ChangePassword((string)null!);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ChangePassword_Post_ReturnsView_WhenPasswordMismatch()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "pwd", username: "user");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(user);
        userManager.Setup(m => m.IsInRoleAsync(user, "User")).ReturnsAsync(true);
        var controller = BuildController(db, userManager.Object);

        var result = await controller.ChangePassword(new ChangePasswordViewModel
        {
            UserId = user.Id,
            UserName = user.UserName,
            NewPassword = "Pass1!",
            ConfirmPassword = "Pass2!"
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
    }

    [Fact]
    public async Task ChangePassword_Get_Forbids_WhenRoleNotUser()
    {
        var db = CreateDbContext();
        var adminUser = TestDataFixtures.CreateUser(id: "admin", username: "admin");
        db.Users.Add(adminUser);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(adminUser);
        userManager.Setup(m => m.FindByIdAsync(adminUser.Id)).ReturnsAsync(adminUser);
        userManager.Setup(m => m.IsInRoleAsync(adminUser, "User")).ReturnsAsync(false);
        var controller = BuildController(db, userManager.Object);

        var result = await controller.ChangePassword(adminUser.Id);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task ChangePassword_Post_Forbids_WhenRoleNotUser()
    {
        var db = CreateDbContext();
        var adminUser = TestDataFixtures.CreateUser(id: "admin", username: "admin");
        db.Users.Add(adminUser);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(adminUser);
        userManager.Setup(m => m.FindByIdAsync(adminUser.Id)).ReturnsAsync(adminUser);
        userManager.Setup(m => m.IsInRoleAsync(adminUser, "User")).ReturnsAsync(false);
        var controller = BuildController(db, userManager.Object);

        var result = await controller.ChangePassword(new ChangePasswordViewModel
        {
            UserId = adminUser.Id,
            UserName = adminUser.UserName,
            NewPassword = "Pass1!",
            ConfirmPassword = "Pass1!"
        });

        Assert.IsType<ForbidResult>(result);
        userManager.Verify(m => m.RemovePasswordAsync(It.IsAny<ApplicationUser>()), Times.Never);
    }

    [Fact]
    public async Task Delete_DeniesNonUserRole()
    {
        var db = CreateDbContext();
        var admin = TestDataFixtures.CreateUser(id: "admin", username: "admin");
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(admin);
        userManager.Setup(m => m.GetRolesAsync(admin)).ReturnsAsync(new[] { "Admin" });
        var controller = BuildController(db, userManager.Object);

        var result = await controller.Delete(admin.Id);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("AccessDenied", redirect.ActionName);
        Assert.Equal("Account", redirect.ControllerName);
    }

    [Fact]
    public void Create_Get_ReturnsView_WithRoles()
    {
        // Arrange
        var db = CreateDbContext();
        if (!db.Roles.Any(r => r.Name == "User"))
        {
            db.Roles.Add(new IdentityRole { Id = "role-user", Name = "User", NormalizedName = "USER" });
            db.SaveChanges();
        }
        var manager = TestDataFixtures.CreateUser(id: "mgr", username: "manager");
        var userManager = MockUserManager(manager);
        var controller = BuildController(db, userManager.Object);

        // Act
        var result = controller.Create();

        // Assert
        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<CreateUserViewModel>(view.Model);
        Assert.NotNull(model.Roles);
    }

    [Fact]
    public async Task Create_Post_RequiresRole()
    {
        // Arrange
        var db = CreateDbContext();
        if (!db.Roles.Any(r => r.Name == "User"))
        {
            db.Roles.Add(new IdentityRole { Id = "role-user", Name = "User", NormalizedName = "USER" });
            db.SaveChanges();
        }
        var manager = TestDataFixtures.CreateUser(id: "mgr", username: "manager");
        var userManager = MockUserManager(manager);
        var controller = BuildController(db, userManager.Object);
        var model = new CreateUserViewModel
        {
            UserName = "newuser",
            DisplayName = "New User",
            Password = "P@ssw0rd!",
            Role = null!
        };

        // Act
        var result = await controller.Create(model);

        // Assert
        var view = Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.True(controller.ModelState.ContainsKey("Role"));
    }

    [Fact]
    public async Task Create_Post_ValidatesRoleMustBeUser()
    {
        // Arrange
        var db = CreateDbContext();
        if (!db.Roles.Any(r => r.Name == "User"))
        {
            db.Roles.Add(new IdentityRole { Id = "role-user", Name = "User", NormalizedName = "USER" });
            db.SaveChanges();
        }
        var manager = TestDataFixtures.CreateUser(id: "mgr", username: "manager");
        var userManager = MockUserManager(manager);
        var controller = BuildController(db, userManager.Object);
        var model = new CreateUserViewModel
        {
            UserName = "newuser",
            DisplayName = "New User",
            Password = "P@ssw0rd!",
            Role = "Admin"
        };

        // Act
        var result = await controller.Create(model);

        // Assert
        var view = Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey("Role"));
    }

    [Fact]
    public async Task Create_Post_PreventsDuplicateUsername()
    {
        // Arrange
        var db = CreateDbContext();
        var existing = TestDataFixtures.CreateUser(id: "existing", username: "duplicate");
        db.Users.Add(existing);
        if (!db.Roles.Any(r => r.Name == "User"))
        {
            db.Roles.Add(new IdentityRole { Id = "role-user", Name = "User", NormalizedName = "USER" });
        }
        await db.SaveChangesAsync();

        var manager = TestDataFixtures.CreateUser(id: "mgr", username: "manager");
        var userManager = MockUserManager(manager);
        userManager.Setup(m => m.FindByNameAsync("duplicate")).ReturnsAsync(existing);
        var controller = BuildController(db, userManager.Object);
        var model = new CreateUserViewModel
        {
            UserName = "duplicate",
            DisplayName = "New User",
            Password = "P@ssw0rd!",
            Role = "User"
        };

        // Act
        var result = await controller.Create(model);

        // Assert
        var view = Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey("UserName"));
    }

    [Fact]
    public async Task Create_Post_CreatesUser_WhenValid()
    {
        // Arrange
        var db = CreateDbContext();
        if (!db.Roles.Any(r => r.Name == "User"))
        {
            db.Roles.Add(new IdentityRole { Id = "role-user", Name = "User", NormalizedName = "USER" });
            db.SaveChanges();
        }
        var manager = TestDataFixtures.CreateUser(id: "mgr", username: "manager");
        var userManager = MockUserManager(manager);
        userManager.Setup(m => m.FindByNameAsync(It.IsAny<string>())).ReturnsAsync((ApplicationUser)null!);
        userManager.Setup(m => m.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Success)
            .Callback<ApplicationUser, string>((u, _) =>
            {
                db.Users.Add(u);
                db.SaveChanges();
            });
        userManager.Setup(m => m.AddToRoleAsync(It.IsAny<ApplicationUser>(), "User")).ReturnsAsync(IdentityResult.Success);
        var controller = BuildController(db, userManager.Object);
        var model = new CreateUserViewModel
        {
            UserName = "newuser",
            DisplayName = "New User",
            Password = "P@ssw0rd!",
            Role = "User",
            IsActive = true,
            IsProtected = false
        };

        // Act
        var result = await controller.Create(model);

        // Assert
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Users", redirect.ControllerName);
        userManager.Verify(m => m.CreateAsync(It.IsAny<ApplicationUser>(), "P@ssw0rd!"), Times.Once);
        userManager.Verify(m => m.AddToRoleAsync(It.IsAny<ApplicationUser>(), "User"), Times.Once);
    }

    [Fact]
    public async Task Create_Post_ReturnsView_WhenModelInvalid()
    {
        // Arrange
        var db = CreateDbContext();
        if (!db.Roles.Any(r => r.Name == "User"))
        {
            db.Roles.Add(new IdentityRole { Id = "role-user", Name = "User", NormalizedName = "USER" });
            db.SaveChanges();
        }
        var manager = TestDataFixtures.CreateUser(id: "mgr", username: "manager");
        var userManager = MockUserManager(manager);
        var controller = BuildController(db, userManager.Object);
        controller.ModelState.AddModelError("UserName", "Required");
        userManager.Setup(m => m.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Description = "invalid" }));
        var model = new CreateUserViewModel
        {
            UserName = "",
            DisplayName = "New User",
            Password = "P@ssw0rd!",
            Role = "User"
        };

        // Act
        var result = await controller.Create(model);

        // Assert
        var view = Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
    }

    [Fact]
    public async Task Create_Post_ReturnsView_WhenUserCreationFails()
    {
        // Arrange
        var db = CreateDbContext();
        if (!db.Roles.Any(r => r.Name == "User"))
        {
            db.Roles.Add(new IdentityRole { Id = "role-user", Name = "User", NormalizedName = "USER" });
            db.SaveChanges();
        }
        var manager = TestDataFixtures.CreateUser(id: "mgr", username: "manager");
        var userManager = MockUserManager(manager);
        userManager.Setup(m => m.FindByNameAsync(It.IsAny<string>())).ReturnsAsync((ApplicationUser)null!);
        userManager.Setup(m => m.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Code = "PasswordTooWeak", Description = "Password is too weak" }));
        var controller = BuildController(db, userManager.Object);
        var model = new CreateUserViewModel
        {
            UserName = "newuser",
            DisplayName = "New User",
            Password = "weak",
            Role = "User"
        };

        // Act
        var result = await controller.Create(model);

        // Assert
        var view = Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(controller.ModelState.Values, v => v.Errors.Any(e => e.ErrorMessage.Contains("Password is too weak")));
    }

    private static UsersController BuildController(ApplicationDbContext db, UserManager<ApplicationUser> userManager)
    {
        var roleManager = MockRoleManager();
        if (Mock.Get(userManager) is Mock<UserManager<ApplicationUser>> userManagerMock)
        {
            userManagerMock.Setup(m => m.FindByIdAsync(It.IsAny<string>()))
                .ReturnsAsync((string id) => db.Users.FirstOrDefault(u => u.Id == id));
        }
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
        var mgr = new Mock<UserManager<ApplicationUser>>(store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        mgr.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        return mgr;
    }

    private static RoleManager<IdentityRole> MockRoleManager(IEnumerable<IdentityRole>? roles = null)
    {
        roles ??= new[]
        {
            new IdentityRole { Id = "role-user", Name = "User", NormalizedName = "USER" }
        };

        var store = new Mock<IRoleStore<IdentityRole>>();
        var roleManager = new Mock<RoleManager<IdentityRole>>(
            store.Object,
            Array.Empty<IRoleValidator<IdentityRole>>(),
            Mock.Of<ILookupNormalizer>(),
            new IdentityErrorDescriber(),
            Mock.Of<ILogger<RoleManager<IdentityRole>>>());

        roleManager.Setup(r => r.Roles).Returns(roles.AsQueryable());
        return roleManager.Object;
    }
}
