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
using Wayfarer.Util;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Admin API token management controller tests.
/// </summary>
public class AdminApiTokenControllerTests : TestBase
{
    [Fact]
    public async Task Create_AddsToken_ForUser()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "user-1", username: "alice");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(user);
        userManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        var controller = BuildController(db, userManager.Object);

        var result = await controller.Create(user.Id, "api-test");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        var token = Assert.Single(db.ApiTokens);
        Assert.Equal("api-test", token.Name);
        Assert.Equal(user.Id, token.UserId);
        Assert.False(controller.TempData.ContainsKey("NewToken"));
        Assert.False(controller.TempData.ContainsKey("NewTokenName"));
        Assert.DoesNotContain("Copy it now", controller.TempData["AlertMessage"]?.ToString() ?? string.Empty);
        var priorHash = token.TokenHash;
        Assert.IsType<RedirectToActionResult>(await controller.Regenerate(user.Id, "api-test"));
        Assert.NotEqual(priorHash, token.TokenHash);
        Assert.False(controller.TempData.ContainsKey("NewToken"));
        Assert.False(controller.TempData.ContainsKey("NewTokenName"));
        Assert.DoesNotContain("Copy it now", controller.TempData["AlertMessage"]?.ToString() ?? string.Empty);
    }

    /// <summary>Index discards unread legacy plaintext while keeping ordinary alerts.</summary>
    [Fact]
    public async Task Index_DiscardsLegacyRevealAndKeepsAlerts()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db, MockUserManager(user).Object);
        controller.TempData["NewToken"] = "legacy-secret";
        controller.TempData["NewTokenName"] = "legacy-name";
        controller.TempData["AlertMessage"] = "Ordinary alert";
        Assert.IsType<ViewResult>(await controller.Index(user.Id));
        Assert.False(controller.TempData.ContainsKey("NewToken"));
        Assert.False(controller.TempData.ContainsKey("NewTokenName"));
        Assert.Equal("Ordinary alert", controller.TempData["AlertMessage"]);
    }

    [Fact]
    public async Task Create_WhenNameExists_ShowsWarningOnly()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "user-dup", username: "bob");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { Name = "existing", Token = "wf_token", UserId = user.Id, User = user });
        await db.SaveChangesAsync();

        var userManager = MockUserManager(user);
        var controller = BuildController(db, userManager.Object);

        var result = await controller.Create(user.Id, "existing");

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Single(db.ApiTokens);
    }

    private static ApiTokenController BuildController(ApplicationDbContext db, UserManager<ApplicationUser> userManager)
    {
        var controller = new ApiTokenController(
            NullLogger<ApiTokenController>.Instance,
            db,
            new ApiTokenService(db, userManager));

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
        var mgr = new Mock<UserManager<ApplicationUser>>(store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        mgr.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        return mgr;
    }
}
