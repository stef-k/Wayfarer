using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
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

namespace Wayfarer.Tests.Integration;

/// <summary>
/// Integration tests for place visit detection through the LocationController.
/// Tests the full flow from log-location API call through visit detection.
/// Note: Spatial queries (PostGIS Distance) don't work in in-memory database,
/// so these tests focus on the non-spatial aspects of the visit detection flow.
/// </summary>
public class PlaceVisitDetectionIntegrationTests : TestBase
{
    /// <summary>
    /// Creates a test context with all required services.
    /// </summary>
    private (LocationController Controller, ApplicationDbContext Db, SseService Sse) CreateTestContext(
        ApplicationSettings? settings = null)
    {
        var db = CreateDbContext();
        var cache = new MemoryCache(new MemoryCacheOptions());

        settings ??= new ApplicationSettings
        {
            Id = 1,
            LocationTimeThresholdMinutes = 5,
            LocationDistanceThresholdMeters = 15,
            VisitedRequiredHits = 2,
            VisitedMinRadiusMeters = 35,
            VisitedMaxRadiusMeters = 100,
            VisitedAccuracyMultiplier = 2.0,
            VisitedAccuracyRejectMeters = 200,
            VisitedMaxSearchRadiusMeters = 150,
            VisitedPlaceNotesSnapshotMaxHtmlChars = 20000
        };
        db.ApplicationSettings.Add(settings);
        db.SaveChanges();

        var settingsService = new ApplicationSettingsService(db, cache);
        var locationService = new LocationService(db);
        var statsService = new LocationStatsService(db);
        var sseService = new SseService();
        var reverseGeocoding = new ReverseGeocodingService(
            new HttpClient(new FakeHttpMessageHandler()),
            NullLogger<BaseApiController>.Instance);
        var visitDetectionService = new PlaceVisitDetectionService(
            db,
            settingsService,
            sseService,
            NullLogger<PlaceVisitDetectionService>.Instance);

        var controller = new LocationController(
            db,
            NullLogger<BaseApiController>.Instance,
            cache,
            settingsService,
            reverseGeocoding,
            locationService,
            sseService,
            statsService,
            locationService,
            visitDetectionService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        return (controller, db, sseService);
    }

    /// <summary>
    /// Seeds a user with API token for authentication.
    /// </summary>
    private async Task<(ApplicationUser User, string Token)> SeedUserWithTokenAsync(ApplicationDbContext db)
    {
        var user = TestDataFixtures.CreateUser();
        var token = TestDataFixtures.CreateApiToken(user, "test-token-visit");

        db.Users.Add(user);
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync();

        return (user, token.Token!);
    }

    /// <summary>
    /// Sets up the controller with authentication.
    /// </summary>
    private void AuthenticateController(LocationController controller, string token)
    {
        controller.ControllerContext.HttpContext!.Request.Headers["Authorization"] = $"Bearer {token}";
    }

    #region LogLocation Integration Tests

    [Fact]
    [Trait("Category", "PlaceVisitDetectionIntegration")]
    public async Task LogLocation_CompletesSuccessfully_WithVisitDetectionEnabled()
    {
        // Arrange
        var (controller, db, _) = CreateTestContext();
        var (user, token) = await SeedUserWithTokenAsync(db);
        AuthenticateController(controller, token);

        var dto = new GpsLoggerLocationDto
        {
            Latitude = 37.98,
            Longitude = 23.72,
            Accuracy = 10,
            Timestamp = DateTime.UtcNow
        };

        // Act
        var result = await controller.LogLocation(dto);

        // Assert - request completes successfully
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    [Trait("Category", "PlaceVisitDetectionIntegration")]
    public async Task LogLocation_CreatesLocation_EvenWhenVisitDetectionFails()
    {
        // Arrange
        var (controller, db, _) = CreateTestContext();
        var (user, token) = await SeedUserWithTokenAsync(db);
        AuthenticateController(controller, token);

        var dto = new GpsLoggerLocationDto
        {
            Latitude = 37.98,
            Longitude = 23.72,
            Accuracy = 10,
            Timestamp = DateTime.UtcNow
        };

        // Act
        await controller.LogLocation(dto);

        // Assert - location is created
        var location = await db.Locations.FirstOrDefaultAsync(l => l.UserId == user.Id);
        Assert.NotNull(location);
    }

    [Fact]
    [Trait("Category", "PlaceVisitDetectionIntegration")]
    public async Task LogLocation_SkipsVisitDetection_WhenAccuracyTooHigh()
    {
        // Arrange
        var settings = new ApplicationSettings
        {
            Id = 1,
            LocationTimeThresholdMinutes = 5,
            LocationDistanceThresholdMeters = 15,
            VisitedAccuracyRejectMeters = 100 // Reject if accuracy > 100m
        };
        var (controller, db, _) = CreateTestContext(settings);
        var (user, token) = await SeedUserWithTokenAsync(db);

        // Create a trip with a place
        var trip = TestDataFixtures.CreateTrip(user);
        var region = TestDataFixtures.CreateRegion(trip);
        var place = TestDataFixtures.CreatePlace(region, latitude: 37.98, longitude: 23.72);
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.Places.Add(place);
        await db.SaveChangesAsync();

        AuthenticateController(controller, token);

        var dto = new GpsLoggerLocationDto
        {
            Latitude = 37.98,
            Longitude = 23.72,
            Accuracy = 150, // Exceeds threshold
            Timestamp = DateTime.UtcNow
        };

        // Act
        await controller.LogLocation(dto);

        // Assert - no candidates created due to accuracy rejection
        Assert.Empty(await db.PlaceVisitCandidates.Where(c => c.UserId == user.Id).ToListAsync());
    }

    #endregion

    #region CheckIn Integration Tests

    [Fact]
    [Trait("Category", "PlaceVisitDetectionIntegration")]
    public async Task CheckIn_CompletesSuccessfully_WithVisitDetectionEnabled()
    {
        // Arrange
        var (controller, db, _) = CreateTestContext();
        var (user, token) = await SeedUserWithTokenAsync(db);
        AuthenticateController(controller, token);

        var dto = new GpsLoggerLocationDto
        {
            Latitude = 37.98,
            Longitude = 23.72,
            Accuracy = 10,
            Timestamp = DateTime.UtcNow
        };

        // Act
        var result = await controller.CheckIn(dto);

        // Assert
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    [Trait("Category", "PlaceVisitDetectionIntegration")]
    public async Task CheckIn_CreatesLocation_WithManualType()
    {
        // Arrange
        var (controller, db, _) = CreateTestContext();
        var (user, token) = await SeedUserWithTokenAsync(db);
        AuthenticateController(controller, token);

        var dto = new GpsLoggerLocationDto
        {
            Latitude = 37.98,
            Longitude = 23.72,
            Accuracy = 10,
            Timestamp = DateTime.UtcNow
        };

        // Act
        await controller.CheckIn(dto);

        // Assert
        var location = await db.Locations.FirstOrDefaultAsync(l => l.UserId == user.Id);
        Assert.NotNull(location);
        Assert.Equal("Manual", location.LocationType);
    }

    #endregion

    #region Visit Event Lifecycle Tests

    [Fact]
    [Trait("Category", "PlaceVisitDetectionIntegration")]
    public async Task LogLocation_ClosesStaleVisits_OnEachPing()
    {
        // Arrange
        var (controller, db, _) = CreateTestContext();
        var (user, token) = await SeedUserWithTokenAsync(db);

        // Create an old open visit
        var staleVisit = new PlaceVisitEvent
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PlaceId = null,
            ArrivedAtUtc = DateTime.UtcNow.AddHours(-3),
            LastSeenAtUtc = DateTime.UtcNow.AddHours(-2), // 2 hours old
            EndedAtUtc = null,
            TripIdSnapshot = Guid.NewGuid(),
            TripNameSnapshot = "Old Trip",
            RegionNameSnapshot = "Old Region",
            PlaceNameSnapshot = "Old Place"
        };
        db.PlaceVisitEvents.Add(staleVisit);
        await db.SaveChangesAsync();

        AuthenticateController(controller, token);

        var dto = new GpsLoggerLocationDto
        {
            Latitude = 37.98,
            Longitude = 23.72,
            Accuracy = 10,
            Timestamp = DateTime.UtcNow
        };

        // Act
        await controller.LogLocation(dto);

        // Assert - stale visit should be closed
        await db.Entry(staleVisit).ReloadAsync();
        Assert.NotNull(staleVisit.EndedAtUtc);
    }

    [Fact]
    [Trait("Category", "PlaceVisitDetectionIntegration")]
    public async Task LogLocation_RemovesStaleCandidates_OnEachPing()
    {
        // Arrange
        var (controller, db, _) = CreateTestContext();
        var (user, token) = await SeedUserWithTokenAsync(db);

        // Create a trip and place
        var trip = TestDataFixtures.CreateTrip(user);
        var region = TestDataFixtures.CreateRegion(trip);
        var place = TestDataFixtures.CreatePlace(region, latitude: 50.0, longitude: 10.0);
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.Places.Add(place);

        // Create a stale candidate
        var staleCandidate = new PlaceVisitCandidate
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PlaceId = place.Id,
            FirstHitUtc = DateTime.UtcNow.AddHours(-3),
            LastHitUtc = DateTime.UtcNow.AddHours(-2), // 2 hours old
            ConsecutiveHits = 1
        };
        db.PlaceVisitCandidates.Add(staleCandidate);
        await db.SaveChangesAsync();

        AuthenticateController(controller, token);

        var dto = new GpsLoggerLocationDto
        {
            Latitude = -74.0, // Far from the place
            Longitude = 40.7,
            Accuracy = 10,
            Timestamp = DateTime.UtcNow
        };

        // Act
        await controller.LogLocation(dto);

        // Assert - stale candidate should be removed
        Assert.Empty(await db.PlaceVisitCandidates.Where(c => c.Id == staleCandidate.Id).ToListAsync());
    }

    #endregion

    #region Settings Validation Tests

    [Fact]
    [Trait("Category", "PlaceVisitDetectionIntegration")]
    public async Task LogLocation_RespectsCustomSettings()
    {
        // Arrange - use custom settings with higher thresholds
        var settings = new ApplicationSettings
        {
            Id = 1,
            LocationTimeThresholdMinutes = 10,
            LocationDistanceThresholdMeters = 30,
            VisitedRequiredHits = 3, // Higher requirement
            VisitedMinRadiusMeters = 50,
            VisitedMaxRadiusMeters = 200,
            VisitedAccuracyMultiplier = 1.5,
            VisitedAccuracyRejectMeters = 300,
            VisitedMaxSearchRadiusMeters = 250,
            VisitedPlaceNotesSnapshotMaxHtmlChars = 50000
        };
        var (controller, db, _) = CreateTestContext(settings);
        var (user, token) = await SeedUserWithTokenAsync(db);
        AuthenticateController(controller, token);

        var dto = new GpsLoggerLocationDto
        {
            Latitude = 37.98,
            Longitude = 23.72,
            Accuracy = 250, // Within 300m threshold
            Timestamp = DateTime.UtcNow
        };

        // Act
        var result = await controller.LogLocation(dto);

        // Assert - completes without error
        Assert.IsType<OkObjectResult>(result);
    }

    #endregion

    /// <summary>
    /// Fake HTTP handler for reverse geocoding mock.
    /// </summary>
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"features\":[]}")
            });
        }
    }
}
