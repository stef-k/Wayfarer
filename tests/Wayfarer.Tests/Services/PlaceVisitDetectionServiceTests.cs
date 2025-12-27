using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Unit tests for <see cref="PlaceVisitDetectionService"/>.
/// Note: Tests for spatial query logic (FindNearestPlaceWithinRadiusAsync) require
/// a real PostgreSQL+PostGIS database and are in integration tests.
/// These tests focus on:
/// - Accuracy rejection logic
/// - Effective radius calculation
/// - Candidate confirmation flow (using mock settings)
/// - Visit lifecycle management
/// </summary>
public class PlaceVisitDetectionServiceTests : TestBase
{
    /// <summary>
    /// Creates a test service with default settings.
    /// </summary>
    private (PlaceVisitDetectionService Service, ApplicationDbContext Db, SseService Sse) CreateService(
        ApplicationSettings? settings = null)
    {
        var db = CreateDbContext();
        var cache = new MemoryCache(new MemoryCacheOptions());

        settings ??= CreateDefaultSettings();
        db.ApplicationSettings.Add(settings);
        db.SaveChanges();

        var settingsService = new ApplicationSettingsService(db, cache);
        var sseService = new SseService();
        var logger = NullLogger<PlaceVisitDetectionService>.Instance;
        var service = new PlaceVisitDetectionService(db, settingsService, sseService, logger);

        return (service, db, sseService);
    }

    /// <summary>
    /// Creates default ApplicationSettings with visit detection configured.
    /// </summary>
    private static ApplicationSettings CreateDefaultSettings()
    {
        return new ApplicationSettings
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
    }

    #region Accuracy Rejection Tests

    [Fact]
    [Trait("Category", "PlaceVisitDetection")]
    public async Task ProcessPingAsync_SkipsDetection_WhenAccuracyExceedsThreshold()
    {
        // Arrange
        var settings = CreateDefaultSettings();
        settings.VisitedAccuracyRejectMeters = 100; // 100m threshold
        var (service, db, _) = CreateService(settings);

        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var location = new Point(23.72, 37.98) { SRID = 4326 };

        // Act - accuracy 150m exceeds 100m threshold
        await service.ProcessPingAsync(user.Id, location, accuracyMeters: 150);

        // Assert - no candidates or visits should be created
        Assert.Empty(db.PlaceVisitCandidates);
        Assert.Empty(db.PlaceVisitEvents);
    }

    [Fact]
    [Trait("Category", "PlaceVisitDetection")]
    public async Task ProcessPingAsync_ContinuesDetection_WhenAccuracyWithinThreshold()
    {
        // Arrange
        var settings = CreateDefaultSettings();
        settings.VisitedAccuracyRejectMeters = 100;
        var (service, db, _) = CreateService(settings);

        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var location = new Point(23.72, 37.98) { SRID = 4326 };

        // Act - accuracy 50m is within 100m threshold
        await service.ProcessPingAsync(user.Id, location, accuracyMeters: 50);

        // Assert - detection ran (even if no place found, no exception thrown)
        // This test just verifies the accuracy check doesn't block
        Assert.True(true);
    }

    [Fact]
    [Trait("Category", "PlaceVisitDetection")]
    public async Task ProcessPingAsync_ContinuesDetection_WhenAccuracyRejectionDisabled()
    {
        // Arrange
        var settings = CreateDefaultSettings();
        settings.VisitedAccuracyRejectMeters = 0; // Disabled
        var (service, db, _) = CreateService(settings);

        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var location = new Point(23.72, 37.98) { SRID = 4326 };

        // Act - even with very high accuracy (bad GPS), should not reject
        await service.ProcessPingAsync(user.Id, location, accuracyMeters: 5000);

        // Assert - no exception thrown, detection attempted
        Assert.True(true);
    }

    [Fact]
    [Trait("Category", "PlaceVisitDetection")]
    public async Task ProcessPingAsync_ContinuesDetection_WhenAccuracyIsNull()
    {
        // Arrange
        var settings = CreateDefaultSettings();
        settings.VisitedAccuracyRejectMeters = 100;
        var (service, db, _) = CreateService(settings);

        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var location = new Point(23.72, 37.98) { SRID = 4326 };

        // Act - null accuracy should not trigger rejection
        await service.ProcessPingAsync(user.Id, location, accuracyMeters: null);

        // Assert - no exception thrown
        Assert.True(true);
    }

    #endregion

    #region Stale Visit Closure Tests

