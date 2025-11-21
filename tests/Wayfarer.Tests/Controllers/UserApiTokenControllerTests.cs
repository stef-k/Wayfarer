using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// User area API token management flows.
/// </summary>
public class UserApiTokenControllerTests : TestBase
{
    [Fact]
    public async Task Index_ReturnsTokensForCurrentUser()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { UserId = user.Id, User = user, Name = "one", Token = "t1", CreatedAt = DateTime.UtcNow });
        db.ApiTokens.Add(new ApiToken { UserId = user.Id, User = user, Name = "two", Token = "t2", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var controller = BuildController(db, user);

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var vm = Assert.IsType<ApiTokenViewModel>(view.Model);
        Assert.Equal("alice", vm.UserName);
        Assert.Equal(2, vm.Tokens.Count);
    }

    [Fact]
    public async Task Create_RejectsDuplicateNameForUser()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { UserId = user.Id, User = user, Name = "dup", Token = "t1", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var controller = BuildController(db, user);

        var result = await controller.Create("dup");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
    }

    [Fact]
    public async Task Create_PersistsNewToken()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db, user);

        var result = await controller.Create("new-token");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Single(db.ApiTokens.Where(t => t.UserId == user.Id && t.Name == "new-token"));
    }

    [Fact]
    public async Task StoreThirdPartyToken_RejectsDuplicateName()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { UserId = user.Id, User = user, Name = "mapbox", Token = "t1", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var controller = BuildController(db, user);

        var result = await controller.StoreThirdPartyToken("Mapbox", "new-token");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal(1, db.ApiTokens.Count(t => t.UserId == user.Id));
    }

    [Fact]
    public async Task StoreThirdPartyToken_CreatesWhenUnique()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db, user);

        var result = await controller.StoreThirdPartyToken("Mapbox", "third");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Single(db.ApiTokens.Where(t => t.UserId == user.Id && t.Name == "Mapbox"));
    }

    [Fact]
    public async Task Regenerate_ReturnsJsonWithToken()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { UserId = user.Id, User = user, Name = "api", Token = "old", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var controller = BuildController(db, user);

        var result = await controller.Regenerate("api");

        var json = Assert.IsType<JsonResult>(result);
        var success = json.Value!.GetType().GetProperty("success")!.GetValue(json.Value);
        var token = json.Value!.GetType().GetProperty("token")!.GetValue(json.Value) as string;
        Assert.Equal(true, success);
        Assert.False(string.IsNullOrWhiteSpace(token));
    }

    [Fact]
    public async Task Delete_BlocksDefaultToken()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { UserId = user.Id, User = user, Name = "Wayfarer Incoming Location Data API Token", Token = "keep", CreatedAt = DateTime.UtcNow, Id = 5 });
        await db.SaveChangesAsync();
        var controller = BuildController(db, user);

        var result = await controller.Delete(5);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Single(db.ApiTokens);
    }

    [Fact]
    public async Task DeleteConfirmed_RemovesOwnedToken()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { UserId = user.Id, User = user, Name = "api", Token = "tok", CreatedAt = DateTime.UtcNow, Id = 12 });
        await db.SaveChangesAsync();
        var controller = BuildController(db, user);

        var result = await controller.DeleteConfirmed(12);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Empty(db.ApiTokens);
    }

    [Fact]
    public async Task Delete_ReturnsNotFoundForMissingToken()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db, user);

        var result = await controller.Delete(99);

        Assert.IsType<NotFoundResult>(result);
    }

    private static ApiTokenController BuildController(ApplicationDbContext db, ApplicationUser user)
    {
        var userManager = MockUserManager(user);
        var apiTokenService = new ApiTokenService(db, userManager.Object);
        var controller = new ApiTokenController(apiTokenService, NullLogger<BaseController>.Instance, db);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id),
                new Claim(ClaimTypes.Name, user.UserName!)
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
