using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models.Dtos;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Calendar validation and statistics bounds at the existing API controller seam.</summary>
public partial class ApiLocationControllerTests
{
    /// <summary>Both actions reject invalid components; statistics preserves exact error precedence without service calls.</summary>
    [Theory]
    [InlineData("month", 2024, 13, null, "Month must be between 1 and 12")]
    [InlineData("month", 2024, 0, null, "Month must be between 1 and 12")]
    [InlineData("day", 2024, -1, 1, "Month must be between 1 and 12")]
    [InlineData("day", 2024, 13, null, "Month and day are required for day filter")]
    [InlineData("day", 2026, 2, 30, "Day is invalid for the selected year and month")]
    [InlineData("day", 2025, 2, 29, "Day is invalid for the selected year and month")]
    [InlineData("day", 1900, 2, 29, "Day is invalid for the selected year and month")]
    [InlineData("day", 2024, 1, 0, "Day is invalid for the selected year and month")]
    [InlineData("day", 2024, 1, -1, "Day is invalid for the selected year and month")]
    [InlineData("day", 2024, 1, 32, "Day is invalid for the selected year and month")]
    [InlineData("day", 2024, null, 32, "Month and day are required for day filter")]
    [InlineData("day", 2024, null, 0, "Month and day are required for day filter")]
    [InlineData("day", 0, 13, 32, "Year must be between 1 and 9999")]
    [InlineData("day", 2024, 13, 32, "Month must be between 1 and 12")]
    [InlineData("month", 0, 13, 32, "Year must be between 1 and 9999")]
    [InlineData("year", 0, null, null, "Year must be between 1 and 9999")]
    [InlineData("year", -1, null, null, "Year must be between 1 and 9999")]
    [InlineData("year", 10000, null, null, "Year must be between 1 and 9999")]
    [InlineData("year", int.MaxValue, null, null, "Year must be between 1 and 9999")]
    [InlineData("unknown", 0, null, null, "Invalid dateType: unknown")]
    [InlineData(" day ", 2024, 2, 29, "Invalid dateType:  day ")]
    [InlineData(" day ", 0, null, null, "Invalid dateType:  day ")]
    [InlineData("", 2024, null, null, "Invalid dateType: ")]
    [InlineData(null, 2024, null, null, "Invalid dateType: ")]
    public async Task CalendarActions_RejectInvalidComponents(string? dateType, int year, int? month, int? day, string message)
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var stats = new StubStatsService(new UserLocationStatsDto());
        var controller = BuildApiController(db, user, statsService: stats);

