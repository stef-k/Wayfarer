using System.Net;
using System.Net.Http;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
/// API Location log-location endpoint basics.
/// </summary>
public class ApiLocationControllerLogTests : TestBase
{
    [Fact]
    public async Task LogLocation_ReturnsUnauthorized_WhenTokenMissing()
    {
        var controller = BuildController(CreateDbContext(), includeAuth: false);

        var result = await controller.LogLocation(new GpsLoggerLocationDto { Latitude = 0, Longitude = 0, Timestamp = DateTime.UtcNow });

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task LogLocation_ReturnsBadRequest_ForOutOfRangeCoords()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.LogLocation(new GpsLoggerLocationDto { Latitude = 200, Longitude = 10, Timestamp = DateTime.UtcNow });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task LogLocation_CreatesLocation_ForValidInput()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);

        var result = await controller.LogLocation(new GpsLoggerLocationDto { Latitude = 10, Longitude = 20, Timestamp = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc) });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, db.Locations.Count());

        // Verify response format: { success: true, skipped: false, locationId: <id> }
        var response = ok.Value;
        var responseType = response!.GetType();
        Assert.True((bool)responseType.GetProperty("success")!.GetValue(response)!);
        Assert.False((bool)responseType.GetProperty("skipped")!.GetValue(response)!);
        var locationId = (int)responseType.GetProperty("locationId")!.GetValue(response)!;
        Assert.Equal(db.Locations.First().Id, locationId);
    }

    [Fact]
    public async Task LogLocation_ReturnsSkipped_WhenTimeThresholdNotMet()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);

        // First location - should succeed
        var firstResult = await controller.LogLocation(new GpsLoggerLocationDto
        {
            Latitude = 10,
            Longitude = 20,
            Timestamp = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc)
        });
        Assert.IsType<OkObjectResult>(firstResult);
        Assert.Equal(1, db.Locations.Count());

        // Second location - same time, different place (more than 15m away) but within time threshold
        // Should be skipped due to time threshold (default 5 minutes)
        var secondResult = await controller.LogLocation(new GpsLoggerLocationDto
        {
            Latitude = 11,
            Longitude = 21,
            Timestamp = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(1), DateTimeKind.Utc)
        });

        var ok = Assert.IsType<OkObjectResult>(secondResult);
        Assert.Equal(1, db.Locations.Count()); // No new location created

        // Verify response format: { success: true, skipped: true, locationId: null }
        var response = ok.Value;
        var responseType = response!.GetType();
        Assert.True((bool)responseType.GetProperty("success")!.GetValue(response)!);
        Assert.True((bool)responseType.GetProperty("skipped")!.GetValue(response)!);
        Assert.Null(responseType.GetProperty("locationId")!.GetValue(response));
    }

    [Fact]
    public async Task LogLocation_ReturnsSkipped_WhenDistanceThresholdNotMet()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);

        // First location - should succeed
        var firstResult = await controller.LogLocation(new GpsLoggerLocationDto
        {
            Latitude = 10,
            Longitude = 20,
            Timestamp = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc)
        });
        Assert.IsType<OkObjectResult>(firstResult);
        Assert.Equal(1, db.Locations.Count());

        // Second location - after time threshold (6 mins) but same place (within 15m)
        // Should be skipped due to distance threshold
        var secondResult = await controller.LogLocation(new GpsLoggerLocationDto
        {
            Latitude = 10.0001,  // ~11 meters difference
            Longitude = 20,
            Timestamp = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(6), DateTimeKind.Utc)
        });

        var ok = Assert.IsType<OkObjectResult>(secondResult);
        Assert.Equal(1, db.Locations.Count()); // No new location created

        // Verify response format: { success: true, skipped: true, locationId: null }
        var response = ok.Value;
        var responseType = response!.GetType();
        Assert.True((bool)responseType.GetProperty("success")!.GetValue(response)!);
        Assert.True((bool)responseType.GetProperty("skipped")!.GetValue(response)!);
        Assert.Null(responseType.GetProperty("locationId")!.GetValue(response));
    }

    [Fact]
    public async Task CheckIn_ReturnsUnauthorized_WhenTokenMissing()
    {
        var controller = BuildController(CreateDbContext(), includeAuth: false);

        var result = await controller.CheckIn(new GpsLoggerLocationDto());

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task CheckIn_CreatesLocation_ForValidInput()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);

        var result = await controller.CheckIn(new GpsLoggerLocationDto { Latitude = 5, Longitude = 6, Timestamp = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc) });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, db.Locations.Count());
    }

    /// <summary>
    /// Tests that a location request is skipped when GPS accuracy exceeds the threshold.
    /// </summary>
    [Fact]
    public async Task LogLocation_ReturnsSkipped_WhenAccuracyExceedsThreshold()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);

        // Send location with accuracy of 150m (default threshold is 100m)
        var result = await controller.LogLocation(new GpsLoggerLocationDto
        {
            Latitude = 10,
            Longitude = 20,
            Timestamp = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc),
            Accuracy = 150  // Exceeds default threshold of 100m
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, db.Locations.Count()); // No location created

        // Verify response format: { success: true, skipped: true, locationId: null }
        var response = ok.Value;
        var responseType = response!.GetType();
        Assert.True((bool)responseType.GetProperty("success")!.GetValue(response)!);
        Assert.True((bool)responseType.GetProperty("skipped")!.GetValue(response)!);
        Assert.Null(responseType.GetProperty("locationId")!.GetValue(response));
    }

    /// <summary>
    /// Tests that a location request is accepted when GPS accuracy is within the threshold.
    /// </summary>
    [Fact]
    public async Task LogLocation_CreatesLocation_WhenAccuracyWithinThreshold()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);

        // Send location with accuracy of 50m (default threshold is 100m)
        var result = await controller.LogLocation(new GpsLoggerLocationDto
        {
            Latitude = 10,
            Longitude = 20,
            Timestamp = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc),
            Accuracy = 50  // Within default threshold of 100m
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, db.Locations.Count()); // Location created

        // Verify response format: { success: true, skipped: false, locationId: <id> }
        var response = ok.Value;
        var responseType = response!.GetType();
        Assert.True((bool)responseType.GetProperty("success")!.GetValue(response)!);
        Assert.False((bool)responseType.GetProperty("skipped")!.GetValue(response)!);
        Assert.NotNull(responseType.GetProperty("locationId")!.GetValue(response));
    }

    /// <summary>
    /// Tests that a location request is accepted when no accuracy is provided.
    /// </summary>
    [Fact]
    public async Task LogLocation_CreatesLocation_WhenNoAccuracyProvided()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);

        // Send location without accuracy
        var result = await controller.LogLocation(new GpsLoggerLocationDto
        {
            Latitude = 10,
            Longitude = 20,
            Timestamp = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc),
            Accuracy = null  // No accuracy provided
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, db.Locations.Count()); // Location created

        // Verify response format: { success: true, skipped: false, locationId: <id> }
        var response = ok.Value;
        var responseType = response!.GetType();
        Assert.True((bool)responseType.GetProperty("success")!.GetValue(response)!);
        Assert.False((bool)responseType.GetProperty("skipped")!.GetValue(response)!);
        Assert.NotNull(responseType.GetProperty("locationId")!.GetValue(response));
    }

    /// <summary>
    /// Tests that a location request is skipped when a record with the same
    /// LocalTimestamp already exists for the user (duplicate detection).
    /// </summary>
    [Fact]
    public async Task LogLocation_ReturnsSkipped_WhenDuplicateLocalTimestampExists()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);
        var duplicateTimestamp = DateTime.SpecifyKind(new DateTime(2026, 1, 8, 6, 32, 23, 615, DateTimeKind.Utc), DateTimeKind.Utc);

        // First location - should succeed
        var firstResult = await controller.LogLocation(new GpsLoggerLocationDto
        {
            Latitude = 10,
            Longitude = 20,
            Timestamp = duplicateTimestamp
        });
        Assert.IsType<OkObjectResult>(firstResult);
        Assert.Equal(1, db.Locations.Count());

        // Second location - same LocalTimestamp but passes time/distance thresholds
        // (different coordinates, timestamp appears old relative to server time)
        // Should be skipped due to duplicate LocalTimestamp check
        var secondResult = await controller.LogLocation(new GpsLoggerLocationDto
        {
            Latitude = 15,  // Different location (passes distance threshold)
            Longitude = 25,
            Timestamp = duplicateTimestamp  // Same LocalTimestamp as first
        });

        var ok = Assert.IsType<OkObjectResult>(secondResult);
        Assert.Equal(1, db.Locations.Count()); // No new location created

        // Verify response format: { success: true, skipped: true, locationId: null }
        var response = ok.Value;
        var responseType = response!.GetType();
        Assert.True((bool)responseType.GetProperty("success")!.GetValue(response)!);
        Assert.True((bool)responseType.GetProperty("skipped")!.GetValue(response)!);
        Assert.Null(responseType.GetProperty("locationId")!.GetValue(response));
    }

    /// <summary>
    /// Tests that concurrent requests with the same LocalTimestamp result in only
    /// one location being saved (race condition prevention).
    /// </summary>
    [Fact]
    public async Task LogLocation_PreventsDuplicates_UnderConcurrentRequests()
    {
        var db = CreateDbContext();
        var duplicateTimestamp = DateTime.SpecifyKind(new DateTime(2026, 1, 9, 4, 47, 46, 72, DateTimeKind.Utc), DateTimeKind.Utc);

        // Create multiple controllers sharing the same DB context to simulate concurrent requests
        var controller1 = BuildController(db, userId: "u-race");
        var controller2 = BuildControllerForExistingUser(db, "u-race");

        var dto = new GpsLoggerLocationDto
        {
            Latitude = 40.8497,
            Longitude = 25.8693,
            Timestamp = duplicateTimestamp
        };

        // Fire concurrent requests
        var task1 = controller1.LogLocation(dto);
        var task2 = controller2.LogLocation(dto);

        await Task.WhenAll(task1, task2);

        // Only one location should be saved
        var locationCount = db.Locations.Count(l => l.UserId == "u-race");
        Assert.Equal(1, locationCount);

        // Both requests should return success (one saved, one skipped)
        var result1 = Assert.IsType<OkObjectResult>(task1.Result);
        var result2 = Assert.IsType<OkObjectResult>(task2.Result);

        var response1 = result1.Value!.GetType();
        var response2 = result2.Value!.GetType();

        var skipped1 = (bool)response1.GetProperty("skipped")!.GetValue(result1.Value)!;
        var skipped2 = (bool)response2.GetProperty("skipped")!.GetValue(result2.Value)!;

        // Exactly one should be skipped, one should be saved
        Assert.True(skipped1 ^ skipped2, "Expected exactly one request to be skipped and one to be saved");
    }

    private LocationController BuildController(ApplicationDbContext db, bool includeAuth = true, string? userId = null)
    {
        SeedSettingsIfNeeded(db);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var settings = new ApplicationSettingsService(db, cache);
        var user = SeedUserWithToken(db, "tok", userId ?? "u-log");
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

        var httpContext = new DefaultHttpContext();
        if (includeAuth)
        {
            httpContext.Request.Headers["Authorization"] = "Bearer tok";
            httpContext.User = BuildHttpContextWithUser(user.Id).User;
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    /// <summary>
    /// Builds a controller for an existing user (does not create user again).
    /// Used for concurrent request testing where multiple controllers share the same user.
    /// </summary>
    private LocationController BuildControllerForExistingUser(ApplicationDbContext db, string userId)
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

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer tok";
        httpContext.User = BuildHttpContextWithUser(userId).User;

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static ApplicationUser SeedUserWithToken(ApplicationDbContext db, string token, string userId)
    {
        var user = TestDataFixtures.CreateUser(id: userId);
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { Token = token, UserId = user.Id, Name = "test", User = user });
        db.SaveChanges();
        return user;
    }

    private static void SeedSettingsIfNeeded(ApplicationDbContext db)
    {
        if (!db.ApplicationSettings.Any())
        {
            db.ApplicationSettings.Add(new ApplicationSettings { Id = 1 });
            db.SaveChanges();
        }
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
