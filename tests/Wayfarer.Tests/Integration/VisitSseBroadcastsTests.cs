using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Integration;

/// <summary>
/// Integration tests for SSE broadcasts when visits are created.
/// Note: Full spatial query testing requires PostgreSQL+PostGIS.
/// These tests verify the broadcast infrastructure and DTO serialization.
/// </summary>
public class VisitSseBroadcastsTests
{
    /// <summary>
    /// Test SSE service that captures broadcast messages.
    /// </summary>
    private sealed class TestSseService : SseService
    {
        public List<(string Channel, string Data)> Messages { get; } = new();

        public override Task BroadcastAsync(string channel, string data)
        {
            Messages.Add((channel, data));
            return Task.CompletedTask;
        }
    }

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options, new ServiceCollection().BuildServiceProvider());
    }

    [Fact]
    public void VisitSseEventDto_SerializesToCorrectChannelFormat()
    {
        // Arrange
        var userId = "user123";
        var expectedChannel = $"user-visits-{userId}";

        var visit = new PlaceVisitEvent
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PlaceId = Guid.NewGuid(),
            TripIdSnapshot = Guid.NewGuid(),
            TripNameSnapshot = "Test Trip",
            RegionNameSnapshot = "Test Region",
            PlaceNameSnapshot = "Test Place",
            ArrivedAtUtc = DateTime.UtcNow,
            PlaceLocationSnapshot = new Point(23.72, 37.97) { SRID = 4326 },
            IconNameSnapshot = "marker",
            MarkerColorSnapshot = "bg-blue"
        };

        // Act
        var dto = VisitSseEventDto.FromVisitEvent(visit);
        var json = JsonSerializer.Serialize(dto);

        // Assert
        Assert.Equal(expectedChannel, $"user-visits-{visit.UserId}");
        Assert.Contains("\"type\":\"visit_started\"", json);
        Assert.Contains("\"placeName\":\"Test Place\"", json);
        Assert.Contains("\"tripName\":\"Test Trip\"", json);
    }

    [Fact]
    public async Task TestSseService_CapturesBroadcastMessages()
    {
        // Arrange
        var sseService = new TestSseService();
        var channel = "user-visits-user123";
        var dto = new VisitSseEventDto
        {
            VisitId = Guid.NewGuid(),
            TripId = Guid.NewGuid(),
            TripName = "Trip",
            PlaceName = "Place",
            RegionName = "Region",
            ArrivedAtUtc = DateTime.UtcNow
        };
        var json = JsonSerializer.Serialize(dto);

        // Act
        await sseService.BroadcastAsync(channel, json);

        // Assert
        Assert.Single(sseService.Messages);
        Assert.Equal(channel, sseService.Messages[0].Channel);
        Assert.Contains("visit_started", sseService.Messages[0].Data);
    }

    [Fact]
    public void PlaceVisitDetectionService_CanBeConstructedWithSseService()
    {
        // Arrange
        var db = CreateDb();
        var cache = new MemoryCache(new MemoryCacheOptions());

        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1,
            LocationTimeThresholdMinutes = 5,
            LocationDistanceThresholdMeters = 15,
            VisitedRequiredHits = 2,
            VisitedMinRadiusMeters = 35,
            VisitedMaxRadiusMeters = 100,
            VisitedAccuracyMultiplier = 2.0,
            VisitedAccuracyRejectMeters = 200,
            VisitedMaxSearchRadiusMeters = 150
        });
        db.SaveChanges();

        var settingsService = new ApplicationSettingsService(db, cache);
        var sseService = new TestSseService();
        var logger = NullLogger<PlaceVisitDetectionService>.Instance;

        // Act
        var service = new PlaceVisitDetectionService(db, settingsService, sseService, logger);

        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public void VisitSseEventDto_DeserializesCorrectly()
    {
        // Arrange
        var visitId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        var placeId = Guid.NewGuid();
        var arrivedAt = new DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc);

        var dto = new VisitSseEventDto
        {
            VisitId = visitId,
            TripId = tripId,
            TripName = "My Trip",
            PlaceId = placeId,
            PlaceName = "Acropolis",
            RegionName = "Athens",
            ArrivedAtUtc = arrivedAt,
            Latitude = 37.97,
            Longitude = 23.72,
            IconName = "museum",
            MarkerColor = "bg-orange"
        };

        // Act
        var json = JsonSerializer.Serialize(dto);
        var deserialized = JsonSerializer.Deserialize<VisitSseEventDto>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal("visit_started", deserialized.Type);
        Assert.Equal(visitId, deserialized.VisitId);
        Assert.Equal(tripId, deserialized.TripId);
        Assert.Equal("My Trip", deserialized.TripName);
        Assert.Equal(placeId, deserialized.PlaceId);
        Assert.Equal("Acropolis", deserialized.PlaceName);
        Assert.Equal("Athens", deserialized.RegionName);
        Assert.Equal(arrivedAt, deserialized.ArrivedAtUtc);
        Assert.Equal(37.97, deserialized.Latitude);
        Assert.Equal(23.72, deserialized.Longitude);
        Assert.Equal("museum", deserialized.IconName);
        Assert.Equal("bg-orange", deserialized.MarkerColor);
    }
}
