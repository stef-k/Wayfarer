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
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Admin API token regenerate/delete guardrails.
/// </summary>
public class AdminApiTokenRegenerateDeleteTests : TestBase
{
    [Fact]
    public async Task Regenerate_WhenTokenMissing_ShowsAlertAndRedirects()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "user-x", username: "bob");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var userManager = MockUserManager(user);
        var controller = BuildController(db, userManager.Object);

        var result = await controller.Regenerate(user.Id, "missing");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
    }

    [Fact]
    public async Task Delete_WhenTokenDoesNotBelongToUser_ShowsAlert()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner", username: "alice");
        var other = TestDataFixtures.CreateUser(id: "other", username: "eve");
        db.Users.AddRange(owner, other);
        db.ApiTokens.Add(new ApiToken { Id = 10, Name = "test", Token = "wf_t", UserId = owner.Id, User = owner });
        await db.SaveChangesAsync();

        var userManager = MockUserManager(owner);
        var controller = BuildController(db, userManager.Object);

        var result = await controller.Delete(other.Id, 10);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
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
