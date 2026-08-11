using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Executes the distinct Place movement and location-clearing transitions required by LIFE.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class PlaceRegionLifecycleTransitionPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Moves each custom anchor role without disturbing other vertices or waypoint indices.</summary>
    [PostgresTheory]
    [InlineData(AnchorRole.From)]
    [InlineData(AnchorRole.To)]
    [InlineData(AnchorRole.FirstWaypoint)]
    [InlineData(AnchorRole.MiddleWaypoint)]
    [InlineData(AnchorRole.LastWaypoint)]
    [InlineData(AnchorRole.ClosedLoop)]
    public async Task CustomMovementChangesOnlyReferencedVertices(AnchorRole role)
    {
        var seeded = await SeedCustomAsync(role, waypointBearing: true);
        await using var context = fixture.CreateContext();

        var result = await Service(context).UpdatePlaceAsync(
            seeded.TripId, seeded.MovingPlaceId, seeded.UserId,
            Update(seeded.SourceRegionId, Point(8, 8)), CancellationToken.None);

        Assert.True(result.Succeeded);
        await using var verification = fixture.CreateContext();
        var segment = await verification.Segments.AsNoTracking().Include(item => item.Waypoints)
            .SingleAsync(item => item.Id == seeded.SegmentId);
        var expected = seeded.OriginalCoordinates.Select(item => item.Copy()).ToArray();
        foreach (var index in seeded.MovingVertexIndices) expected[index] = new Coordinate(8, 8);
        Assert.Equal(expected, segment.RouteGeometry!.Coordinates);
        Assert.Equal(seeded.RouteVertexIndices, segment.Waypoints.OrderBy(item => item.Position).Select(item => item.RouteVertexIndex));
        Assert.NotNull(segment.EstimatedDistanceKm);
        Assert.NotNull(segment.EstimatedDuration);
    }

    /// <summary>Leaves fallback storage null while effective anchors follow a moved Place.</summary>
    [PostgresTheory]
    [InlineData(AnchorRole.From)]
    [InlineData(AnchorRole.To)]
    [InlineData(AnchorRole.MiddleWaypoint)]
    public async Task FallbackMovementKeepsGeometryAndIndicesNull(AnchorRole role)
    {
        var seeded = await SeedFallbackAsync(role);
        await using var context = fixture.CreateContext();

        var result = await Service(context).UpdatePlaceAsync(
            seeded.TripId, seeded.MovingPlaceId, seeded.UserId,
            Update(seeded.SourceRegionId, Point(8, 8)), CancellationToken.None);

        Assert.True(result.Succeeded);
        await using var verification = fixture.CreateContext();
        var segment = await verification.Segments.AsNoTracking().Include(item => item.Waypoints)
            .SingleAsync(item => item.Id == seeded.SegmentId);
        Assert.Null(segment.RouteGeometry);
        Assert.All(segment.Waypoints, item => Assert.Null(item.RouteVertexIndex));
        Assert.NotNull(segment.EstimatedDistanceKm);
    }

    /// <summary>Rejects location clearing for every role when any affected Segment bears waypoints.</summary>
    [PostgresTheory]
    [InlineData(AnchorRole.From)]
    [InlineData(AnchorRole.To)]
    [InlineData(AnchorRole.MiddleWaypoint)]
    [InlineData(AnchorRole.ClosedLoop)]
    public async Task WaypointBearingLocationClearRejectsWithoutPartialState(AnchorRole role)
    {
        var seeded = await SeedCustomAsync(role, waypointBearing: true);
        await using var context = fixture.CreateContext();

        var result = await Service(context).UpdatePlaceAsync(
            seeded.TripId, seeded.MovingPlaceId, seeded.UserId,
            Update(seeded.TargetRegionId, null), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("waypoint-location-required", result.ErrorCode);
        await using var verification = fixture.CreateContext();
        var place = await verification.Places.AsNoTracking().SingleAsync(item => item.Id == seeded.MovingPlaceId);
        var segment = await verification.Segments.AsNoTracking().SingleAsync(item => item.Id == seeded.SegmentId);
        Assert.Equal(seeded.SourceRegionId, place.RegionId);
        Assert.NotNull(place.Location);
        Assert.Equal(seeded.OriginalCoordinates, segment.RouteGeometry!.Coordinates);
    }

    /// <summary>Preserves zero-waypoint compatibility for each endpoint shape and duration authority.</summary>
    [PostgresTheory]
    [InlineData(AnchorRole.From, EstimatedDurationSource.Automatic)]
    [InlineData(AnchorRole.To, EstimatedDurationSource.Automatic)]
    [InlineData(AnchorRole.ClosedLoop, EstimatedDurationSource.Automatic)]
    [InlineData(AnchorRole.From, EstimatedDurationSource.Manual)]
    [InlineData(AnchorRole.To, EstimatedDurationSource.Manual)]
    [InlineData(AnchorRole.ClosedLoop, EstimatedDurationSource.Manual)]
    public async Task ZeroWaypointLocationClearPreservesCompatibility(
        AnchorRole role,
        EstimatedDurationSource durationSource)
    {
        var seeded = await SeedCustomAsync(role, waypointBearing: false, durationSource);
        await using var context = fixture.CreateContext();

        var result = await Service(context).UpdatePlaceAsync(
            seeded.TripId, seeded.MovingPlaceId, seeded.UserId,
            Update(seeded.SourceRegionId, null), CancellationToken.None);

        Assert.True(result.Succeeded);
        await using var verification = fixture.CreateContext();
        var place = await verification.Places.AsNoTracking().SingleAsync(item => item.Id == seeded.MovingPlaceId);
        var segment = await verification.Segments.AsNoTracking().SingleAsync(item => item.Id == seeded.SegmentId);
        Assert.Null(place.Location);
        Assert.Null(segment.RouteGeometry);
        Assert.Null(segment.EstimatedDistanceKm);
        if (durationSource == EstimatedDurationSource.Automatic) Assert.Null(segment.EstimatedDuration);
        else Assert.Equal(TimeSpan.FromMinutes(37), segment.EstimatedDuration);
        Assert.Equal(durationSource, segment.EstimatedDurationSource);
    }

    /// <summary>Preserves null fallback storage and Manual authority for zero-waypoint location clearing.</summary>
    [PostgresTheory]
    [InlineData(EstimatedDurationSource.Automatic)]
    [InlineData(EstimatedDurationSource.Manual)]
    public async Task ZeroWaypointFallbackLocationClearPreservesCompatibility(
        EstimatedDurationSource durationSource)
    {
        var seeded = await SeedCustomAsync(
            AnchorRole.From, waypointBearing: false, durationSource);
        await using (var setup = fixture.CreateContext())
        {
            var segment = await setup.Segments.SingleAsync(item => item.Id == seeded.SegmentId);
            segment.RouteGeometry = null;
            await setup.SaveChangesAsync();
        }
        await using var context = fixture.CreateContext();

        var result = await Service(context).UpdatePlaceAsync(
            seeded.TripId, seeded.MovingPlaceId, seeded.UserId,
            Update(seeded.SourceRegionId, null), CancellationToken.None);

        Assert.True(result.Succeeded);
        await using var verification = fixture.CreateContext();
        var segmentAfter = await verification.Segments.AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SegmentId);
        Assert.Null(segmentAfter.RouteGeometry);
        Assert.Null(segmentAfter.EstimatedDistanceKm);
        Assert.Equal(durationSource, segmentAfter.EstimatedDurationSource);
        Assert.Equal(
            durationSource == EstimatedDurationSource.Manual ? TimeSpan.FromMinutes(37) : null,
            segmentAfter.EstimatedDuration);
    }

    /// <summary>Rejects an atomic clear when mixed dependencies contain any waypoint-bearing Segment.</summary>
    [PostgresFact]
    public async Task MixedZeroWaypointAndWaypointBearingLocationClearRejectsEveryDependency()
    {
        var seeded = await SeedCustomAsync(AnchorRole.From, waypointBearing: true);
        Guid zeroWaypointSegmentId;
        await using (var setup = fixture.CreateContext())
        {
            var trip = await setup.Trips.SingleAsync(item => item.Id == seeded.TripId);
            var toId = await setup.Places.Where(item => item.RegionId == seeded.SourceRegionId && item.Id != seeded.MovingPlaceId)
                .Select(item => item.Id).FirstAsync();
            var segment = new Segment
            {
                Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = seeded.UserId,
                FromPlaceId = seeded.MovingPlaceId, ToPlaceId = toId, DisplayOrder = 2,
                RouteGeometry = new LineString([new(1, 1), new(1.5, 1.5), new(2, 2)]) { SRID = 4326 }
            };
            zeroWaypointSegmentId = segment.Id;
            setup.Segments.Add(segment);
            await setup.SaveChangesAsync();
        }
        await using var context = fixture.CreateContext();

        var result = await Service(context).UpdatePlaceAsync(
            seeded.TripId, seeded.MovingPlaceId, seeded.UserId,
            Update(seeded.TargetRegionId, null), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("waypoint-location-required", result.ErrorCode);
        await using var verification = fixture.CreateContext();
        Assert.NotNull(await verification.Places.Where(item => item.Id == seeded.MovingPlaceId)
            .Select(item => item.Location).SingleAsync());
        Assert.NotNull(await verification.Segments.Where(item => item.Id == seeded.SegmentId)
            .Select(item => item.RouteGeometry).SingleAsync());
        Assert.NotNull(await verification.Segments.Where(item => item.Id == zeroWaypointSegmentId)
            .Select(item => item.RouteGeometry).SingleAsync());
    }

    /// <summary>Moves Region membership alone without changing route or measurement state.</summary>
    [PostgresFact]
    public async Task RegionOnlyMovementPreservesGeometryAndMeasurements()
    {
        var seeded = await SeedCustomAsync(AnchorRole.MiddleWaypoint, waypointBearing: true);
        await using var beforeContext = fixture.CreateContext();
        var before = await beforeContext.Segments.AsNoTracking().SingleAsync(item => item.Id == seeded.SegmentId);
        var geometry = before.RouteGeometry!.Copy();
        var distance = before.EstimatedDistanceKm;
        var duration = before.EstimatedDuration;
        await using var context = fixture.CreateContext();

        var result = await Service(context).UpdatePlaceAsync(
            seeded.TripId, seeded.MovingPlaceId, seeded.UserId,
            Update(seeded.TargetRegionId, Point(3, 3)), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.LocationChanged);
        await using var verification = fixture.CreateContext();
        var place = await verification.Places.AsNoTracking().SingleAsync(item => item.Id == seeded.MovingPlaceId);
        var segment = await verification.Segments.AsNoTracking().SingleAsync(item => item.Id == seeded.SegmentId);
        Assert.Equal(seeded.TargetRegionId, place.RegionId);
        Assert.True(geometry.EqualsExact(segment.RouteGeometry));
        Assert.Equal(distance, segment.EstimatedDistanceKm);
        Assert.Equal(duration, segment.EstimatedDuration);
    }

    private async Task<TransitionSeed> SeedCustomAsync(
        AnchorRole role,
        bool waypointBearing,
        EstimatedDurationSource durationSource = EstimatedDurationSource.Automatic)
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Lifecycle transition" };
        fixture.RegisterTrip(trip.Id);
        var source = Region(trip, user.Id, "Source", 1);
        var target = Region(trip, user.Id, "Target", 2);
        var from = Place(source, user.Id, "From", Point(1, 1));
        var first = Place(source, user.Id, "First", Point(2, 2));
        var middle = Place(source, user.Id, "Middle", Point(3, 3));
        var last = Place(source, user.Id, "Last", Point(4, 4));
        var to = role == AnchorRole.ClosedLoop ? from : Place(source, user.Id, "To", Point(5, 5));
        var moving = role switch
        {
            AnchorRole.From or AnchorRole.ClosedLoop => from,
            AnchorRole.To => to,
            AnchorRole.FirstWaypoint => first,
            AnchorRole.MiddleWaypoint => middle,
            AnchorRole.LastWaypoint => last,
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
        var coordinates = new[] { new Coordinate(1, 1), new(1.5, 1.5), new(2, 2), new(2.5, 2.5), new(3, 3), new(3.5, 3.5), new(4, 4), new(4.5, 4.5), to.Location!.Coordinate.Copy() };
        var segment = Segment(trip, from, to, new LineString(coordinates) { SRID = 4326 }, durationSource);
        if (waypointBearing)
        {
            AddWaypoint(segment, first, 0, 2);
            AddWaypoint(segment, middle, 1, 4);
            AddWaypoint(segment, last, 2, 6);
        }
        await SaveAsync(trip);
        var movingIndices = role switch
        {
            AnchorRole.From => new[] { 0 }, AnchorRole.To => new[] { 8 },
            AnchorRole.FirstWaypoint => new[] { 2 }, AnchorRole.MiddleWaypoint => new[] { 4 },
            AnchorRole.LastWaypoint => new[] { 6 }, AnchorRole.ClosedLoop => new[] { 0, 8 }, _ => []
        };
        return new(user.Id, trip.Id, source.Id, target.Id, moving.Id, segment.Id,
            coordinates, movingIndices, segment.Waypoints.OrderBy(item => item.Position).Select(item => item.RouteVertexIndex).ToArray());
    }

    private async Task<TransitionSeed> SeedFallbackAsync(AnchorRole role)
    {
        var seeded = await SeedCustomAsync(role, waypointBearing: true);
        await using var context = fixture.CreateContext();
        var segment = await context.Segments.Include(item => item.Waypoints).SingleAsync(item => item.Id == seeded.SegmentId);
        segment.RouteGeometry = null;
        foreach (var waypoint in segment.Waypoints) waypoint.RouteVertexIndex = null;
        await context.SaveChangesAsync();
        return seeded;
    }

    private async Task SaveAsync(Trip trip)
    {
        await using var context = fixture.CreateContext();
        context.Trips.Add(trip);
        await context.SaveChangesAsync();
    }

    private static Segment Segment(Trip trip, Place from, Place to, LineString geometry, EstimatedDurationSource source)
    {
        var segment = new Segment
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = trip.UserId,
            FromPlaceId = from.Id, ToPlaceId = to.Id, DisplayOrder = 1, RouteGeometry = geometry,
            Mode = "walk", EstimatedDistanceKm = 99,
            EstimatedDuration = source == EstimatedDurationSource.Manual ? TimeSpan.FromMinutes(37) : TimeSpan.FromHours(9),
            EstimatedDurationSource = source
        };
        trip.Segments.Add(segment);
        return segment;
    }

    private static void AddWaypoint(Segment segment, Place place, int position, int routeVertexIndex) =>
        segment.Waypoints.Add(new SegmentWaypoint
        {
            Segment = segment, SegmentId = segment.Id, Place = place, PlaceId = place.Id,
            Position = position, RouteVertexIndex = routeVertexIndex
        });

    private static Region Region(Trip trip, string userId, string name, int order)
    {
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = name, DisplayOrder = order };
        trip.Regions.Add(region);
        return region;
    }

    private static Place Place(Region region, string userId, string name, Point location)
    {
        var place = new Place { Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = userId, Name = name, DisplayOrder = region.Places.Count + 1, Location = location };
        region.Places.Add(place);
        return place;
    }

    private static PlaceLifecycleServiceFactory Factory => new();
    private static PlaceRegionLifecycleService Service(ApplicationDbContext context) => Factory.Create(context);
    private static PlaceLifecycleUpdate Update(Guid regionId, Point? location) => new(regionId, "Moved", "notes", "address", "marker", "bg-blue", location);
    private static Point Point(double x, double y) => new(x, y) { SRID = 4326 };

    public enum AnchorRole { From, To, FirstWaypoint, MiddleWaypoint, LastWaypoint, ClosedLoop }

    private sealed record TransitionSeed(
        string UserId, Guid TripId, Guid SourceRegionId, Guid TargetRegionId, Guid MovingPlaceId,
        Guid SegmentId, Coordinate[] OriginalCoordinates, int[] MovingVertexIndices, int?[] RouteVertexIndices);

    private sealed class PlaceLifecycleServiceFactory
    {
        /// <summary>Creates the production lifecycle boundary with isolated token protection.</summary>
        public PlaceRegionLifecycleService Create(ApplicationDbContext context) =>
            new(context, new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider()));
    }
}
