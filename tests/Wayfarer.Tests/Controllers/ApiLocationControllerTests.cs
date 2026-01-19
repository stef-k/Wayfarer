using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
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
/// API-facing location tests (bulk delete and navigation flags).
/// </summary>
public class ApiLocationControllerTests : TestBase
{
    [Fact]
    public async Task BulkDelete_RemovesOnlyCurrentUserLocations()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "api-user", username: "api-user");
        var other = TestDataFixtures.CreateUser(id: "api-other", username: "api-other");
        db.Users.AddRange(user, other);

        var userLocation1 = CreateLocation(user.Id, 1);
        var userLocation2 = CreateLocation(user.Id, 2);
        var otherLocation = CreateLocation(other.Id, 3);

        db.Locations.AddRange(userLocation1, userLocation2, otherLocation);
        await db.SaveChangesAsync();

        var controller = BuildApiController(db, user);

        var request = new LocationController.BulkDeleteRequest
        {
            LocationIds = new List<int> { userLocation1.Id, userLocation2.Id, otherLocation.Id }
        };

        var result = await controller.BulkDelete(request);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("2 locations deleted", payload);

        Assert.DoesNotContain(db.Locations, l => l.Id == userLocation1.Id);
        Assert.DoesNotContain(db.Locations, l => l.Id == userLocation2.Id);
        Assert.Contains(db.Locations, l => l.Id == otherLocation.Id);
    }

    [Fact]
    public async Task Update_ReturnsUnauthorized_WhenNoToken()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var controller = BuildApiController(db, user, includeAuthHeader: false, includeUserPrincipal: false);

        var result = await controller.Update(1, new LocationUpdateRequestDto());

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Update_ReturnsNotFound_WhenCookieAuthWithoutToken()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var controller = BuildApiController(db, user, includeAuthHeader: false, includeUserPrincipal: true);

        var result = await controller.Update(1, new LocationUpdateRequestDto());

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Update_ReturnsBadRequest_WhenLatWithoutLon()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        db.Locations.Add(CreateLocation(user.Id, 99));
        await db.SaveChangesAsync();
        var controller = BuildApiController(db, user);

        var result = await controller.Update(99, new LocationUpdateRequestDto { Latitude = 10 });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Update_ReturnsBadRequest_WhenCoordinatesOutOfRange()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        db.Locations.Add(CreateLocation(user.Id, 100));
        await db.SaveChangesAsync();
        var controller = BuildApiController(db, user);

        var result = await controller.Update(100, new LocationUpdateRequestDto { Latitude = 120, Longitude = 10 });

        Assert.IsType<BadRequestObjectResult>(result);
        var unchanged = db.Locations.First(l => l.Id == 100);
        Assert.Equal(100, unchanged.Coordinates.X);
        Assert.Equal(100, unchanged.Coordinates.Y);
    }

    [Fact]
    public async Task Update_ReturnsNotFound_WhenLocationDoesNotBelongToUser()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var other = TestDataFixtures.CreateUser(id: "other", username: "other");
        db.Users.Add(other);
        db.Locations.Add(CreateLocation(other.Id, 77));
        await db.SaveChangesAsync();
        var controller = BuildApiController(db, user);

        var result = await controller.Update(77, new LocationUpdateRequestDto { Latitude = 1, Longitude = 1 });

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Update_UpdatesCoordinates_AndNotes()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        db.Locations.Add(CreateLocation(user.Id, 42));
        await db.SaveChangesAsync();
        var controller = BuildApiController(db, user);

        var result = await controller.Update(42, new LocationUpdateRequestDto { Latitude = 1, Longitude = 2, Notes = "updated" });

        var ok = Assert.IsType<OkObjectResult>(result);
        var updated = db.Locations.First(l => l.Id == 42);
        Assert.Equal(2, updated.Coordinates.X);
        Assert.Equal(1, updated.Coordinates.Y);
        Assert.Equal("updated", updated.Notes);
    }

    [Fact]
    public async Task Update_ClearsNotesAndActivity()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var activity = new ActivityType { Id = 5, Name = "Hike" };
        db.Set<ActivityType>().Add(activity);
        db.Locations.Add(new Wayfarer.Models.Location
        {
            Id = 55,
            UserId = user.Id,
            Coordinates = new Point(0, 0) { SRID = 4326 },
            Timestamp = DateTime.UtcNow,
            LocalTimestamp = DateTime.UtcNow,
            TimeZoneId = "UTC",
            Notes = "old",
            ActivityTypeId = activity.Id
        });
        await db.SaveChangesAsync();
        var controller = BuildApiController(db, user);

        var result = await controller.Update(55, new LocationUpdateRequestDto { ClearNotes = true, ClearActivity = true });

        Assert.IsType<OkObjectResult>(result);
        var updated = db.Locations.First(l => l.Id == 55);
        Assert.Null(updated.Notes);
        Assert.Null(updated.ActivityTypeId);
    }

    [Fact]
    public async Task Update_SetsActivity_ByName()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var activity = new ActivityType { Id = 8, Name = "Run" };
        db.Set<ActivityType>().Add(activity);
        db.Locations.Add(CreateLocation(user.Id, 88));
        await db.SaveChangesAsync();
        var controller = BuildApiController(db, user);

        var result = await controller.Update(88, new LocationUpdateRequestDto { ActivityName = "Run" });

        Assert.IsType<OkObjectResult>(result);
        var updated = db.Locations.First(l => l.Id == 88);
        Assert.Equal(activity.Id, updated.ActivityTypeId);
    }

    [Fact]
    public async Task Update_ReturnsOk_WhenNoChangesApplied()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        db.Locations.Add(CreateLocation(user.Id, 15));
        await db.SaveChangesAsync();
        var controller = BuildApiController(db, user);

        var result = await controller.Update(15, new LocationUpdateRequestDto());

        Assert.IsType<OkObjectResult>(result);
        var existing = db.Locations.First(l => l.Id == 15);
        Assert.Equal(15, existing.Coordinates.X);
        Assert.Equal(15, existing.Coordinates.Y);
    }

    [Fact]
    public async Task BulkDelete_ReturnsNotFoundWhenNoMatchingIds()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "api-user", username: "api-user");
        db.Users.Add(user);
        db.Locations.Add(CreateLocation(user.Id, 10));
        await db.SaveChangesAsync();

        var controller = BuildApiController(db, user);
        var request = new LocationController.BulkDeleteRequest
        {
            LocationIds = new List<int> { 999, 1000 }
        };

        var result = await controller.BulkDelete(request);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = JsonSerializer.Serialize(notFound.Value);
        Assert.Contains("No locations found", payload);
        Assert.Single(db.Locations);
    }

    [Fact]
    public async Task Delete_RemovesSingleLocationForOwner()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "api-user", username: "api-user");
        var other = TestDataFixtures.CreateUser(id: "api-other", username: "api-other");
        db.Users.AddRange(user, other);

        var ownLoc = CreateLocation(user.Id, 50);
        var otherLoc = CreateLocation(other.Id, 60);
        db.Locations.AddRange(ownLoc, otherLoc);
        await db.SaveChangesAsync();

        var controller = BuildApiController(db, user);

        var result = await controller.Delete(ownLoc.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("Location deleted", payload);
        Assert.DoesNotContain(db.Locations, l => l.Id == ownLoc.Id);
        Assert.Contains(db.Locations, l => l.Id == otherLoc.Id);
    }

    [Fact]
    public void CheckNavigationAvailability_ReturnsUnauthorized_WhenTokenMissing()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "api-user", username: "api-user");
        db.Users.Add(user);
        db.SaveChanges();

        var controller = BuildApiController(db, user, includeAuthHeader: false);

        var result = controller.CheckNavigationAvailability("day", DateTime.UtcNow.Year, 1, 1);

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public void CheckNavigationAvailability_ReturnsFlags_ForCurrentDay()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "api-user", username: "api-user");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { Name = "mobile", Token = "tok", UserId = user.Id, User = user, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();

        var controller = BuildApiController(db, user, includeAuthHeader: true, tokenOverride: "tok");
        var today = DateTime.Today;

        var result = controller.CheckNavigationAvailability("day", today.Year, today.Month, today.Day);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        bool? canPrev = payload.GetType().GetProperty("canNavigatePrevDay")?.GetValue(payload) as bool?;
        bool? canNext = payload.GetType().GetProperty("canNavigateNextDay")?.GetValue(payload) as bool?;
        Assert.True(canPrev);
        Assert.False(canNext);
    }

    [Fact]
    public async Task CheckIn_ReturnsBadRequest_WhenCoordinatesAreZero()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "api-user", username: "api-user");
        db.Users.Add(user);
        var controller = BuildApiController(db, user);

        var result = await controller.CheckIn(new GpsLoggerLocationDto { Latitude = 0, Longitude = 0, Timestamp = DateTime.UtcNow });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CheckIn_ReturnsBadRequest_WhenLatitudeOutOfRange()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "api-user", username: "api-user");
        db.Users.Add(user);
        var controller = BuildApiController(db, user);

        var result = await controller.CheckIn(new GpsLoggerLocationDto { Latitude = 100, Longitude = 50, Timestamp = DateTime.UtcNow });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CheckIn_ReturnsBadRequest_WhenLongitudeOutOfRange()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "api-user", username: "api-user");
        db.Users.Add(user);
        var controller = BuildApiController(db, user);

        var result = await controller.CheckIn(new GpsLoggerLocationDto { Latitude = 50, Longitude = 200, Timestamp = DateTime.UtcNow });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CheckIn_ReturnsUnauthorized_WhenNoToken()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "api-user", username: "api-user");
        db.Users.Add(user);
        var controller = BuildApiController(db, user, includeAuthHeader: false);

        var result = await controller.CheckIn(new GpsLoggerLocationDto { Latitude = 40.7128, Longitude = -74.0060, Timestamp = DateTime.UtcNow });

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task CheckIn_CreatesLocation_WithValidData()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "api-user", username: "api-user");
        user.IsActive = true;
        db.Users.Add(user);
        db.ApplicationSettings.Add(new ApplicationSettings { Id = 1, LocationTimeThresholdMinutes = 5, LocationDistanceThresholdMeters = 15 });
        db.SaveChanges();
        var controller = BuildApiController(db, user);

        var result = await controller.CheckIn(new GpsLoggerLocationDto
        {
            Latitude = 40.7128,
            Longitude = -74.0060,
            Timestamp = DateTime.UtcNow,
            Accuracy = 10,
            Altitude = 50,
            Speed = 5
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Single(db.Locations.Where(l => l.UserId == user.Id));
        var location = db.Locations.First(l => l.UserId == user.Id);
        Assert.Equal(40.7128, location.Coordinates.Y, 4);
        Assert.Equal(-74.0060, location.Coordinates.X, 4);
        Assert.Equal(10, location.Accuracy);
        Assert.Equal(50, location.Altitude);
    }

    [Fact]
    public async Task CheckIn_ReturnsForbid_WhenUserInactive()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "api-user", username: "api-user");
        user.IsActive = false;
        db.Users.Add(user);
        db.SaveChanges();
        var controller = BuildApiController(db, user);

        var result = await controller.CheckIn(new GpsLoggerLocationDto { Latitude = 40.7128, Longitude = -74.0060, Timestamp = DateTime.UtcNow });

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GetStats_ReturnsUnauthorized_WhenNoToken()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var controller = BuildApiController(db, user, includeAuthHeader: false);

        var result = await controller.GetStats();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<UserLocationStatsDto>(ok.Value);
    }

    [Fact]
    public async Task GetStats_ReturnsStats_WhenAuthorized()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var statsDto = new UserLocationStatsDto { TotalLocations = 5 };
        var statsService = new StubStatsService(statsDto);
        var controller = BuildApiController(db, user, statsService: statsService);

        var result = await controller.GetStats();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(statsDto, ok.Value);
    }

    [Fact]
    public async Task GetChronological_ReturnsUnauthorized_WhenNoToken()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var controller = BuildApiController(db, user, includeAuthHeader: false);

        var result = await controller.GetChronological("day", DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day);

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task GetChronological_ReturnsForbid_WhenUserInactive()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        user.IsActive = false;
        db.SaveChanges();
        var controller = BuildApiController(db, user);

        var result = await controller.GetChronological("day", DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GetChronological_ReturnsData_ForDay()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        user.IsActive = true;
        db.ApplicationSettings.Add(new ApplicationSettings { Id = 1, LocationTimeThresholdMinutes = 5, LocationDistanceThresholdMeters = 1 });
        var today = DateTime.UtcNow.Date.AddHours(10);
        db.Locations.Add(new Wayfarer.Models.Location
        {
            UserId = user.Id,
            Coordinates = new Point(1, 2) { SRID = 4326 },
            Timestamp = today,
            LocalTimestamp = today,
            TimeZoneId = "UTC"
        });
        await db.SaveChangesAsync();

        var controller = BuildApiController(db, user);

        var result = await controller.GetChronological("day", today.Year, today.Month, today.Day);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var payloadType = payload.GetType();
        Assert.True((bool)(payloadType.GetProperty("success")?.GetValue(payload) ?? false));
        Assert.Equal(1, (int)(payloadType.GetProperty("totalItems")?.GetValue(payload) ?? 0));
    }

    [Fact]
    public async Task GetChronologicalStats_ReturnsBadRequest_ForInvalidDateType()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var controller = BuildApiController(db, user);

        var result = await controller.GetChronologicalStats("invalid", 2024);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetChronologicalStats_ReturnsStats_ForValidRange()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var statsDto = new UserLocationStatsDto { TotalLocations = 3 };
        var statsService = new StubStatsService(statsDto);
        var controller = BuildApiController(db, user, statsService: statsService);

        var result = await controller.GetChronologicalStats("year", 2024);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var payloadType = payload.GetType();
        Assert.True((bool)(payloadType.GetProperty("success")?.GetValue(payload) ?? false));
        Assert.Same(statsDto, payloadType.GetProperty("stats")?.GetValue(payload));
    }

    [Fact]
    public async Task HasDataForDate_ReturnsBadRequest_ForInvalidDate()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var controller = BuildApiController(db, user);

        var result = await controller.HasDataForDate("not-a-date");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task HasDataForDate_ReturnsOkFalse_WhenNoData()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var controller = BuildApiController(db, user);

        var result = await controller.HasDataForDate(DateTime.UtcNow.ToString("O"));

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var hasData = (bool)(payload.GetType().GetProperty("hasData")?.GetValue(payload) ?? false);
        Assert.False(hasData);
    }

    [Fact]
    public async Task Search_ReturnsUnauthorized_WhenNoUser()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var controller = BuildApiController(db, user, includeAuthHeader: false);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal();

        var result = await controller.Search(null, null!, null!, null!, null!, null!, null!, null!, null!, null!);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Search_FiltersByActivityNotesAndAddress()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var activity = new ActivityType { Id = 1, Name = "Walk" };
        db.ActivityTypes.Add(activity);
        db.Locations.Add(new Wayfarer.Models.Location
        {
            UserId = user.Id,
            Coordinates = new Point(1, 1) { SRID = 4326 },
            Timestamp = DateTime.UtcNow,
            LocalTimestamp = DateTime.UtcNow,
            TimeZoneId = "UTC",
            ActivityTypeId = activity.Id,
            ActivityType = activity,
            Notes = "morning walk",
            Address = "123 Main St",
            Country = "USA",
            Region = "CA",
            Place = "LA"
        });
        db.Locations.Add(new Wayfarer.Models.Location
        {
            UserId = user.Id,
            Coordinates = new Point(2, 2) { SRID = 4326 },
            Timestamp = DateTime.UtcNow,
            LocalTimestamp = DateTime.UtcNow,
            TimeZoneId = "UTC",
            ActivityTypeId = null,
            Notes = "other",
            Address = "Other St",
            Country = "Canada",
            Region = "BC",
            Place = "Vancouver"
        });
        await db.SaveChangesAsync();

        var controller = BuildApiController(db, user);

        var result = await controller.Search(null, null, null, null, "walk", "morning", "Main", "usa", "ca", "LA");

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var dataProp = payload.GetType().GetProperty("Data");
        var data = Assert.IsAssignableFrom<IEnumerable<object>>(dataProp!.GetValue(payload)!);
        Assert.Single(data);
    }

    private static ApplicationUser SeedUserWithToken(ApplicationDbContext db, string token)
    {
        var user = TestDataFixtures.CreateUser(id: $"user-{token}", username: $"user-{token}");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { Name = "mobile", Token = token, UserId = user.Id, User = user, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        return user;
    }

    private static LocationController BuildApiController(
        ApplicationDbContext db,
        ApplicationUser user,
        bool includeAuthHeader = true,
        string? tokenOverride = null,
        ILocationStatsService? statsService = null,
        bool includeUserPrincipal = true)
    {
        var token = tokenOverride ?? $"token-{user.Id}";
        if (!db.ApiTokens.Any(t => t.Token == token))
        {
            db.ApiTokens.Add(new ApiToken
            {
                Name = "mobile",
                Token = token,
                UserId = user.Id,
                User = user,
                CreatedAt = DateTime.UtcNow
            });
            db.SaveChanges();
        }

        var cache = new MemoryCache(new MemoryCacheOptions());
        var settings = new ApplicationSettingsService(db, cache);
        var reverseGeocoding = new ReverseGeocodingService(new HttpClient(new FakeHandler()), NullLogger<BaseApiController>.Instance);
        var locationService = new LocationService(db);
        var sse = new SseService();
        var stats = statsService ?? new LocationStatsService(db);

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

        var httpContext = new DefaultHttpContext();
        if (includeAuthHeader)
        {
            httpContext.Request.Headers["Authorization"] = $"Bearer {token}";
        }
        if (includeUserPrincipal)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id)
            }, "TestAuth"));
        }
        else
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        }

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        return controller;
    }

    private static Wayfarer.Models.Location CreateLocation(string userId, int seed)
    {
        return new Wayfarer.Models.Location
        {
            Id = seed,
            UserId = userId,
            Coordinates = new Point(seed, seed) { SRID = 4326 },
            Timestamp = DateTime.UtcNow,
            LocalTimestamp = DateTime.UtcNow,
            TimeZoneId = "UTC"
        };
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"features\":[]}", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StubStatsService : ILocationStatsService
    {
        private readonly UserLocationStatsDto _stats;
        public StubStatsService(UserLocationStatsDto stats) => _stats = stats;
        public Task<UserLocationStatsDto> GetStatsForUserAsync(string userId) => Task.FromResult(_stats);
        public Task<UserLocationStatsDto> GetStatsForDateRangeAsync(string userId, DateTime startDate, DateTime endDate) => Task.FromResult(_stats);
        public Task<UserLocationStatsDetailedDto> GetDetailedStatsForUserAsync(string userId) => Task.FromResult(new UserLocationStatsDetailedDto());
        public Task<UserLocationStatsDetailedDto> GetDetailedStatsForDateRangeAsync(string userId, DateTime startDate, DateTime endDate) => Task.FromResult(new UserLocationStatsDetailedDto());
    }
}
