using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
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
/// Manager user creation edge cases.
/// </summary>
public class ManagerUsersCreateControllerTests : TestBase
{
    [Fact]
    public async Task Create_DuplicateUsername_ReturnsViewWithError()
    {
        var db = CreateDbContext();
        var existing = TestDataFixtures.CreateUser(id: "dup", username: "alice");
        db.Users.Add(existing);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(existing);
        userManager.Setup(m => m.FindByNameAsync("alice")).ReturnsAsync(existing);
        var roleManager = MockRoleManager();
        var controller = BuildController(db, userManager.Object, roleManager.Object);

        var model = new CreateUserViewModel
        {
            UserName = "alice",
            DisplayName = "Alice",
            Role = "User",
            Roles = new SelectList(new[] { "User" })
        };

        var result = await controller.Create(model);

        var view = Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey("UserName"));
    }

    [Fact]
    public async Task Create_RejectsRoleOutsideUser()
    {
        var db = CreateDbContext();
        var userManager = MockUserManager();
        var roleManager = MockRoleManager();
        var controller = BuildController(db, userManager.Object, roleManager.Object);

        var model = new CreateUserViewModel
        {
            UserName = "bob",
            DisplayName = "Bob",
            Password = "P@ss1!",
            Role = "Admin",
            Roles = new SelectList(new[] { "User" })
        };

        var result = await controller.Create(model);

        var view = Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey("Role"));
    }

    private static UsersController BuildController(ApplicationDbContext db, UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager)
    {
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
                new Claim(ClaimTypes.NameIdentifier, "manager-create"),
                new Claim(ClaimTypes.Name, "manager-create"),
                new Claim(ClaimTypes.Role, ApplicationRoles.Manager)
            }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
        return controller;
    }

    private static Mock<RoleManager<IdentityRole>> MockRoleManager()
    {
        var roleStore = new Mock<IRoleStore<IdentityRole>>();
        var roles = new[] { new IdentityRole("User"), new IdentityRole("Admin") }.AsQueryable();
        var roleManager = new Mock<RoleManager<IdentityRole>>(roleStore.Object, null, null, null, null);
        roleManager.Setup(r => r.Roles).Returns(roles);
        return roleManager;
    }

    private static Mock<UserManager<ApplicationUser>> MockUserManager(ApplicationUser? user = null)
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        var mgr = new Mock<UserManager<ApplicationUser>>(store.Object, null, null, null, null, null, null, null, null);
        if (user != null)
        {
            mgr.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        }
        return mgr;
    }
}
