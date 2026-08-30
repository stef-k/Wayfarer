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
    public void PublicSegmentDto_ProjectsCurrentTransportProfileIdentity()
    {
        var currentProfileId = Guid.NewGuid();
        var retainedProfileId = Guid.NewGuid();
        var db = CreateDbContext();
        var segment = LoadJourney(db, route: null);
        segment.TransportProfileId = currentProfileId;
        segment.RouteTransportProfileId = retainedProfileId;

        var resolution = PublicSegmentResolver.Resolve(segment, segment.TripId, db);

        Assert.Equal(currentProfileId, resolution.Segment!.TransportProfileId);
        Assert.Equal(retainedProfileId, resolution.Segment.RouteTransportProfileId);
    }

    [Fact]
    public void PublicSegmentDto_ContainsAdditiveWaypointContract()
    {
        var json = JsonSerializer.Serialize(new ApiTripSegmentDto(), JsonOptions);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("waypoints").ValueKind);
        Assert.Equal(JsonValueKind.False, document.RootElement.GetProperty("hasCustomRoute").ValueKind);
    }

    [Fact]
    public void PublicSegmentDto_PreservesExactFieldOrderTypesAndWaypointAllowlist()
    {
        var dto = new ApiTripSegmentDto
        {
            Id = Guid.NewGuid(),
            Mode = "walk",
            EstimatedDistanceKm = null,
            EstimatedDurationMinutes = null,
            Notes = null,
            DisplayOrder = 2,
            FromPlaceId = null,
            ToPlaceId = null,
            RouteJson = null,
            Waypoints = [new ApiTripSegmentWaypointDto { PlaceId = Guid.NewGuid(), Position = 0, RouteVertexIndex = null }],
            HasCustomRoute = false
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(dto, JsonOptions));
        Assert.Equal(
            ["id", "mode", "estimatedDistanceKm", "estimatedDurationMinutes", "notes", "displayOrder", "transportProfileId",
                "fromPlaceId", "toPlaceId", "routeJson", "waypoints", "hasCustomRoute",
                "routeInstructionsJson", "routeProvider", "routeProviderConfigurationId",
                "routeProviderConfigurationVersion", "routeTransportProfileId", "routeGeneratedAt",
                "routeAttribution", "routeStorageMode"],
            document.RootElement.EnumerateObject().Select(item => item.Name));
        var waypoint = document.RootElement.GetProperty("waypoints")[0];
        Assert.Equal(["placeId", "position", "routeVertexIndex"], waypoint.EnumerateObject().Select(item => item.Name));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("routeJson").ValueKind);
        Assert.Equal(JsonValueKind.Null, waypoint.GetProperty("routeVertexIndex").ValueKind);
    }

    [Fact]
    public void WaypointFallback_ContainsCompleteEffectiveGeoJson()
    {
        var db = CreateDbContext();
        var segment = LoadJourney(db, route: null);

        var resolution = PublicSegmentResolver.Resolve(segment, segment.TripId, db);

        Assert.True(resolution.Succeeded);
        using var route = JsonDocument.Parse(resolution.Segment!.RouteJson!);
        var coordinates = route.RootElement.GetProperty("coordinates");
        Assert.Equal(3, coordinates.GetArrayLength());
        Assert.Equal(23.73, coordinates[1][0].GetDouble());
        Assert.False(resolution.Segment.HasCustomRoute);
        Assert.Null(Assert.Single(resolution.Segment.Waypoints).RouteVertexIndex);
    }

    [Fact]
    public void CustomWaypointRoute_PreservesCompleteGeometryAndIndex()
    {
        var db = CreateDbContext();
        var geometry = new LineString(
        [
            new Coordinate(23.72, 37.98),
            new Coordinate(23.73, 37.99),
            new Coordinate(23.74, 38.0)
        ]) { SRID = 4326 };
        var segment = LoadJourney(db, geometry, routeVertexIndex: 1);
        var originalRoute = segment.RouteGeometry!.Copy();

        var resolution = PublicSegmentResolver.Resolve(segment, segment.TripId, db);

        Assert.True(resolution.Succeeded);
        Assert.True(resolution.Segment!.HasCustomRoute);
        Assert.Equal(1, Assert.Single(resolution.Segment.Waypoints).RouteVertexIndex);
        Assert.Contains("LineString", resolution.Segment.RouteJson);
        Assert.True(segment.RouteGeometry.EqualsExact(originalRoute));
        Assert.Equal(4326, segment.RouteGeometry.SRID);
    }

    [Fact]
    public void CustomWaypointRoute_RejectsContradictoryWaypointIndex()
    {
        var db = CreateDbContext();
        var geometry = new LineString(
        [
            new Coordinate(23.72, 37.98),
            new Coordinate(23.73, 37.99),
            new Coordinate(23.74, 38.0)
        ]) { SRID = 4326 };
        var segment = LoadJourney(db, geometry, routeVertexIndex: null);

        var resolution = PublicSegmentResolver.Resolve(segment, segment.TripId, db);

        Assert.Null(resolution.Segment);
        Assert.Equal(PublicSegmentFailure.MalformedState, resolution.Failure);
    }

    [Fact]
    public void LegacyFallback_RemainsNullWithEmptyWaypoints()
    {
        var db = CreateDbContext();
        var segment = LoadJourney(db, route: null, includeWaypoint: false);

        var resolution = PublicSegmentResolver.Resolve(segment, segment.TripId, db);

        Assert.True(resolution.Succeeded);
        Assert.Empty(resolution.Segment!.Waypoints);
        Assert.False(resolution.Segment.HasCustomRoute);
        Assert.Null(resolution.Segment.RouteJson);
    }

    [Fact]
    public void ClosedLoopFallback_UsesOneCanonicalEndpointIdentity()
    {
        var db = CreateDbContext();
        var segment = LoadJourney(db, route: null, closedLoop: true);

        var resolution = PublicSegmentResolver.Resolve(segment, segment.TripId, db);

        Assert.True(resolution.Succeeded);
        Assert.Equal(resolution.Segment!.FromPlaceId, resolution.Segment.ToPlaceId);
        using var route = JsonDocument.Parse(resolution.Segment.RouteJson!);
        var coordinates = route.RootElement.GetProperty("coordinates");
        Assert.Equal(coordinates[0][0].GetDouble(), coordinates[2][0].GetDouble());
        Assert.Equal(coordinates[0][1].GetDouble(), coordinates[2][1].GetDouble());
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

        var db = CreateDbContext();

        var resolution = PublicSegmentResolver.Resolve(segment, segment.TripId, db);

        Assert.Null(resolution.Segment);
        Assert.Equal(PublicSegmentFailure.UnloadedOrMissingState, resolution.Failure);
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

        var loadedTrip = db.Trips
            .Include(item => item.Segments).ThenInclude(item => item.Waypoints)
            .Single(item => item.Id == trip.Id);
        var loadedSegment = Assert.Single(loadedTrip.Segments);

        Assert.True(db.Entry(loadedSegment).Collection(item => item.Waypoints).IsLoaded);
    }

    [Fact]
    public void PublicProjection_RejectsCrossTripWaypointStateWithoutLeakingIdentity()
    {
        var db = CreateDbContext();
        var segment = LoadJourney(db, route: null);
        var waypoint = Assert.Single(segment.Waypoints);
        waypoint.Place.Region.TripId = Guid.NewGuid();

        var resolution = PublicSegmentResolver.Resolve(segment, segment.TripId, db);

        Assert.Null(resolution.Segment);
        Assert.Equal(PublicSegmentFailure.ForeignState, resolution.Failure);
    }

    [Fact]
    public void PublicProjection_RejectsMalformedWaypointOrder()
    {
        var db = CreateDbContext();
        var segment = LoadJourney(db, route: null);
        var waypoint = Assert.Single(segment.Waypoints);
        waypoint.Position = 1;

        var resolution = PublicSegmentResolver.Resolve(segment, segment.TripId, db);

        Assert.Null(resolution.Segment);
        Assert.Equal(PublicSegmentFailure.MalformedState, resolution.Failure);
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

    private static Segment LoadJourney(
        ApplicationDbContext db,
        LineString? route,
        int? routeVertexIndex = null,
        bool includeWaypoint = true,
        bool closedLoop = false)
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
            ToPlaceId = closedLoop ? from.Id : to.Id,
            ToPlace = closedLoop ? from : to,
            RouteGeometry = route
        };
        var waypoint = new SegmentWaypoint
        {
            Segment = segment,
            SegmentId = segment.Id,
            Place = via,
            PlaceId = via.Id,
            Position = 0,
            RouteVertexIndex = routeVertexIndex
        };
        if (includeWaypoint) segment.Waypoints.Add(waypoint);
        var trip = new Trip { Id = tripId, UserId = "owner", Name = "Trip", IsPublic = true };
        region.Trip = trip;
        region.Places.Add(from);
        region.Places.Add(via);
        region.Places.Add(to);
        trip.Regions.Add(region);
        trip.Segments.Add(segment);
        segment.Trip = trip;
        db.Add(trip);
        db.SaveChanges();
        db.ChangeTracker.Clear();

        return db.Segments
            .Include(item => item.FromPlace).ThenInclude(item => item!.Region)
            .Include(item => item.ToPlace).ThenInclude(item => item!.Region)
            .Include(item => item.Waypoints).ThenInclude(item => item.Place).ThenInclude(item => item.Region)
            .Single(item => item.Id == segment.Id);
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
