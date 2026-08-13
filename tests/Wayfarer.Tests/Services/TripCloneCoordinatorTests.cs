using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves the shared clone coordinator's canonical mapping and failure boundaries.</summary>
public sealed class TripCloneCoordinatorTests : TestBase
{
    /// <summary>Preserves custom/fallback/closed-loop state while remapping every semantic Place identity.</summary>
    [Fact]
    public async Task CloneAsync_PreservesCanonicalWaypointAggregateStates()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var destination = TestDataFixtures.CreateUser(id: "destination");
        db.Users.AddRange(owner, destination);
        var source = TestDataFixtures.CreateTrip(owner, "Canonical clone", isPublic: true);
        var region = RegionWithPlaces(source, owner.Id, out var places);
        var customGeometry = Line((0, 0), (1, 1), (2, 2));
        var profile = await db.Set<TransportProfile>().SingleAsync(item => item.Key == "walk");
        profile.IsActive = false;
        var automatic = Segment(source, owner.Id, places[0], places[2], places[1], customGeometry, 1);
        automatic.TransportProfileId = profile.Id;
        automatic.EstimatedDurationSource = EstimatedDurationSource.Automatic;
        automatic.EstimatedDuration = TimeSpan.FromDays(1);
        source.Regions = [region];
        source.Segments =
        [
            automatic,
            Segment(source, owner.Id, places[0], places[0], places[1], null, null)
        ];
        db.Trips.Add(source);
        await db.SaveChangesAsync();

        var result = await new TripCloneCoordinator(db).CloneAsync(source.Id, destination.Id);

        Assert.Equal(TripCloneStatus.Succeeded, result.Status);
        var clone = await db.Trips.Include(trip => trip.Regions).ThenInclude(item => item.Places)
            .Include(trip => trip.Segments).ThenInclude(item => item.Waypoints)
            .SingleAsync(trip => trip.Id == result.ClonedTripId);
        var clonedPlaces = clone.Regions.SelectMany(item => item.Places).ToDictionary(item => item.Name);
        var custom = clone.Segments.Single(item => item.RouteGeometry != null);
        var loop = clone.Segments.Single(item => item.RouteGeometry == null);
        Assert.Equal((clonedPlaces["A"].Id, clonedPlaces["B"].Id, clonedPlaces["C"].Id),
            (custom.FromPlaceId, Assert.Single(custom.Waypoints).PlaceId, custom.ToPlaceId));
        Assert.Equal(1, Assert.Single(custom.Waypoints).RouteVertexIndex);
        Assert.NotSame(customGeometry, custom.RouteGeometry);
        Assert.Equal(clonedPlaces["A"].Id, loop.FromPlaceId);
        Assert.Equal(clonedPlaces["A"].Id, loop.ToPlaceId);
        Assert.Equal(clonedPlaces["B"].Id, Assert.Single(loop.Waypoints).PlaceId);
        Assert.Null(Assert.Single(loop.Waypoints).RouteVertexIndex);
        Assert.NotEqual(TimeSpan.FromDays(1), custom.EstimatedDuration);
        Assert.Equal(EstimatedDurationSource.Automatic, custom.EstimatedDurationSource);
        Assert.Equal(TimeSpan.FromMinutes(9), loop.EstimatedDuration);
        Assert.Equal(EstimatedDurationSource.Manual, loop.EstimatedDurationSource);
        Assert.NotNull(custom.EstimatedDistanceKm);
        Assert.NotNull(loop.EstimatedDistanceKm);
    }

    /// <summary>Rejects an endpoint outside the source Trip before any clone residue is added.</summary>
    [Fact]
    public async Task CloneAsync_UnmappableEndpointLeavesNoCloneResidue()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var destination = TestDataFixtures.CreateUser(id: "destination");
        db.Users.AddRange(owner, destination);
        var source = TestDataFixtures.CreateTrip(owner, "Malformed clone", isPublic: true);
        source.Segments.Add(new Segment
        {
            Id = Guid.NewGuid(), TripId = source.Id, UserId = owner.Id,
            FromPlaceId = Guid.NewGuid(), Mode = string.Empty,
            EstimatedDurationSource = EstimatedDurationSource.Automatic
        });
        db.Trips.Add(source);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new TripCloneCoordinator(db).CloneAsync(source.Id, destination.Id));

        Assert.False(await db.Trips.AnyAsync(trip => trip.UserId == destination.Id));
    }

    /// <summary>Rejects persisted sub-second Manual duration without leaving destination aggregate residue.</summary>
    [Fact]
    public async Task CloneAsync_SubSecondManualDurationLeavesNoCloneResidue()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var destination = TestDataFixtures.CreateUser(id: "destination");
        db.Users.AddRange(owner, destination);
        var source = TestDataFixtures.CreateTrip(owner, "Sub-second Manual clone", isPublic: true);
        var region = RegionWithPlaces(source, owner.Id, out var places);
        source.Regions = [region];
        var segment = Segment(source, owner.Id, places[0], places[2], places[1], null, null);
        segment.EstimatedDuration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond + 4_000_000);
        source.Segments = [segment];
        db.Trips.Add(source);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new TripCloneCoordinator(db).CloneAsync(source.Id, destination.Id));

        Assert.False(await db.Trips.AnyAsync(trip => trip.UserId == destination.Id));
        Assert.False(await db.Regions.AnyAsync(item => item.UserId == destination.Id));
        Assert.False(await db.Places.AnyAsync(item => item.UserId == destination.Id));
        Assert.False(await db.Segments.AnyAsync(item => item.UserId == destination.Id));
        Assert.DoesNotContain(db.ChangeTracker.Entries<SegmentWaypoint>(),
            entry => entry.Entity.Segment.UserId == destination.Id);
    }

    /// <summary>Creates one source Region containing canonical A, B, and C Places.</summary>
    private static Region RegionWithPlaces(Trip trip, string userId, out Place[] places)
    {
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, UserId = userId, Name = "Route" };
        places =
        [
            Place(region, userId, "A", 0, 0),
            Place(region, userId, "B", 1, 1),
            Place(region, userId, "C", 2, 2)
        ];
        region.Places = places;
        return region;
    }

    /// <summary>Creates one located saved Place.</summary>
    private static Place Place(Region region, string userId, string name, double x, double y) => new()
    {
        Id = Guid.NewGuid(), RegionId = region.Id, UserId = userId, Name = name,
        Location = new Point(x, y) { SRID = 4326 }
    };

    /// <summary>Creates one Manual waypoint-bearing source Segment.</summary>
    private static Segment Segment(
        Trip trip, string userId, Place from, Place to, Place waypoint, LineString? geometry, int? index) => new()
    {
        Id = Guid.NewGuid(), TripId = trip.Id, UserId = userId, FromPlaceId = from.Id, ToPlaceId = to.Id,
        Mode = "walk", RouteGeometry = geometry, EstimatedDuration = TimeSpan.FromMinutes(9),
        EstimatedDurationSource = EstimatedDurationSource.Manual,
        Waypoints = [new SegmentWaypoint { PlaceId = waypoint.Id, Position = 0, RouteVertexIndex = index }]
    };

    /// <summary>Creates an SRID 4326 route from coordinate tuples.</summary>
    private static LineString Line(params (double X, double Y)[] coordinates) =>
        new(coordinates.Select(item => new Coordinate(item.X, item.Y)).ToArray()) { SRID = 4326 };
}
