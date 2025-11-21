using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using System.Security.Claims;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// User settings controller simple load behavior.
/// </summary>
public class SettingsControllerTests : TestBase
{
    [Fact]
    public async Task Index_ReturnsView_WithUserModel()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var userManager = BuildUserManager(user);
        var controller = new SettingsController(db, NullLogger<SettingsController>.Instance, userManager.Object);
        ConfigureControllerWithUser(controller, user.Id);

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal(user, view.Model);
    }

    private static Mock<UserManager<ApplicationUser>> BuildUserManager(ApplicationUser user)
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        var mgr = new Mock<UserManager<ApplicationUser>>(store.Object, null, null, null, null, null, null, null, null);
        mgr.Setup(m => m.GetUserId(It.IsAny<ClaimsPrincipal>())).Returns(user.Id);
        mgr.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        return mgr;
    }
}
