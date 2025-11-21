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
/// Manager API token controller coverage.
/// </summary>
public class ManagerApiTokenControllerTests : TestBase
{
    [Fact]
    public async Task Create_AddsToken_ForManagerUser()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "mgr-user", username: "manager");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(user);
        var controller = BuildController(db, userManager.Object);

        var result = await controller.Create(user.Id, "manager-api");

        Assert.IsType<RedirectToActionResult>(result);
        var token = Assert.Single(db.ApiTokens);
        Assert.Equal(user.Id, token.UserId);
        Assert.Equal("manager-api", token.Name);
    }

    [Fact]
    public async Task Index_Redirects_WhenUserMissing()
    {
        var db = CreateDbContext();
        var controller = BuildController(db, MockUserManager(TestDataFixtures.CreateUser(id: "mgr", username: "mgr")).Object);

        var result = await controller.Index("missing");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Home", redirect.ControllerName);
    }

    [Fact]
    public async Task Index_ReturnsView_WithTokens()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "mgr-user3", username: "carol");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { Id = 7, UserId = user.Id, User = user, Name = "existing", Token = "tok" });
        await db.SaveChangesAsync();
        var userManager = MockUserManager(user);
        var controller = BuildController(db, userManager.Object);

        var result = await controller.Index(user.Id);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ApiTokenViewModel>(view.Model);
        Assert.Equal(user.Id, model.UserId);
        Assert.Single(model.Tokens);
        Assert.Equal("existing", model.Tokens.Single().Name);
    }

    [Fact]
    public async Task Create_ShowsWarning_WhenNameExists()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "mgr-user4", username: "dave");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { Id = 9, UserId = user.Id, User = user, Name = "dupe", Token = "tok" });
        await db.SaveChangesAsync();
        var controller = BuildController(db, MockUserManager(user).Object);

        var result = await controller.Create(user.Id, "dupe");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Single(db.ApiTokens);
    }

    [Fact]
    public async Task Regenerate_UpdatesTokenValue()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "mgr-user5", username: "erin");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { Id = 11, UserId = user.Id, User = user, Name = "reporting", Token = "old" });
        await db.SaveChangesAsync();
        var controller = BuildController(db, MockUserManager(user).Object);

        var result = await controller.Regenerate(user.Id, "reporting");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        var token = Assert.Single(db.ApiTokens);
        Assert.NotEqual("old", token.Token);
    }

    [Fact]
    public async Task Delete_RejectsDefaultToken()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "mgr-user2", username: "bob");
        db.Users.Add(user);
        var token = new ApiToken
        {
            Id = 5,
            Name = "Wayfarer Incoming Location Data API Token",
            Token = "wf_token",
            UserId = user.Id,
            User = user
        };
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync();

        var userManager = MockUserManager(user);
        var controller = BuildController(db, userManager.Object);

        var result = await controller.Delete(user.Id, token.Id);

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
