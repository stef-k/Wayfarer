using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Settings-specific public timeline transition and acknowledgement coverage.
/// </summary>
public class TimelineSettingsControllerTests : TestBase
{
    [Fact]
    public async Task UpdateSettings_PersistsOneDayDelay_ForPrivateToPublicBlankSubmission()
    {
        var (db, user) = await CreateUserAsync();
        var result = await BuildController(db, user).UpdateSettings(new TimelineSettingsViewModel { IsTimelinePublic = true });
        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(user.IsTimelinePublic);
        Assert.Equal("1d", user.PublicTimelineTimeThreshold);
    }

    [Fact]
    public async Task UpdateSettings_RejectsLivePublicSubmission_WithoutAcknowledgement()
    {
        var (db, user) = await CreateUserAsync("1d");
        var controller = BuildController(db, user);
        var result = await controller.UpdateSettings(new TimelineSettingsViewModel { IsTimelinePublic = true, PublicTimelineTimeThreshold = "now" });
        Assert.IsType<ViewResult>(result);
        Assert.False(user.IsTimelinePublic);
        Assert.Equal("1d", user.PublicTimelineTimeThreshold);
        Assert.True(controller.ModelState.ContainsKey(nameof(TimelineSettingsViewModel.ConfirmLivePublicTimeline)));
    }

    [Fact]
    public async Task UpdateSettings_PersistsLivePublicSubmission_WithAcknowledgement()
    {
        var (db, user) = await CreateUserAsync();
        var result = await BuildController(db, user).UpdateSettings(new TimelineSettingsViewModel { IsTimelinePublic = true, PublicTimelineTimeThreshold = "now", ConfirmLivePublicTimeline = true });
        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(user.IsTimelinePublic);
        Assert.Equal("now", user.PublicTimelineTimeThreshold);
    }

    [Fact]
    public async Task UpdateSettings_DoesNotRepairExistingPublicInvalidThreshold_WhenBlankPosted()
    {
        var (db, user) = await CreateUserAsync("stale", isPublic: true);
        var result = await BuildController(db, user).UpdateSettings(new TimelineSettingsViewModel { IsTimelinePublic = true });
        Assert.IsType<ViewResult>(result);
        Assert.True(user.IsTimelinePublic);
        Assert.Equal("stale", user.PublicTimelineTimeThreshold);
    }

    [Fact]
    public async Task Settings_ShowsRemediation_ForExistingPublicInvalidThreshold()
    {
        var (db, user) = await CreateUserAsync("stale", isPublic: true);
        var result = await BuildController(db, user).Settings();
        var model = Assert.IsType<TimelineSettingsViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.True(model.HasInvalidPublicTimelineThreshold);
        Assert.Equal("Public timeline unavailable until a valid threshold is selected and saved.", model.PublicTimelineStatus);
    }

    private async Task<(ApplicationDbContext Db, ApplicationUser User)> CreateUserAsync(string? threshold = null, bool isPublic = false)
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: Guid.NewGuid().ToString(), username: "alice");
        user.IsTimelinePublic = isPublic;
        user.PublicTimelineTimeThreshold = threshold;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (db, user);
    }

    private static TimelineController BuildController(ApplicationDbContext db, ApplicationUser user)
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        var manager = new Mock<UserManager<ApplicationUser>>(store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        manager.Setup(value => value.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(user);
        var controller = new TimelineController(NullLogger<BaseController>.Instance, db, manager.Object, new LocationService(db), new LocationStatsService(db));
        controller.TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = CreateUserPrincipal(user.Id) } };
        return controller;
    }
}