        var statsResult = Assert.IsType<BadRequestObjectResult>(
            await controller.GetChronologicalStats(dateType!, year, month, day));
        var statsBody = JsonSerializer.SerializeToElement(statsResult.Value);
        Assert.False(statsBody.GetProperty("success").GetBoolean());
        Assert.Equal(message, statsBody.GetProperty("message").GetString());
        var navigationResult = Assert.IsType<BadRequestObjectResult>(
            controller.CheckNavigationAvailability(dateType!, year, month, day));
        Assert.Equal("{\"success\":false}", JsonSerializer.Serialize(navigationResult.Value));
        Assert.Null(stats.DateRange);
    }

    /// <summary>Missing components precede invalid numbers, including the zero year bound by MVC when omitted.</summary>
    [Theory]
    [InlineData("day", 2024, null, null, "Month and day are required for day filter")]
    [InlineData("day", 2024, 2, null, "Month and day are required for day filter")]
    [InlineData("day", 2024, null, 29, "Month and day are required for day filter")]
    [InlineData("DaY", 2024, 13, null, "Month and day are required for day filter")]
    [InlineData("day", 0, 13, null, "Month and day are required for day filter")]
    [InlineData("day", 0, null, 29, "Month and day are required for day filter")]
    [InlineData("day", 10000, null, 32, "Month and day are required for day filter")]
    [InlineData("month", 2024, null, null, "Month is required for month filter")]
    [InlineData("MoNtH", 0, null, null, "Month is required for month filter")]
    [InlineData("month", 10000, null, 32, "Month is required for month filter")]
    public async Task GetChronologicalStats_RequiresSelectedComponents(string dateType, int year, int? month, int? day,
        string message)
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var stats = new StubStatsService(new UserLocationStatsDto());
        var controller = BuildApiController(db, user, statsService: stats);

        var result = Assert.IsType<BadRequestObjectResult>(await controller.GetChronologicalStats(dateType, year, month, day));

        Assert.Equal(JsonSerializer.Serialize(new { success = false, message }), JsonSerializer.Serialize(result.Value));
        Assert.Null(stats.DateRange);
    }

    /// <summary>Case-insensitive modes retain exact inclusive UTC selections, ignored extras and maximum dates.</summary>
    [Theory]
    [InlineData("DaY", 2024, 2, 29, "2024-02-29T00:00:00Z", "2024-02-29T23:59:59.9999999Z")]
    [InlineData("day", 2000, 2, 29, "2000-02-29T00:00:00Z", "2000-02-29T23:59:59.9999999Z")]
    [InlineData("MoNtH", 2024, 2, 32, "2024-02-01T00:00:00Z", "2024-02-29T23:59:59.9999999Z")]
    [InlineData("YEAR", 2024, 13, 32, "2024-01-01T00:00:00Z", "2024-12-31T23:59:59.9999999Z")]
    [InlineData("day", 2026, 4, 15, "2026-04-15T00:00:00Z", "2026-04-15T23:59:59.9999999Z")]
    [InlineData("day", 9999, 12, 31, "9999-12-31T00:00:00Z", "9999-12-31T23:59:59.9999999Z")]
    [InlineData("month", 9999, 12, null, "9999-12-01T00:00:00Z", "9999-12-31T23:59:59.9999999Z")]
    [InlineData("year", 9999, null, null, "9999-01-01T00:00:00Z", "9999-12-31T23:59:59.9999999Z")]
    public async Task GetChronologicalStats_PreservesUtcBoundsAndResponse(string dateType, int year, int? month,
        int? day, string expectedStart, string expectedEnd)
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var dto = new UserLocationStatsDto { TotalLocations = 7, CountriesVisited = 2, CitiesVisited = 3, RegionsVisited = 4 };
        var stats = new StubStatsService(dto);
        var controller = BuildApiController(db, user, statsService: stats);

        var result = Assert.IsType<OkObjectResult>(await controller.GetChronologicalStats(dateType, year, month, day));

        var request = Assert.IsType<(string UserId, DateTime StartDate, DateTime EndDate)>(stats.DateRange);
        Assert.Equal((user.Id, DateTime.Parse(expectedStart, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTime.Parse(expectedEnd, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)), request);
        Assert.Equal(DateTimeKind.Utc, request.StartDate.Kind);
        Assert.Equal(DateTimeKind.Utc, request.EndDate.Kind);
        Assert.Equal(JsonSerializer.Serialize(new { success = true, stats = dto }), JsonSerializer.Serialize(result.Value));
    }

    /// <summary>Downstream argument and other service faults remain safe server errors after valid input.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetChronologicalStats_ReturnsServerError_ForUnexpectedServiceFailure(bool argumentFailure)
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var stats = new StubStatsService(new UserLocationStatsDto())
        {
            DateRangeFailure = argumentFailure ? new ArgumentException("internal database details")
                : new InvalidOperationException("internal database details")
        };
        var controller = BuildApiController(db, user, statsService: stats);

        var result = Assert.IsType<ObjectResult>(await controller.GetChronologicalStats("day", 2024, 2, 29));

        Assert.Equal(500, result.StatusCode);
        Assert.Equal("{\"success\":false,\"message\":\"An error occurred while fetching stats.\"}", JsonSerializer.Serialize(result.Value));
        Assert.NotNull(stats.DateRange);
    }

    /// <summary>Incomplete and invalid dates do not override missing, wrong or inactive bearer authority.</summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData("wrong", true)]
    [InlineData("tok", false)]
    public async Task CalendarActions_ResolveBearerBeforeCalendarValidation(string? token, bool active)
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        user.IsActive = active;
        db.SaveChanges();
        var stats = new StubStatsService(new UserLocationStatsDto());
        var controller = BuildApiController(db, user, includeAuthHeader: false, statsService: stats);
        if (token != null) controller.Request.Headers.Authorization = $"Bearer {token}";

        var chronology = Assert.IsType<UnauthorizedObjectResult>(await controller.GetChronological("month", 2026, 13));
        var summary = Assert.IsType<UnauthorizedObjectResult>(await controller.GetChronologicalStats("day", 0, 13));
        var navigation = Assert.IsType<UnauthorizedObjectResult>(controller.CheckNavigationAvailability("month", 2026, 13));

        Assert.Equal(JsonSerializer.Serialize(chronology.Value), JsonSerializer.Serialize(summary.Value));
        Assert.Equal("{\"success\":false,\"message\":\"Invalid or missing API token.\"}", JsonSerializer.Serialize(summary.Value));
        Assert.Equal("{\"success\":false}", JsonSerializer.Serialize(navigation.Value));
        Assert.Null(stats.DateRange);
    }
}
