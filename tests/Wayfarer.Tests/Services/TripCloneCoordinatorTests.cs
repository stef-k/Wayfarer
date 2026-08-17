using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.ViewModels;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves the shared clone coordinator's canonical mapping and failure boundaries.</summary>
public sealed class TripCloneCoordinatorTests : TestBase
{
    /// <summary>Composes persisted A-B-C and A-B-A state through projection, clone, and native interchange.</summary>
    [Fact]
    public async Task CloneAsync_PreservesCanonicalWaypointAggregateStates()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var destination = TestDataFixtures.CreateUser(id: "destination");
        db.Users.AddRange(owner, destination);
        var source = TestDataFixtures.CreateTrip(owner, "Canonical clone", isPublic: true);
        var region = RegionWithPlaces(source, owner.Id, out var places);
        var customGeometry = Line((0, 0), (0.5, 0.25), (1, 1), (2, 2));
        var profile = await db.Set<TransportProfile>().SingleAsync(item => item.Key == "walk");
        profile.IsActive = false;
        var manualDuration = TimeSpan.FromSeconds(541);
        var automatic = Segment(source, owner.Id, places[0], places[2], places[1], customGeometry, 2);
        automatic.TransportProfileId = profile.Id;
        automatic.EstimatedDurationSource = EstimatedDurationSource.Automatic;
        automatic.EstimatedDuration = TimeSpan.FromDays(1);
        source.Regions = [region];
        source.Segments =
        [
            automatic,
            Segment(source, owner.Id, places[0], places[0], places[1], null, null, manualDuration)
        ];
        db.Trips.Add(source);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var persisted = await LoadTripAggregateAsync(db, source.Id);
        var persistedCustom = persisted.Segments.Single(item => item.RouteGeometry != null);
        var persistedLoop = persisted.Segments.Single(item => item.RouteGeometry == null);
        AssertComposition(persistedCustom, expectedCustom: true, expectedTrail: "A → B → C", expectedRoutePoints: 4, db);
        AssertComposition(persistedLoop, expectedCustom: false, expectedTrail: "A → B → A", expectedRoutePoints: 3, db);

        var result = await new TripCloneCoordinator(db).CloneAsync(source.Id, destination.Id);

        Assert.Equal(TripCloneStatus.Succeeded, result.Status);
        db.ChangeTracker.Clear();
        var clone = await LoadTripAggregateAsync(db, result.ClonedTripId!.Value);
        var clonedPlaces = clone.Regions.SelectMany(item => item.Places).ToDictionary(item => item.Name);
        var custom = clone.Segments.Single(item => item.RouteGeometry != null);
        var loop = clone.Segments.Single(item => item.RouteGeometry == null);
        Assert.Equal((clonedPlaces["A"].Id, clonedPlaces["B"].Id, clonedPlaces["C"].Id),
            (custom.FromPlaceId, Assert.Single(custom.Waypoints).PlaceId, custom.ToPlaceId));
        Assert.Equal(2, Assert.Single(custom.Waypoints).RouteVertexIndex);
        Assert.NotSame(customGeometry, custom.RouteGeometry);
        Assert.Equal(clonedPlaces["A"].Id, loop.FromPlaceId);
        Assert.Equal(clonedPlaces["A"].Id, loop.ToPlaceId);
        Assert.Equal(clonedPlaces["B"].Id, Assert.Single(loop.Waypoints).PlaceId);
        Assert.Null(Assert.Single(loop.Waypoints).RouteVertexIndex);
        Assert.NotEqual(TimeSpan.FromDays(1), custom.EstimatedDuration);
        Assert.Equal(EstimatedDurationSource.Automatic, custom.EstimatedDurationSource);
        Assert.Equal(manualDuration, loop.EstimatedDuration);
        Assert.Equal(EstimatedDurationSource.Manual, loop.EstimatedDurationSource);
        Assert.NotNull(custom.EstimatedDistanceKm);
        Assert.NotNull(loop.EstimatedDistanceKm);
        Assert.Equal(3, clonedPlaces.Count);
        AssertComposition(custom, expectedCustom: true, expectedTrail: "A → B → C", expectedRoutePoints: 4, db);
        AssertComposition(loop, expectedCustom: false, expectedTrail: "A → B → A", expectedRoutePoints: 3, db);