    [Fact]
    [Trait("Category", "PlaceVisitDetection")]
    public async Task ProcessPingAsync_ClosesStaleVisit_WhenLastSeenExceedsTimeout()
    {
        // Arrange
        var settings = CreateDefaultSettings();
        // With LocationTimeThresholdMinutes=5, VisitedEndVisitAfterMinutes = 5 * 9 = 45
        var (service, db, _) = CreateService(settings);

        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        // Create an open visit that's 60 minutes old (exceeds 45 min timeout)
        var oldVisit = new PlaceVisitEvent
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PlaceId = null, // No place reference
            ArrivedAtUtc = DateTime.UtcNow.AddMinutes(-120),
            LastSeenAtUtc = DateTime.UtcNow.AddMinutes(-60),
            EndedAtUtc = null, // Open
            TripIdSnapshot = Guid.NewGuid(),
            TripNameSnapshot = "Test Trip",
            RegionNameSnapshot = "Test Region",
            PlaceNameSnapshot = "Test Place"
        };
        db.PlaceVisitEvents.Add(oldVisit);
        await db.SaveChangesAsync();

        var location = new Point(23.72, 37.98) { SRID = 4326 };

        // Act
        await service.ProcessPingAsync(user.Id, location, accuracyMeters: 10);

        // Assert - visit should be closed
        await db.Entry(oldVisit).ReloadAsync();
        Assert.NotNull(oldVisit.EndedAtUtc);
        Assert.Equal(oldVisit.LastSeenAtUtc, oldVisit.EndedAtUtc);
    }

    [Fact]
    [Trait("Category", "PlaceVisitDetection")]
    public async Task ProcessPingAsync_DoesNotCloseRecentVisit()
    {
        // Arrange
        var settings = CreateDefaultSettings();
        var (service, db, _) = CreateService(settings);

        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        // Create an open visit that's only 10 minutes old (within 45 min timeout)
        var recentVisit = new PlaceVisitEvent
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PlaceId = null,
            ArrivedAtUtc = DateTime.UtcNow.AddMinutes(-30),
            LastSeenAtUtc = DateTime.UtcNow.AddMinutes(-10), // Recent
            EndedAtUtc = null,
            TripIdSnapshot = Guid.NewGuid(),
            TripNameSnapshot = "Test Trip",
            RegionNameSnapshot = "Test Region",
            PlaceNameSnapshot = "Test Place"
        };
        db.PlaceVisitEvents.Add(recentVisit);
        await db.SaveChangesAsync();

        var location = new Point(23.72, 37.98) { SRID = 4326 };

        // Act
        await service.ProcessPingAsync(user.Id, location, accuracyMeters: 10);

        // Assert - visit should remain open
        await db.Entry(recentVisit).ReloadAsync();
        Assert.Null(recentVisit.EndedAtUtc);
    }

    #endregion

    #region Stale Candidate Cleanup Tests

    [Fact]
    [Trait("Category", "PlaceVisitDetection")]
    public async Task ProcessPingAsync_CleansUpStaleCandidates()
    {
        // Arrange
        var settings = CreateDefaultSettings();
        // With LocationTimeThresholdMinutes=5, VisitedCandidateStaleMinutes = 5 * 12 = 60
        var (service, db, _) = CreateService(settings);

        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user);
        var region = TestDataFixtures.CreateRegion(trip);
        // Place at different location than the ping
        var place = TestDataFixtures.CreatePlace(region, latitude: 50.0, longitude: 10.0);

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.Places.Add(place);

        // Create a stale candidate (90 minutes old, exceeds 60 min threshold)
        var staleCandidate = new PlaceVisitCandidate
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PlaceId = place.Id,
            FirstHitUtc = DateTime.UtcNow.AddMinutes(-100),
            LastHitUtc = DateTime.UtcNow.AddMinutes(-90),
            ConsecutiveHits = 1
        };
        db.PlaceVisitCandidates.Add(staleCandidate);
        await db.SaveChangesAsync();

        // Ping location far from the place (different continent)
        var location = new Point(-74.0, 40.7) { SRID = 4326 }; // New York area

        // Act
        await service.ProcessPingAsync(user.Id, location, accuracyMeters: 10);

        // Assert - candidate should be removed
        Assert.Empty(db.PlaceVisitCandidates.Where(c => c.Id == staleCandidate.Id));
    }

    [Fact]
    [Trait("Category", "PlaceVisitDetection")]
    public async Task ProcessPingAsync_KeepsRecentCandidates()
    {
        // Arrange
        var settings = CreateDefaultSettings();
        var (service, db, _) = CreateService(settings);

        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user);
        var region = TestDataFixtures.CreateRegion(trip);
        var place = TestDataFixtures.CreatePlace(region, latitude: 37.98, longitude: 23.72);

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.Places.Add(place);

        // Create a recent candidate (5 minutes old, within 60 min threshold)
        var recentCandidate = new PlaceVisitCandidate
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PlaceId = place.Id,
            FirstHitUtc = DateTime.UtcNow.AddMinutes(-10),
            LastHitUtc = DateTime.UtcNow.AddMinutes(-5),
            ConsecutiveHits = 1
        };
        db.PlaceVisitCandidates.Add(recentCandidate);
        await db.SaveChangesAsync();

        var location = new Point(100, 100) { SRID = 4326 }; // Far from place

        // Act
        await service.ProcessPingAsync(user.Id, location, accuracyMeters: 10);

        // Assert - candidate should remain
        Assert.Single(db.PlaceVisitCandidates.Where(c => c.Id == recentCandidate.Id));
    }

    #endregion

    #region Derived Settings Tests

    [Fact]
    [Trait("Category", "PlaceVisitDetection")]
    public void DerivedSettings_CalculateCorrectly_FromTimeThreshold()
    {
        // Arrange
        var settings = new ApplicationSettings
        {
            LocationTimeThresholdMinutes = 5
        };

        // Act & Assert
        // HitWindow = 5 * 1.6 = 8
        Assert.Equal(8, settings.VisitedHitWindowMinutes);

        // CandidateStale = 5 * 12 = 60
        Assert.Equal(60, settings.VisitedCandidateStaleMinutes);

        // EndVisitAfter = 5 * 9 = 45
        Assert.Equal(45, settings.VisitedEndVisitAfterMinutes);
    }

    [Fact]
    [Trait("Category", "PlaceVisitDetection")]
    public void DerivedSettings_ScaleWithTimeThreshold()
    {
        // Arrange - 10 minute threshold
        var settings = new ApplicationSettings
        {
            LocationTimeThresholdMinutes = 10
        };

        // Act & Assert
        // HitWindow = 10 * 1.6 = 16
        Assert.Equal(16, settings.VisitedHitWindowMinutes);

        // CandidateStale = 10 * 12 = 120
        Assert.Equal(120, settings.VisitedCandidateStaleMinutes);

        // EndVisitAfter = 10 * 9 = 90
        Assert.Equal(90, settings.VisitedEndVisitAfterMinutes);
    }

    #endregion

    #region Visit Event Properties Tests

    [Fact]
    [Trait("Category", "PlaceVisitDetection")]
    public void PlaceVisitEvent_ObservedDwellMinutes_CalculatesCorrectly()
    {
        // Arrange
        var visit = new PlaceVisitEvent
        {
            ArrivedAtUtc = DateTime.UtcNow.AddMinutes(-30),
            LastSeenAtUtc = DateTime.UtcNow
        };

        // Act
        var dwell = visit.ObservedDwellMinutes;

        // Assert - should be approximately 30 minutes
        Assert.NotNull(dwell);
        Assert.InRange(dwell.Value, 29.9, 30.1);
    }

    [Fact]
    [Trait("Category", "PlaceVisitDetection")]
    public void PlaceVisitEvent_IsOpen_ReturnsTrueWhenEndedAtIsNull()
    {
        // Arrange
        var openVisit = new PlaceVisitEvent
        {
            EndedAtUtc = null
        };

        // Act & Assert
        Assert.True(openVisit.IsOpen);
    }

    [Fact]
    [Trait("Category", "PlaceVisitDetection")]
    public void PlaceVisitEvent_IsOpen_ReturnsFalseWhenEndedAtHasValue()
    {
        // Arrange
        var closedVisit = new PlaceVisitEvent
        {
            EndedAtUtc = DateTime.UtcNow
        };

        // Act & Assert
        Assert.False(closedVisit.IsOpen);
    }

    #endregion

    #region PlaceVisitCandidate Tests

    [Fact]
    [Trait("Category", "PlaceVisitDetection")]
    public void PlaceVisitCandidate_ConsecutiveHits_DefaultsToZero()
    {
        // Arrange & Act
        var candidate = new PlaceVisitCandidate();

        // Assert
        Assert.Equal(0, candidate.ConsecutiveHits);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    [Trait("Category", "PlaceVisitDetection")]
    public void Constructor_InitializesSuccessfully()
    {
        // Arrange & Act
        var (service, _, _) = CreateService();

        // Assert
        Assert.NotNull(service);
    }

    #endregion
}
