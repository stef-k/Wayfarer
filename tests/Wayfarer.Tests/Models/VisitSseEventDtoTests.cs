using System.Text.Json;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>
/// Tests for <see cref="VisitSseEventDto"/>.
/// </summary>
public class VisitSseEventDtoTests
{
    [Fact]
    public void FromVisitEvent_MapsAllProperties()
    {
        // Arrange
        var visitId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        var placeId = Guid.NewGuid();
        var arrivedAt = DateTime.UtcNow;

        var visit = new PlaceVisitEvent
        {
            Id = visitId,
            UserId = "user1",
            PlaceId = placeId,
            TripIdSnapshot = tripId,
            TripNameSnapshot = "My Trip",
            RegionNameSnapshot = "Athens",
            PlaceNameSnapshot = "Acropolis",
            ArrivedAtUtc = arrivedAt,
            PlaceLocationSnapshot = new Point(23.72, 37.97) { SRID = 4326 },
            IconNameSnapshot = "museum",
            MarkerColorSnapshot = "bg-orange"
        };

        // Act
        var dto = VisitSseEventDto.FromVisitEvent(visit);

        // Assert
        Assert.Equal("visit_started", dto.Type);
        Assert.Equal(visitId, dto.VisitId);
        Assert.Equal(tripId, dto.TripId);
        Assert.Equal("My Trip", dto.TripName);
        Assert.Equal(placeId, dto.PlaceId);
        Assert.Equal("Acropolis", dto.PlaceName);
        Assert.Equal("Athens", dto.RegionName);
        Assert.Equal(arrivedAt, dto.ArrivedAtUtc);
        Assert.Equal(37.97, dto.Latitude);
        Assert.Equal(23.72, dto.Longitude);
        Assert.Equal("museum", dto.IconName);
        Assert.Equal("bg-orange", dto.MarkerColor);
    }

    [Fact]
    public void FromVisitEvent_HandlesNullLocation()
    {
        // Arrange
        var visit = new PlaceVisitEvent
        {
            Id = Guid.NewGuid(),
            UserId = "user1",
            PlaceId = Guid.NewGuid(),
            TripIdSnapshot = Guid.NewGuid(),
            TripNameSnapshot = "Trip",
            RegionNameSnapshot = "Region",
            PlaceNameSnapshot = "Place",
            ArrivedAtUtc = DateTime.UtcNow,
            PlaceLocationSnapshot = null
        };

        // Act
        var dto = VisitSseEventDto.FromVisitEvent(visit);

        // Assert
        Assert.Null(dto.Latitude);
        Assert.Null(dto.Longitude);
    }

    [Fact]
    public void FromVisitEvent_HandlesNullOptionalFields()
    {
        // Arrange
        var visit = new PlaceVisitEvent
        {
            Id = Guid.NewGuid(),
            UserId = "user1",
            PlaceId = Guid.NewGuid(),
            TripIdSnapshot = Guid.NewGuid(),
            TripNameSnapshot = null!,
            RegionNameSnapshot = null!,
            PlaceNameSnapshot = null!,
            ArrivedAtUtc = DateTime.UtcNow,
            IconNameSnapshot = null,
            MarkerColorSnapshot = null
        };

        // Act
        var dto = VisitSseEventDto.FromVisitEvent(visit);

        // Assert
        Assert.Equal(string.Empty, dto.TripName);
        Assert.Equal(string.Empty, dto.RegionName);
        Assert.Equal(string.Empty, dto.PlaceName);
        Assert.Null(dto.IconName);
        Assert.Null(dto.MarkerColor);
    }

    [Fact]
    public void Serialization_OmitsNullFields()
    {
        // Arrange
        var dto = new VisitSseEventDto
        {
            VisitId = Guid.NewGuid(),
            TripId = Guid.NewGuid(),
            TripName = "Trip",
            PlaceName = "Place",
            RegionName = "Region",
            ArrivedAtUtc = DateTime.UtcNow,
            // Leave nullable fields null
            PlaceId = null,
            Latitude = null,
            Longitude = null,
            IconName = null,
            MarkerColor = null
        };

        // Act
        var json = JsonSerializer.Serialize(dto);

        // Assert
        Assert.DoesNotContain("placeId", json);
        Assert.DoesNotContain("latitude", json);
        Assert.DoesNotContain("longitude", json);
        Assert.DoesNotContain("iconName", json);
        Assert.DoesNotContain("markerColor", json);
    }

    [Fact]
    public void Serialization_IncludesAllFieldsWhenPresent()
    {
        // Arrange
        var dto = new VisitSseEventDto
        {
            VisitId = Guid.NewGuid(),
            TripId = Guid.NewGuid(),
            TripName = "Trip",
            PlaceId = Guid.NewGuid(),
            PlaceName = "Place",
            RegionName = "Region",
            ArrivedAtUtc = DateTime.UtcNow,
            Latitude = 37.97,
            Longitude = 23.72,
            IconName = "marker",
            MarkerColor = "bg-blue"
        };

        // Act
        var json = JsonSerializer.Serialize(dto);

        // Assert
        Assert.Contains("\"type\":\"visit_started\"", json);
        Assert.Contains("\"visitId\"", json);
        Assert.Contains("\"tripId\"", json);
        Assert.Contains("\"tripName\":\"Trip\"", json);
        Assert.Contains("\"placeId\"", json);
        Assert.Contains("\"placeName\":\"Place\"", json);
        Assert.Contains("\"regionName\":\"Region\"", json);
        Assert.Contains("\"latitude\":37.97", json);
        Assert.Contains("\"longitude\":23.72", json);
        Assert.Contains("\"iconName\":\"marker\"", json);
        Assert.Contains("\"markerColor\":\"bg-blue\"", json);
    }
}
