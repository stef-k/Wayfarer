using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Changes lifecycle dependency identities and roles while production row locks are awaited.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class PlaceRegionLifecycleDependencyDriftPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Retries discovery when a new Segment dependency is committed during the Place lock wait.</summary>
    [PostgresFact]
    public async Task DependencyCreationDuringLockWait_RetriesAndReconcilesNewSegment()
    {
        var seeded = await SeedAsync(InitialRole.None);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await LockPlaceAsync(blocker, seeded.MovingPlaceId);
        await using var lifecycle = fixture.CreateContext();
        await lifecycle.Database.OpenConnectionAsync();
        var lifecyclePid = await BackendPidAsync(lifecycle);
        var operation = Service(lifecycle).UpdatePlaceAsync(
            seeded.TripId, seeded.MovingPlaceId, seeded.UserId,
            Update(seeded.RegionId), CancellationToken.None);
        await WaitUntilBlockedAsync(lifecyclePid);

        blocker.Set<SegmentWaypoint>().Add(new SegmentWaypoint
        {
            SegmentId = seeded.SegmentId,
            PlaceId = seeded.MovingPlaceId,
            Position = 0,
            RouteVertexIndex = 1
        });
        var lockedPlace = await blocker.Places.SingleAsync(item => item.Id == seeded.MovingPlaceId);
        lockedPlace.Notes = "Dependency created during lock wait";
        await blocker.SaveChangesAsync();
        await transaction.CommitAsync();

        var result = await operation;

        Assert.True(result.Succeeded);
        Assert.Equal([seeded.SegmentId], result.Segments.Select(item => item.Id));
        await AssertRoleAndGeometryAsync(seeded, expectedEndpoint: false, expectedWaypoint: true, 1);
    }

    /// <summary>Uses post-lock canonical discovery when a dependency is removed during the Segment wait.</summary>
    [PostgresFact]
    public async Task DependencyRemovalDuringLockWait_DoesNotRewriteFormerSegment()
    {
        var seeded = await SeedAsync(InitialRole.Waypoint);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await SegmentRouteReconciler.LockSegmentAsync(blocker, seeded.SegmentId, CancellationToken.None);
        await using var lifecycle = fixture.CreateContext();
        await lifecycle.Database.OpenConnectionAsync();
        var lifecyclePid = await BackendPidAsync(lifecycle);
        var operation = Service(lifecycle).UpdatePlaceAsync(
            seeded.TripId, seeded.MovingPlaceId, seeded.UserId,
            Update(seeded.RegionId), CancellationToken.None);
        await WaitUntilBlockedAsync(lifecyclePid);

        await blocker.Set<SegmentWaypoint>().Where(item => item.SegmentId == seeded.SegmentId)
            .ExecuteDeleteAsync();
        await transaction.CommitAsync();

        var result = await operation;

        Assert.True(result.Succeeded);
        Assert.Empty(result.Segments);
        await AssertRoleAndGeometryAsync(seeded, expectedEndpoint: false, expectedWaypoint: false, 1);
    }

    /// <summary>Recomputes the canonical role when a waypoint becomes an endpoint during the lock wait.</summary>
    [PostgresFact]
    public async Task WaypointToEndpointRoleDrift_RewritesOnlyCanonicalEndpoint()
    {
        var seeded = await SeedAsync(InitialRole.Waypoint);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await SegmentRouteReconciler.LockSegmentAsync(blocker, seeded.SegmentId, CancellationToken.None);
        await using var lifecycle = fixture.CreateContext();
        await lifecycle.Database.OpenConnectionAsync();
        var lifecyclePid = await BackendPidAsync(lifecycle);
        var operation = Service(lifecycle).UpdatePlaceAsync(
            seeded.TripId, seeded.MovingPlaceId, seeded.UserId,
            Update(seeded.RegionId), CancellationToken.None);
        await WaitUntilBlockedAsync(lifecyclePid);

        await blocker.Set<SegmentWaypoint>().Where(item => item.SegmentId == seeded.SegmentId)
            .ExecuteDeleteAsync();
        var segment = await blocker.Segments.SingleAsync(item => item.Id == seeded.SegmentId);
        segment.FromPlaceId = seeded.MovingPlaceId;
        segment.RouteGeometry = Line((2, 2), (2.5, 2.5), (3, 3));
        await blocker.SaveChangesAsync();
        await transaction.CommitAsync();

        var result = await operation;

        Assert.True(result.Succeeded);
        await AssertRoleAndGeometryAsync(seeded, expectedEndpoint: true, expectedWaypoint: false, 0);
    }

    /// <summary>Recomputes the canonical role when an endpoint becomes a waypoint during the lock wait.</summary>
    [PostgresFact]
    public async Task EndpointToWaypointRoleDrift_RewritesOnlyCanonicalWaypointVertex()
    {
        var seeded = await SeedAsync(InitialRole.Endpoint);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await SegmentRouteReconciler.LockSegmentAsync(blocker, seeded.SegmentId, CancellationToken.None);
        await using var lifecycle = fixture.CreateContext();
        await lifecycle.Database.OpenConnectionAsync();
        var lifecyclePid = await BackendPidAsync(lifecycle);
        var operation = Service(lifecycle).UpdatePlaceAsync(
            seeded.TripId, seeded.MovingPlaceId, seeded.UserId,
            Update(seeded.RegionId), CancellationToken.None);
        await WaitUntilBlockedAsync(lifecyclePid);

        var segment = await blocker.Segments.SingleAsync(item => item.Id == seeded.SegmentId);
        segment.FromPlaceId = seeded.FromPlaceId;
        segment.RouteGeometry = Line((1, 1), (2, 2), (3, 3));
        blocker.Set<SegmentWaypoint>().Add(new SegmentWaypoint
        {
            SegmentId = seeded.SegmentId,
            PlaceId = seeded.MovingPlaceId,
            Position = 0,
            RouteVertexIndex = 1
        });
        await blocker.SaveChangesAsync();
        await transaction.CommitAsync();

        var result = await operation;

        Assert.True(result.Succeeded);
        await AssertRoleAndGeometryAsync(seeded, expectedEndpoint: false, expectedWaypoint: true, 1);
    }

    private async Task<DriftSeed> SeedAsync(InitialRole role)
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Dependency drift" };
        fixture.RegisterTrip(trip.Id);
        var region = new Region
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id,
            Name = "Region", DisplayOrder = 1
        };
        trip.Regions.Add(region);
        var from = Place(region, user.Id, "From", 1, 1);
        var moving = Place(region, user.Id, "Moving", 2, 2);
        var to = Place(region, user.Id, "To", 3, 3);
        var segment = new Segment
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id,
            FromPlaceId = role == InitialRole.Endpoint ? moving.Id : from.Id,
            ToPlaceId = to.Id,
            DisplayOrder = 1,
            RouteGeometry = role == InitialRole.Endpoint
                ? Line((2, 2), (2.5, 2.5), (3, 3))
                : Line((1, 1), (2, 2), (3, 3))
        };
        if (role == InitialRole.Waypoint)
        {
            segment.Waypoints.Add(new SegmentWaypoint
            {
                Segment = segment, SegmentId = segment.Id, Place = moving, PlaceId = moving.Id,
                Position = 0, RouteVertexIndex = 1
            });
        }
        trip.Segments.Add(segment);
        await using var context = fixture.CreateContext();
        context.Trips.Add(trip);
        await context.SaveChangesAsync();
        return new(user.Id, trip.Id, region.Id, moving.Id, from.Id, segment.Id);
    }

    private async Task AssertRoleAndGeometryAsync(
        DriftSeed seeded,
        bool expectedEndpoint,
        bool expectedWaypoint,
        int rewrittenIndex)
    {
        await using var context = fixture.CreateContext();
        var segment = await context.Segments.Include(item => item.Waypoints)
            .SingleAsync(item => item.Id == seeded.SegmentId);
        Assert.Equal(expectedEndpoint, segment.FromPlaceId == seeded.MovingPlaceId);
        Assert.Equal(expectedWaypoint, segment.Waypoints.Any(item => item.PlaceId == seeded.MovingPlaceId));
        if (expectedEndpoint || expectedWaypoint)
            Assert.Equal(new Coordinate(8, 8), segment.RouteGeometry!.Coordinates[rewrittenIndex]);
        else
            Assert.Equal(new Coordinate(2, 2), segment.RouteGeometry!.Coordinates[rewrittenIndex]);
    }

    private async Task WaitUntilBlockedAsync(int backendPid)
    {
        await using var context = fixture.CreateContext();
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var count = await context.Database.SqlQueryRaw<int>(
                "SELECT cardinality(pg_blocking_pids({0})) AS \"Value\"", backendPid).SingleAsync();
            if (count > 0) return;
            await Task.Yield();
        }
        throw new TimeoutException("Lifecycle discovery did not reach the expected PostgreSQL lock wait.");
    }

    private static Task LockPlaceAsync(ApplicationDbContext context, Guid placeId) =>
        context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM public.\"Places\" WHERE \"Id\" = {placeId} FOR UPDATE");

    private static Task<int> BackendPidAsync(ApplicationDbContext context) =>
        context.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();

    private static PlaceLifecycleUpdate Update(Guid regionId) =>
        new(regionId, "Moved", "", "", "marker", "bg-blue", new Point(8, 8) { SRID = 4326 });

    private static PlaceRegionLifecycleService Service(ApplicationDbContext context) =>
        new(context, new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider()));

    private static Place Place(Region region, string userId, string name, double x, double y)
    {
        var place = new Place
        {
            Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = userId,
            Name = name, DisplayOrder = region.Places.Count + 1,
            Location = new Point(x, y) { SRID = 4326 }
        };
        region.Places.Add(place);
        return place;
    }

    private static LineString Line(params (double X, double Y)[] coordinates) =>
        new(coordinates.Select(item => new Coordinate(item.X, item.Y)).ToArray()) { SRID = 4326 };

    private enum InitialRole { None, Waypoint, Endpoint }

    private sealed record DriftSeed(
        string UserId,
        Guid TripId,
        Guid RegionId,
        Guid MovingPlaceId,
        Guid FromPlaceId,
        Guid SegmentId);
}
