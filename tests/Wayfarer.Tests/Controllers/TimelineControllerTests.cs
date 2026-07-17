using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Wayfarer.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.ViewModels;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using AppLocation = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Private timeline controller (User area) validation/auth/data responses.
/// </summary>
public class TimelineControllerTests : TestBase
{
    [Fact]
    public async Task Index_UsesFallbackTimelineTitle_WhenNoCustomTitleExists()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice", displayName: "Alice");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db, BuildUserManager(user));
        ConfigureControllerWithUser(controller, user.Id);

        var result = await controller.Index();

        Assert.IsType<ViewResult>(result);
        Assert.Equal("Timeline of Alice", controller.ViewData["TimelineTitle"]);
    }

    [Fact]
    public void ResolveTimelineTitle_UsesUsername_WhenDisplayNameIsEmpty()
    {
        var user = TestDataFixtures.CreateUser(username: "alice", displayName: " ");

        Assert.Equal("Timeline of alice", user.ResolveTimelineTitle());
    }

    [Fact]
    public void ResolveTimelineTitle_UsesFallback_WhenCustomTitleIsWhitespace()
    {
        var user = TestDataFixtures.CreateUser(username: "alice", displayName: "Alice");
        user.TimelineTitle = " \t ";

        Assert.Equal("Timeline of Alice", user.ResolveTimelineTitle());
    }

    [Fact]
    public void ResolveTimelineTitle_ReturnsTrimmedCustomTitle()
    {
        var user = TestDataFixtures.CreateUser(username: "alice", displayName: "Alice");
        user.TimelineTitle = "  Alice's adventures  ";

        Assert.Equal("Alice's adventures", user.ResolveTimelineTitle());
    }

    [Fact]
    public async Task UpdateSettings_SavesTrimmedTimelineTitle_ForCurrentUserOnly()
    {
        var db = CreateDbContext();
        var currentUser = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        var otherUser = TestDataFixtures.CreateUser(id: "u2", username: "bob");
        otherUser.TimelineTitle = "Bob's timeline";
        db.Users.AddRange(currentUser, otherUser);
        await db.SaveChangesAsync();
        var controller = BuildController(db, BuildUserManager(currentUser));
        ConfigureControllerWithUser(controller, currentUser.Id);

        var result = await controller.UpdateSettings(new TimelineSettingsViewModel
        {
            TimelineTitle = "  Alice's adventures  ",
            PublicTimelineTimeThreshold = "now"
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Alice's adventures", currentUser.TimelineTitle);
        Assert.Equal("Bob's timeline", otherUser.TimelineTitle);
    }

    [Fact]
    public async Task UpdateSettings_ClearsWhitespaceOnlyTimelineTitle()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice", displayName: "Alice");
        user.TimelineTitle = "Old title";
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db, BuildUserManager(user));
        ConfigureControllerWithUser(controller, user.Id);

        var result = await controller.UpdateSettings(new TimelineSettingsViewModel
        {
            TimelineTitle = new string(' ', 81),
            PublicTimelineTimeThreshold = "now"
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Null(user.TimelineTitle);
        Assert.Equal("Timeline of Alice", user.ResolveTimelineTitle());
    }

    [Fact]
    public async Task UpdateSettings_AcceptsEightyCharacterTimelineTitle_AndRejectsLongerValue()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db, BuildUserManager(user));
        ConfigureControllerWithUser(controller, user.Id);
        var acceptedTitle = new string('a', 80);

        var acceptedResult = await controller.UpdateSettings(new TimelineSettingsViewModel
        {
            TimelineTitle = acceptedTitle,
            PublicTimelineTimeThreshold = "now"
        });

        Assert.IsType<RedirectToActionResult>(acceptedResult);
        Assert.Equal(acceptedTitle, user.TimelineTitle);

        var rejectedResult = await controller.UpdateSettings(new TimelineSettingsViewModel
        {
            TimelineTitle = new string('b', 81),
            PublicTimelineTimeThreshold = "now"
        });

        Assert.IsType<ViewResult>(rejectedResult);
        Assert.Equal(acceptedTitle, user.TimelineTitle);
        Assert.True(controller.ModelState.ContainsKey(nameof(TimelineSettingsViewModel.TimelineTitle)));
    }

    [Fact]
    public async Task Settings_RedirectsHome_WhenUserMissing()
    {
        var db = CreateDbContext();
        var userManager = BuildUserManager(null);
        var controller = BuildController(db, userManager);
        ConfigureControllerWithUser(controller, "ghost");

        var result = await controller.Settings();

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Home", redirect.ControllerName);
    }

    [Fact]
    public async Task Settings_ShowsTheResolvedDefaultTimelineTitle()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice", displayName: "Alice");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db, BuildUserManager(user));
        ConfigureControllerWithUser(controller, user.Id);

        var result = await controller.Settings();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<TimelineSettingsViewModel>(view.Model);
        Assert.Equal("Timeline of Alice", model.DefaultTimelineTitle);
    }

    [Fact]
    public async Task UpdateSettings_ReturnsView_WhenCustomThresholdInvalid()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var userManager = BuildUserManager(user);
        var controller = BuildController(db, userManager);
        ConfigureControllerWithUser(controller, user.Id);
        var model = new TimelineSettingsViewModel
        {
            IsTimelinePublic = true,
            PublicTimelineTimeThreshold = "custom",
            CustomThreshold = "not-a-timespan"
        };

        var result = await controller.UpdateSettings(model);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Settings", view.ViewName);
        Assert.True(controller.ModelState.ContainsKey("CustomThreshold"));
    }

    [Fact]
    public async Task UpdateSettings_PersistsOneDayDelay_ForPrivateToPublicBlankSubmission()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db, BuildUserManager(user));
        ConfigureControllerWithUser(controller, user.Id);

        var result = await controller.UpdateSettings(new TimelineSettingsViewModel { IsTimelinePublic = true });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(user.IsTimelinePublic);
        Assert.Equal("1d", user.PublicTimelineTimeThreshold);
    }

    [Fact]
    public async Task UpdateSettings_RejectsLivePublicSubmission_WithoutAcknowledgement()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        user.PublicTimelineTimeThreshold = "1d";
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db, BuildUserManager(user));
        ConfigureControllerWithUser(controller, user.Id);

        var result = await controller.UpdateSettings(new TimelineSettingsViewModel
        {
            IsTimelinePublic = true,
            PublicTimelineTimeThreshold = "now",
            ConfirmLivePublicTimeline = false
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(user.IsTimelinePublic);
        Assert.Equal("1d", user.PublicTimelineTimeThreshold);
        Assert.True(controller.ModelState.ContainsKey(nameof(TimelineSettingsViewModel.ConfirmLivePublicTimeline)));
    }

    [Fact]
    public async Task UpdateSettings_PersistsLivePublicSubmission_WithAcknowledgement()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db, BuildUserManager(user));
        ConfigureControllerWithUser(controller, user.Id);

        var result = await controller.UpdateSettings(new TimelineSettingsViewModel
        {
            IsTimelinePublic = true,
            PublicTimelineTimeThreshold = "now",
            ConfirmLivePublicTimeline = true
        });

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(user.IsTimelinePublic);
        Assert.Equal("now", user.PublicTimelineTimeThreshold);
    }

    [Fact]
    public async Task UpdateSettings_DoesNotRepairExistingPublicInvalidThreshold_WhenBlankPosted()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        user.IsTimelinePublic = true;
        user.PublicTimelineTimeThreshold = "stale";
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db, BuildUserManager(user));
        ConfigureControllerWithUser(controller, user.Id);

        var result = await controller.UpdateSettings(new TimelineSettingsViewModel { IsTimelinePublic = true });

        Assert.IsType<ViewResult>(result);
        Assert.True(user.IsTimelinePublic);
        Assert.Equal("stale", user.PublicTimelineTimeThreshold);
    }

    [Fact]
    public async Task Settings_ShowsRemediation_ForExistingPublicInvalidThreshold()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        user.IsTimelinePublic = true;
        user.PublicTimelineTimeThreshold = "stale";
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db, BuildUserManager(user));
        ConfigureControllerWithUser(controller, user.Id);

        var result = await controller.Settings();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<TimelineSettingsViewModel>(view.Model);
        Assert.True(model.HasInvalidPublicTimelineThreshold);
        Assert.Equal("Public timeline unavailable until a valid threshold is selected and saved.", model.PublicTimelineStatus);
    }

    [Fact]
    public async Task GetChronologicalData_ReturnsUnauthorized_WhenUserMissing()
    {
        var db = CreateDbContext();
        var userManager = BuildUserManager(null);
        var controller = BuildController(db, userManager);
        ConfigureControllerWithUser(controller, "ghost");

        var result = await controller.GetChronologicalData("day", 2024, 6, 1);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var success = unauthorized.Value?.GetType().GetProperty("success")?.GetValue(unauthorized.Value) as bool?;
        Assert.False(success);
    }

    [Fact]
    public async Task GetChronologicalData_ReturnsLocations_ForDay()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        db.Locations.Add(new AppLocation
        {
            UserId = user.Id,
            Timestamp = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            LocalTimestamp = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            TimeZoneId = "UTC",
            Coordinates = new Point(1, 2) { SRID = 4326 }
        });
        await db.SaveChangesAsync();
        var userManager = BuildUserManager(user);
        var controller = BuildController(db, userManager);
        ConfigureControllerWithUser(controller, user.Id);

        var result = await controller.GetChronologicalData("day", 2024, 6, 1);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var success = payload.GetType().GetProperty("success")?.GetValue(payload) as bool?;
        var totalItems = payload.GetType().GetProperty("totalItems")?.GetValue(payload) as int?;
        Assert.True(success);
        Assert.Equal(1, totalItems);
    }

    [Fact]
    public async Task GetChronologicalStats_ReturnsCounts_ForMonth()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "stats-user", username: "stat");
        db.Users.Add(user);
        db.Locations.AddRange(
            CreateLoc(user.Id, new DateTime(2024, 6, 1)),
            CreateLoc(user.Id, new DateTime(2024, 6, 15)),
            CreateLoc(user.Id, new DateTime(2024, 7, 1)));
        await db.SaveChangesAsync();
        var userManager = BuildUserManager(user);
        var stats = new UserLocationStatsDto { TotalLocations = 2 };
        var statsService = new Mock<ILocationStatsService>();
        statsService.Setup(s => s.GetStatsForDateRangeAsync(user.Id, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(stats);
        var controller = BuildController(db, userManager, statsService.Object);
        ConfigureControllerWithUser(controller, user.Id);

        var result = await controller.GetChronologicalStats("month", 2024, month: 6);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var success = payload.GetType().GetProperty("success")?.GetValue(payload) as bool?;
        var returnedStats = payload.GetType().GetProperty("stats")?.GetValue(payload) as UserLocationStatsDto;
        Assert.True(success);
        Assert.NotNull(returnedStats);
        Assert.Equal(2, returnedStats!.TotalLocations);
    }

    [Fact]
    public async Task GetChronologicalStatsDetailed_ReturnsAggregation_ForYear()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "stats-detail", username: "stat2");
        db.Users.Add(user);
        db.Locations.AddRange(
            CreateLoc(user.Id, new DateTime(2024, 1, 1)),
            CreateLoc(user.Id, new DateTime(2024, 5, 1)),
            CreateLoc(user.Id, new DateTime(2024, 12, 31)));
        await db.SaveChangesAsync();
        var userManager = BuildUserManager(user);
        var detailedStats = new UserLocationStatsDetailedDto { TotalLocations = 3 };
        var statsService = new Mock<ILocationStatsService>();
        statsService.Setup(s => s.GetDetailedStatsForDateRangeAsync(user.Id, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(detailedStats);
        var controller = BuildController(db, userManager, statsService.Object);
        ConfigureControllerWithUser(controller, user.Id);

        var result = await controller.GetChronologicalStatsDetailed("year", 2024);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var success = payload.GetType().GetProperty("success")?.GetValue(payload) as bool?;
        var returnedStats = payload.GetType().GetProperty("stats")?.GetValue(payload) as UserLocationStatsDetailedDto;
        Assert.True(success);
        Assert.NotNull(returnedStats);
        Assert.Equal(3, returnedStats!.TotalLocations);
    }

    [Fact]
    public async Task CheckNavigationAvailability_ReturnsFlags()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "nav-user", username: "nav");
        db.Users.Add(user);
        var today = DateTime.Now.Date;
        db.Locations.Add(CreateLoc(user.Id, today));
        await db.SaveChangesAsync();
        var controller = BuildController(db, BuildUserManager(user));
        ConfigureControllerWithUser(controller, user.Id);

        var result = await controller.CheckNavigationAvailability("day", today.Year, today.Month, today.Day);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        bool? prevDay = payload.GetType().GetProperty("canNavigatePrevDay")?.GetValue(payload) as bool?;
        bool? nextDay = payload.GetType().GetProperty("canNavigateNextDay")?.GetValue(payload) as bool?;
        Assert.True(prevDay);
        Assert.False(nextDay);
    }

    private static AppLocation CreateLoc(string userId, DateTime localTimestamp)
    {
        return new AppLocation
        {
            UserId = userId,
            Timestamp = DateTime.SpecifyKind(localTimestamp, DateTimeKind.Utc),
            LocalTimestamp = DateTime.SpecifyKind(localTimestamp, DateTimeKind.Utc),
            TimeZoneId = "UTC",
            Coordinates = new Point(0, 0) { SRID = 4326 }
        };
    }

    private static TimelineController BuildController(
        ApplicationDbContext db,
        Mock<UserManager<ApplicationUser>> userManager,
        ILocationStatsService? statsService = null)
    {
        var locationService = new LocationService(db);
        statsService ??= new LocationStatsService(db);
        var controller = new TimelineController(
            NullLogger<BaseController>.Instance,
            db,
            userManager.Object,
            locationService,
            statsService);
        controller.TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>());
        return controller;
    }

    private static Mock<UserManager<ApplicationUser>> BuildUserManager(ApplicationUser? user)
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        var mgr = new Mock<UserManager<ApplicationUser>>(store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        mgr.Setup(m => m.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(user);
        mgr.Setup(m => m.GetUserId(It.IsAny<ClaimsPrincipal>())).Returns(user?.Id);
        return mgr;
    }
}
