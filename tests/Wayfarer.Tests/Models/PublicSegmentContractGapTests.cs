using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Captures the bounded public Segment contract gaps owned by issue 411.</summary>
public sealed class PublicSegmentContractGapTests : TestBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void PublicSegmentDto_ContainsAdditiveWaypointContract()
    {
        var json = JsonSerializer.Serialize(new ApiTripSegmentDto(), JsonOptions);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("waypoints").ValueKind);
        Assert.Equal(JsonValueKind.False, document.RootElement.GetProperty("hasCustomRoute").ValueKind);
    }

    [Fact]
    public void WaypointFallback_ContainsCompleteEffectiveGeoJson()
    {
        var (segment, _, _, _) = CreateJourney(route: null);

        var dto = segment.ToApiDto();

        Assert.Equal(
            "{\"type\":\"LineString\",\"coordinates\":[[23.72,37.98],[23.73,37.99],[23.74,38.0]]}",
            dto.RouteJson);
    }

    [Fact]
    public void PublicProjection_RejectsUnloadedWaypointState()
    {
        var segment = new Segment
        {
            Id = Guid.NewGuid(),
            TripId = Guid.NewGuid(),
            FromPlaceId = Guid.NewGuid(),
            ToPlaceId = Guid.NewGuid()
        };

        Assert.Throws<InvalidOperationException>(() => segment.ToApiDto());
    }

    [Fact]
    public void CurrentPublicTripQuery_DoesNotAuthoritativelyLoadWaypointChildren()
    {
        var db = CreateDbContext();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "owner", Name = "Trip", IsPublic = true };
        var segment = new Segment { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = trip.UserId };
        trip.Segments.Add(segment);
        db.Add(trip);
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var loadedTrip = db.Trips.Include(item => item.Segments).Single(item => item.Id == trip.Id);
        var loadedSegment = Assert.Single(loadedTrip.Segments);

        Assert.True(db.Entry(loadedSegment).Collection(item => item.Waypoints).IsLoaded);
    }

    [Fact]
    public void PublicProjection_RejectsCrossTripWaypointStateWithoutLeakingIdentity()
    {
        var (segment, _, _, waypoint) = CreateJourney(route: null);
        waypoint.Place.Region.TripId = Guid.NewGuid();

        var exception = Assert.Throws<InvalidOperationException>(() => segment.ToApiDto());

        Assert.DoesNotContain(waypoint.PlaceId.ToString(), exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublicProjection_RejectsMalformedWaypointOrder()
    {
        var (segment, _, _, waypoint) = CreateJourney(route: null);
        waypoint.Position = 1;

        Assert.Throws<InvalidOperationException>(() => segment.ToApiDto());
    }

    [Fact]
    public void OlderClient_IgnoresAdditiveFieldsAndPreservesExistingContract()
    {
        var id = Guid.NewGuid();
        var from = Guid.NewGuid();
        var to = Guid.NewGuid();
        var json = $$"""
            {"id":"{{id}}","mode":"walk","estimatedDistanceKm":2.5,"estimatedDurationMinutes":30,
             "notes":"Via B","displayOrder":4,"fromPlaceId":"{{from}}","toPlaceId":"{{to}}",
             "routeJson":"{\"type\":\"LineString\",\"coordinates\":[[1,2],[3,4],[5,6]]}",
             "waypoints":[{"placeId":"{{Guid.NewGuid()}}","position":0,"routeVertexIndex":1}],"hasCustomRoute":true}
            """;

        var older = JsonSerializer.Deserialize<OlderClientSegment>(json, JsonOptions);

        Assert.NotNull(older);
        Assert.Equal(id, older.Id);
        Assert.Equal(from, older.FromPlaceId);
        Assert.Equal(to, older.ToPlaceId);
        Assert.Equal("walk", older.Mode);
        Assert.Equal(2.5, older.EstimatedDistanceKm);
        Assert.Equal(30, older.EstimatedDurationMinutes);
        Assert.Equal("Via B", older.Notes);
        Assert.Equal(4, older.DisplayOrder);
        Assert.Contains("LineString", older.RouteJson);
    }

    private static (Segment Segment, Place From, Place To, SegmentWaypoint Waypoint) CreateJourney(LineString? route)
    {
        var tripId = Guid.NewGuid();
        var region = new Region { Id = Guid.NewGuid(), TripId = tripId };
        var from = PlaceAt(region, 23.72, 37.98);
        var via = PlaceAt(region, 23.73, 37.99);
        var to = PlaceAt(region, 23.74, 38.0);
        var segment = new Segment
        {
            Id = Guid.NewGuid(),
            TripId = tripId,
            FromPlaceId = from.Id,
            FromPlace = from,
            ToPlaceId = to.Id,
            ToPlace = to,
            RouteGeometry = route
        };
        var waypoint = new SegmentWaypoint
        {
            Segment = segment,
            SegmentId = segment.Id,
            Place = via,
            PlaceId = via.Id,
            Position = 0
        };
        segment.Waypoints.Add(waypoint);
        return (segment, from, to, waypoint);
    }

    private static Place PlaceAt(Region region, double longitude, double latitude) => new()
    {
        Id = Guid.NewGuid(),
        RegionId = region.Id,
        Region = region,
        Location = new Point(longitude, latitude) { SRID = 4326 }
    };

    private sealed class OlderClientSegment
    {
        public Guid Id { get; set; }
        public string? Mode { get; set; }
        public double? EstimatedDistanceKm { get; set; }
        public double? EstimatedDurationMinutes { get; set; }
        public string? Notes { get; set; }
        public int DisplayOrder { get; set; }
        public Guid? FromPlaceId { get; set; }
        public Guid? ToPlaceId { get; set; }
        public string? RouteJson { get; set; }
    }
}
