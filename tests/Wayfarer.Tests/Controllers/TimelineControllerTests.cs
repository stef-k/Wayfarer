using System.Security.Claims;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>Both Web DTO adapters reject semantic errors before calling statistics, with required fields first.</summary>
    [Theory]
    [InlineData(null, 2024, null, null, "Invalid dateType: ")]
    [InlineData("", 2024, null, null, "Invalid dateType: ")]
    [InlineData(" day ", 2024, 2, 29, "Invalid dateType:  day ")]
    [InlineData("unknown", 0, null, null, "Invalid dateType: unknown")]
    [InlineData("DaY", 0, 13, null, "Month and day are required for day filter")]
    [InlineData("day", 10000, null, 32, "Month and day are required for day filter")]
    [InlineData("MoNtH", 0, null, 32, "Month is required for month filter")]
    [InlineData("year", 0, null, null, "Year must be between 1 and 9999")]
    [InlineData("year", -1, null, null, "Year must be between 1 and 9999")]
    [InlineData("year", 10000, null, null, "Year must be between 1 and 9999")]
    [InlineData("year", int.MaxValue, null, null, "Year must be between 1 and 9999")]
    [InlineData("day", 0, 13, 32, "Year must be between 1 and 9999")]
    [InlineData("month", 2024, 0, null, "Month must be between 1 and 12")]
    [InlineData("day", 2024, 13, 1, "Month must be between 1 and 12")]
    [InlineData("day", 2025, 2, 29, "Day is invalid for the selected year and month")]
    [InlineData("day", 2024, 2, 30, "Day is invalid for the selected year and month")]
    [InlineData("day", 2024, 1, 0, "Day is invalid for the selected year and month")]
    public async Task WebStats_RejectInvalidCalendar(string? mode, int year, int? month, int? day, string message)
    {
        var stats = new Mock<ILocationStatsService>(MockBehavior.Strict);
        var controller = BuildController(CreateDbContext(), BuildUserManager(TestDataFixtures.CreateUser()), stats.Object);
        foreach (var detailed in new[] { false, true })
        {
            var result = Assert.IsType<BadRequestObjectResult>(await Stats(controller, detailed, mode!, year, month, day));
            Assert.Equal(JsonSerializer.Serialize(new { success = false, message }), JsonSerializer.Serialize(result.Value));
        }
        stats.VerifyNoOtherCalls();
    }

    /// <summary>Summary and detail keep their DTO identities, owner and exact inclusive UTC bounds.</summary>
    [Theory]
    [InlineData("DaY", 2024, 2, 29, "2024-02-29T00:00:00Z", "2024-02-29T23:59:59.9999999Z")]
    [InlineData("MoNtH", 2024, 2, 32, "2024-02-01T00:00:00Z", "2024-02-29T23:59:59.9999999Z")]
    [InlineData("YEAR", 1, 13, 32, "0001-01-01T00:00:00Z", "0001-12-31T23:59:59.9999999Z")]
    [InlineData("day", 9999, 12, 31, "9999-12-31T00:00:00Z", "9999-12-31T23:59:59.9999999Z")]
    [InlineData("month", 9999, 12, null, "9999-12-01T00:00:00Z", "9999-12-31T23:59:59.9999999Z")]
    [InlineData("year", 9999, null, null, "9999-01-01T00:00:00Z", "9999-12-31T23:59:59.9999999Z")]
    public async Task WebStats_PreserveRangesAndDtos(string mode, int year, int? month, int? day,
        string first, string last)
    {
        var user = TestDataFixtures.CreateUser();
        var start = DateTime.Parse(first, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var end = DateTime.Parse(last, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var summary = new UserLocationStatsDto { TotalLocations = 7, CountriesVisited = 2 };
        var detail = new UserLocationStatsDetailedDto { TotalLocations = 7 };
        var stats = new Mock<ILocationStatsService>(MockBehavior.Strict);
        stats.Setup(s => s.GetStatsForDateRangeAsync(user.Id, start, end)).ReturnsAsync(summary);
        stats.Setup(s => s.GetDetailedStatsForDateRangeAsync(user.Id, start, end)).ReturnsAsync(detail);
        var controller = BuildController(CreateDbContext(), BuildUserManager(user), stats.Object);
        foreach (var detailed in new[] { false, true })
        {
            var result = Assert.IsType<OkObjectResult>(await Stats(controller, detailed, mode, year, month, day));
            var dto = result.Value!.GetType().GetProperty("stats")!.GetValue(result.Value);
            Assert.Same(detailed ? (object)detail : summary, dto);
            Assert.Equal(JsonSerializer.Serialize(new { success = true, stats = dto }), JsonSerializer.Serialize(result.Value));
        }
        Assert.All(stats.Invocations, call => Assert.All(call.Arguments.OfType<DateTime>(),
            date => Assert.Equal(DateTimeKind.Utc, date.Kind)));
        stats.VerifyAll();
    }

    /// <summary>Valid input never turns genuine downstream argument/operation faults into client errors.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebStats_PreserveDistinctServerErrors(bool argumentFailure)
    {
        var user = TestDataFixtures.CreateUser();
        Exception failure = argumentFailure ? new ArgumentException("private details") : new InvalidOperationException("private details");
        var stats = new Mock<ILocationStatsService>(MockBehavior.Strict);
        stats.Setup(s => s.GetStatsForDateRangeAsync(user.Id, It.IsAny<DateTime>(), It.IsAny<DateTime>())).ThrowsAsync(failure);
        stats.Setup(s => s.GetDetailedStatsForDateRangeAsync(user.Id, It.IsAny<DateTime>(), It.IsAny<DateTime>())).ThrowsAsync(failure);
        var controller = BuildController(CreateDbContext(), BuildUserManager(user), stats.Object);
        foreach (var detailed in new[] { false, true })
        {
            var result = Assert.IsType<ObjectResult>(await Stats(controller, detailed, "day", 2024, 2, 29));
            Assert.Equal(500, result.StatusCode);
            var message = detailed ? "An error occurred while fetching detailed stats." : "An error occurred while fetching stats.";
            Assert.Equal(JsonSerializer.Serialize(new { success = false, message }), JsonSerializer.Serialize(result.Value));
        }
        stats.VerifyAll();
    }

    /// <summary>Navigation rejects only bound applicable invalid components, including invalid day-only selections.</summary>
    [Theory]
    [InlineData(null, 2024, null, null)]
    [InlineData("", 2024, null, null)]
    [InlineData("unknown", 2024, null, null)]
    [InlineData(" day ", 2024, 2, 29)]
    [InlineData("year", 0, null, null)]
    [InlineData("year", -1, null, null)]
    [InlineData("year", 10000, null, null)]
    [InlineData("year", int.MaxValue, null, null)]
    [InlineData("month", 2024, 13, null)]
    [InlineData("day", 2024, 0, null)]
    [InlineData("day", 2025, 2, 29)]
    [InlineData("day", 2024, 2, 30)]
    [InlineData("day", 2024, null, 32)]
    [InlineData("day", 2024, null, 0)]
    public async Task WebNavigation_RejectInvalidCalendar(string? mode, int year, int? month, int? day)
    {
        var controller = BuildController(CreateDbContext(), BuildUserManager(TestDataFixtures.CreateUser()));
        var result = Assert.IsType<BadRequestObjectResult>(await controller.CheckNavigationAvailability(mode!, year, month, day));
        Assert.Equal("{\"success\":false}", JsonSerializer.Serialize(result.Value));
    }

    /// <summary>Past, partial, clamped leap transitions and maximum dates retain all six navigation meanings.</summary>
    [Theory]
    [InlineData("DaY", 2024, 1, 31, true, true, true)]
    [InlineData("day", 2024, 2, 29, true, true, true)]
    [InlineData("day", 2024, null, null, false, false, true)]
    [InlineData("day", 2024, null, 29, false, false, true)]
    [InlineData("day", 2024, 2, null, false, true, true)]
    [InlineData("month", 2024, null, 32, false, false, true)]
    [InlineData("month", 2024, 2, 32, false, true, true)]
    [InlineData("YEAR", 2024, 13, 32, false, false, true)]
    [InlineData("day", 9999, 1, 1, false, false, false)]
    [InlineData("day", 9999, 12, 31, false, false, false)]
    [InlineData("month", 9999, 12, null, false, false, false)]
    [InlineData("year", 9999, null, null, false, false, false)]
    public async Task WebNavigation_PreserveFlags(string mode, int year, int? month, int? day,
        bool nextDay, bool nextMonth, bool nextYear)
    {
        var controller = BuildController(CreateDbContext(), BuildUserManager(TestDataFixtures.CreateUser()));
        var result = Assert.IsType<OkObjectResult>(await controller.CheckNavigationAvailability(mode, year, month, day));
        AssertNavigation(result, nextDay, nextMonth, nextYear);
    }

    /// <summary>Today uses the server-local clock and disables future navigation in every direction.</summary>
    [Fact]
    public async Task WebNavigation_LocalTodayDisablesNextFlags()
    {
        var today = DateTime.Now.Date;
        var controller = BuildController(CreateDbContext(), BuildUserManager(TestDataFixtures.CreateUser()));
        var result = Assert.IsType<OkObjectResult>(await controller.CheckNavigationAvailability("day", today.Year, today.Month, today.Day));
        AssertNavigation(result, false, false, false);
    }

    /// <summary>Real cookie/MVC routing retains auth precedence, optional binding fallbacks and Web envelopes.</summary>
    [Fact]
    public async Task WebCalendarRoutes_PreserveAuthorityBindingAndMaximumSelection()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var stats = new Mock<ILocationStatsService>();
        stats.Setup(s => s.GetStatsForDateRangeAsync(user.Id, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new UserLocationStatsDto());
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(),
            services => services.AddSingleton(stats.Object));
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://wayfarer.test");
        var actions = new[] { "GetChronologicalStats", "GetChronologicalStatsDetailed", "CheckNavigationAvailability" };
        foreach (var action in actions)
        {
            var path = $"/User/Timeline/{action}?dateType=month&year=0&month=abc";
            using var anonymous = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            Assert.Contains("/Account/Login", anonymous.Headers.Location!.OriginalString);
            UserConnectionTokenHttpTests.Cookie(client, app.Services, user.Id, "Admin");
            using var denied = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
            Assert.Contains("/Account/AccessDenied", denied.Headers.Location!.OriginalString);
            client.DefaultRequestHeaders.Remove("Cookie");
            UserConnectionTokenHttpTests.Cookie(client, app.Services, "missing-user");
            using var missing = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
            var expected = action == "CheckNavigationAvailability" ? "{\"success\":false}"
                : "{\"success\":false,\"message\":\"User not found.\"}";
            Assert.Equal(expected, await missing.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Remove("Cookie");
        }
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        using var xhr = await client.GetAsync("/User/Timeline/GetChronologicalStats?dateType=month&year=2024&month=13");
        Assert.Equal(HttpStatusCode.Unauthorized, xhr.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Requested-With");
        UserConnectionTokenHttpTests.Cookie(client, app.Services, user.Id);
        var requests = new (string Action, string Query, int Status, string? Message)[]
        {
            (actions[0], "dateType=month&year=2024&month=13", 400, "Month must be between 1 and 12"),
            (actions[1], "dateType=month&year=2024&month=13", 400, "Month must be between 1 and 12"),
            (actions[2], "dateType=day&year=2024&month=2&day=30", 400, null),
            (actions[0], "dateType=month&year=2024&month=abc", 400, "Month is required for month filter"),
            (actions[2], "dateType=day&year=2024&month=abc&day=29", 200, null),
            (actions[2], "dateType=day&year=2024&month=2&day=", 200, null),
            (actions[0], "dateType=year&year=abc", 400, "Year must be between 1 and 9999"),
            (actions[2], "dateType=year", 400, null),
            (actions[0], "dateType=year&year=2024&month=abc&day=abc", 200, null),
            (actions[0], "dateType=day&year=9999&month=12&day=31", 200, null)
        };
        foreach (var request in requests)
        {
            using var response = await client.GetAsync($"/User/Timeline/{request.Action}?{request.Query}");
            Assert.Equal(request.Status, (int)response.StatusCode);
            var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            Assert.Equal(request.Status == 200, body.GetProperty("success").GetBoolean());
            if (request.Message != null) Assert.Equal(request.Message, body.GetProperty("message").GetString());
            if (request.Status == 400 && request.Message == null) Assert.Single(body.EnumerateObject());
        }
    }

    /// <summary>Exercises both adapters without changing their distinct production DTO contracts.</summary>
    private static Task<IActionResult> Stats(TimelineController controller, bool detailed, string mode, int year,
        int? month, int? day) => detailed ? controller.GetChronologicalStatsDetailed(mode, year, month, day)
            : controller.GetChronologicalStats(mode, year, month, day);

    /// <summary>Checks the complete existing navigation envelope, including all previous-direction flags.</summary>
    private static void AssertNavigation(OkObjectResult result, bool nextDay, bool nextMonth, bool nextYear) =>
        Assert.Equal(JsonSerializer.Serialize(new { success = true, canNavigatePrevDay = true, canNavigateNextDay = nextDay,
            canNavigatePrevMonth = true, canNavigateNextMonth = nextMonth, canNavigatePrevYear = true, canNavigateNextYear = nextYear }),
            JsonSerializer.Serialize(result.Value));

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