        var kml = TripWayfarerKmlExporter.BuildKml(clone);
        var importedId = await new TripImportService(db, NullLogger<TripImportService>.Instance)
            .ImportWayfarerKmlAsync(Stream(kml), destination.Id, TripImportMode.CreateNew);
        db.ChangeTracker.Clear();
        var imported = await LoadTripAggregateAsync(db, importedId);
        var importedPlaces = imported.Regions.SelectMany(item => item.Places).ToDictionary(item => item.Name);
        var importedCustom = imported.Segments.Single(item => item.RouteGeometry != null);
        var importedLoop = imported.Segments.Single(item => item.RouteGeometry == null);
        Assert.Equal(3, importedPlaces.Count);
        Assert.Equal((importedPlaces["A"].Id, importedPlaces["B"].Id, importedPlaces["C"].Id),
            (importedCustom.FromPlaceId, Assert.Single(importedCustom.Waypoints).PlaceId, importedCustom.ToPlaceId));
        Assert.Equal(importedPlaces["A"].Id, importedLoop.FromPlaceId);
        Assert.Equal(importedPlaces["A"].Id, importedLoop.ToPlaceId);
        Assert.Equal(importedPlaces["B"].Id, Assert.Single(importedLoop.Waypoints).PlaceId);
        Assert.Equal("walk", importedCustom.Mode);
        Assert.Equal("walk", importedCustom.TransportProfile!.Key);
        Assert.NotNull(importedCustom.EstimatedDistanceKm);
        Assert.Equal(EstimatedDurationSource.Automatic, importedCustom.EstimatedDurationSource);
        Assert.Equal(EstimatedDurationSource.Manual, importedLoop.EstimatedDurationSource);
        Assert.Equal(manualDuration, importedLoop.EstimatedDuration);
        AssertComposition(importedCustom, expectedCustom: true, expectedTrail: "A → B → C", expectedRoutePoints: 4, db);
        AssertComposition(importedLoop, expectedCustom: false, expectedTrail: "A → B → A", expectedRoutePoints: 3, db);
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
        Trip trip, string userId, Place from, Place to, Place waypoint, LineString? geometry, int? index,
        TimeSpan? duration = null) => new()
    {
        Id = Guid.NewGuid(), TripId = trip.Id, UserId = userId, FromPlaceId = from.Id, ToPlaceId = to.Id,
        Mode = "walk", RouteGeometry = geometry, EstimatedDuration = duration ?? TimeSpan.FromMinutes(9),
        EstimatedDurationSource = EstimatedDurationSource.Manual,
        Waypoints = [new SegmentWaypoint { PlaceId = waypoint.Id, Position = 0, RouteVertexIndex = index }]
    };

    /// <summary>Creates an SRID 4326 route from coordinate tuples.</summary>
    private static LineString Line(params (double X, double Y)[] coordinates) =>
        new(coordinates.Select(item => new Coordinate(item.X, item.Y)).ToArray()) { SRID = 4326 };

    /// <summary>Loads every relationship required by viewer, public, clone, and native-export authorities.</summary>
    private static Task<Trip> LoadTripAggregateAsync(ApplicationDbContext db, Guid tripId) =>
        db.Trips
            .Include(trip => trip.Regions).ThenInclude(region => region.Places)
            .Include(trip => trip.Segments).ThenInclude(segment => segment.FromPlace).ThenInclude(place => place!.Region)
            .Include(trip => trip.Segments).ThenInclude(segment => segment.ToPlace).ThenInclude(place => place!.Region)
            .Include(trip => trip.Segments).ThenInclude(segment => segment.Waypoints.OrderBy(item => item.Position))
                .ThenInclude(waypoint => waypoint.Place).ThenInclude(place => place.Region)
            .Include(trip => trip.Segments).ThenInclude(segment => segment.TransportProfile)
            .SingleAsync(trip => trip.Id == tripId);

    /// <summary>Checks direct viewer and public projections against one fully loaded persisted Segment.</summary>
    private static void AssertComposition(
        Segment segment,
        bool expectedCustom,
        string expectedTrail,
        int expectedRoutePoints,
        ApplicationDbContext db)
    {
        var viewer = ViewerSegmentJourneyResolver.Resolve(segment, segment.TripId, waypointsLoaded: true);
        var projected = PublicSegmentResolver.Resolve(segment, segment.TripId, db);

        Assert.Equal(expectedTrail, viewer.TrailText);
        Assert.Equal(expectedRoutePoints, viewer.RoutePointCount);
        Assert.Equal(segment.FromPlaceId, viewer.Anchors[0].PlaceId);
        Assert.Equal(segment.ToPlaceId, viewer.Anchors[^1].PlaceId);
        Assert.Equal("Start", viewer.Anchors[0].Role);
        Assert.Equal("Via 1", viewer.Anchors[1].Role);
        Assert.Equal("End", viewer.Anchors[^1].Role);
        Assert.True(projected.Succeeded);
        Assert.Equal(expectedCustom, projected.Segment!.HasCustomRoute);
        Assert.Equal(segment.FromPlaceId, projected.Segment.FromPlaceId);
        Assert.Equal(segment.ToPlaceId, projected.Segment.ToPlaceId);
        Assert.Equal(Assert.Single(segment.Waypoints).PlaceId, Assert.Single(projected.Segment.Waypoints).PlaceId);
        Assert.Equal(0, Assert.Single(projected.Segment.Waypoints).Position);
        Assert.Equal(Assert.Single(segment.Waypoints).RouteVertexIndex,
            Assert.Single(projected.Segment.Waypoints).RouteVertexIndex);
        using var route = JsonDocument.Parse(projected.Segment.RouteJson!);
        var coordinates = route.RootElement.GetProperty("coordinates");
        Assert.Equal(expectedRoutePoints, coordinates.GetArrayLength());
        Assert.Equal(1, coordinates[expectedCustom ? 2 : 1][0].GetDouble());
        if (expectedCustom) Assert.Equal(0.5, coordinates[1][0].GetDouble());
    }

    /// <summary>Creates a readable stream for native KML import.</summary>
    private static MemoryStream Stream(string value) => new(Encoding.UTF8.GetBytes(value));
}
