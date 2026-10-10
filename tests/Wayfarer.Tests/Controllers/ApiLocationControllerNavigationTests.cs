using Microsoft.AspNetCore.Http;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Mocks;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Navigation availability API path.
/// </summary>
public class ApiLocationControllerNavigationTests : TestBase
{
    /// <summary>Malformed integers precede bearer resolution; an omitted year remains action-level validation.</summary>
    [Theory]
    [InlineData("chronological-stats")]
    [InlineData("check-navigation-availability")]
    public async Task CalendarBinding_PreservesAuthenticationPrecedence_ThroughHttp(string route)
    {
        var db = CreateDbContext();
        SeedUserWithToken(db, "tok");
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();

        using var malformed = await client.GetAsync($"/api/location/{route}?dateType=month&year=2026&month=abc");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var problem = JsonDocument.Parse(await malformed.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("month", out _));
        Assert.False(problem.RootElement.TryGetProperty("success", out _));

        using var anonymous = await client.GetAsync($"/api/location/{route}?dateType=year");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "tok");
        using var authenticated = await client.GetAsync($"/api/location/{route}?dateType=year");
        Assert.Equal(HttpStatusCode.BadRequest, authenticated.StatusCode);
        using var failure = JsonDocument.Parse(await authenticated.Content.ReadAsStringAsync());
        Assert.False(failure.RootElement.GetProperty("success").GetBoolean());
    }

    /// <summary>Routes the three confirmed calendar witnesses through production MVC and bearer resolution.</summary>
    [Theory]
    [InlineData("chronological", "dateType=month&year=2026&month=13")]
    [InlineData("chronological-stats", "dateType=month&year=2026&month=13")]
    [InlineData("check-navigation-availability", "dateType=day&year=2026&month=2&day=30")]
    public async Task CalendarValidation_ReturnsBadRequest_ThroughHttp(string route, string query)
    {
        var db = CreateDbContext();
        SeedUserWithToken(db, "tok");
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "tok");

        using var response = await client.GetAsync($"/api/location/{route}?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(route == "check-navigation-availability" ? new[] { "success" } : new[] { "message", "success" },
            document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>Optional inputs and irrelevant numeric extras retain all six navigation flags.</summary>
    [Theory]
    [InlineData("DaY", 2024, 2, 29, true, true, true)]
    [InlineData("MoNtH", 2024, 2, 32, false, true, true)]
    [InlineData("YEAR", 2024, 13, 32, false, false, true)]
    [InlineData("day", 2024, null, null, false, false, true)]
    [InlineData("day", 2024, 2, null, false, true, true)]
    [InlineData("day", 2024, null, 31, false, false, true)]
    [InlineData("month", 2024, null, null, false, false, true)]
    [InlineData("day", 1, 1, 1, true, true, true)]
    [InlineData("day", 9999, 1, 1, false, false, false)]
    [InlineData("day", 9999, 12, 31, false, false, false)]
    [InlineData("day", 9999, 12, null, false, false, false)]
    [InlineData("month", 9999, 12, null, false, false, false)]
    [InlineData("year", 9999, null, null, false, false, false)]
    public void CheckNavigationAvailability_PreservesSelectionFlags(string dateType, int year, int? month, int? day,
        bool nextDay, bool nextMonth, bool nextYear)
    {
        var db = CreateDbContext();
        SeedUserWithToken(db, "tok");
        var controller = BuildController(db);

        AssertNavigationFlags(controller.CheckNavigationAvailability(dateType, year, month, day), nextDay, nextMonth, nextYear);
    }

    /// <summary>Month/year transitions preserve day context and clamp January 31 and leap day.</summary>
    [Fact]
    public void CheckNavigationAvailability_PreservesCalendarTransitions()
    {
        var db = CreateDbContext();
        SeedUserWithToken(db, "tok");
        var controller = BuildController(db);
        var today = DateTime.Today;
        var january = controller.CheckNavigationAvailability("day", today.Year, 1, 31);
        var februaryEnd = new DateTime(today.Year, 2, DateTime.IsLeapYear(today.Year) ? 29 : 28);
        AssertNavigationFlags(january, new DateTime(today.Year, 2, 1) <= today, februaryEnd <= today, false);

        var december = controller.CheckNavigationAvailability("day", today.Year - 1, 12, 31);
        AssertNavigationFlags(december, true, new DateTime(today.Year, 1, 31) <= today,
            new DateTime(today.Year, 12, 31) <= today);

        var leapYear = today.Year - 1;
        while (!DateTime.IsLeapYear(leapYear)) leapYear--;
        var leapDay = controller.CheckNavigationAvailability("day", leapYear, 2, 29);
        AssertNavigationFlags(leapDay, true, true, new DateTime(leapYear + 1, 2, 28) <= today);
    }

    /// <summary>Server-local today and future selections keep forward navigation unavailable.</summary>
    [Fact]
    public void CheckNavigationAvailability_RestrictsFutureDates()
    {
        var db = CreateDbContext();
        SeedUserWithToken(db, "tok");
        var controller = BuildController(db);
        var today = DateTime.Today;
        var future = today.AddYears(1);

        AssertNavigationFlags(controller.CheckNavigationAvailability("day", today.Year, today.Month, today.Day), false, false, false);
        AssertNavigationFlags(controller.CheckNavigationAvailability("day", future.Year, future.Month, future.Day), false, false, false);
    }

    /// <summary>Checks the complete successful response, including always-true previous navigation.</summary>
    private static void AssertNavigationFlags(IActionResult result, bool nextDay, bool nextMonth, bool nextYear)
    {
        var response = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(JsonSerializer.Serialize(new
        {
            success = true,
            canNavigatePrevDay = true,
            canNavigateNextDay = nextDay,
            canNavigatePrevMonth = true,
            canNavigateNextMonth = nextMonth,
            canNavigatePrevYear = true,
            canNavigateNextYear = nextYear
        }), JsonSerializer.Serialize(response.Value));
    }

    [Fact]
    public void CheckNavigationAvailability_ReturnsUnauthorized_WhenInactive()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        user.IsActive = false;
        db.SaveChanges();
        var controller = BuildController(db);
        var today = DateTime.Today;

        var result = controller.CheckNavigationAvailability("day", today.Year, today.Month, today.Day);

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    private LocationController BuildController(ApplicationDbContext db)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var settings = new ApplicationSettingsService(db, cache);
        var reverseGeocoding = new ReverseGeocodingService(new HttpClient(new FakeHandler()), NullLogger<BaseApiController>.Instance);
        var locationService = new LocationService(db);
        var sse = new SseService();
        var stats = new LocationStatsService(db);

        var controller = new LocationController(
            db,
            NullLogger<BaseApiController>.Instance,
            cache,
            settings,
            reverseGeocoding,
            locationService,
            sse,
            stats,
            locationService,
            new NullPlaceVisitDetectionService());

        var httpContext = BuildHttpContextWithUser("u1");
        httpContext.Request.Headers["Authorization"] = "Bearer tok";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static ApplicationUser SeedUserWithToken(ApplicationDbContext db, string token)
    {
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { Token = token, UserId = user.Id, Name = "test", User = user });
        db.SaveChanges();
        return user;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"features\":[]}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
