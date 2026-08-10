using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Executes destructive lifecycle operations while competing writers wait on production row locks.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class PlaceRegionLifecycleDestructiveConcurrencyPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Serializes confirmed Place deletion against direct reconciliation of its surviving Segment.</summary>
    [PostgresFact]
    public async Task PlaceDeletion_VersusSegmentReconciliation_CommitsOneCoherentState()
    {
        var seeded = await SeedAsync();
        var confirmation = new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider());
        var placeToken = await PlaceTokenAsync(seeded, confirmation);
        var gate = new SaveGateInterceptor();
        await using var deletionContext = fixture.CreateContext(gate);
        await using var reconciliationContext = fixture.CreateContext();
        await reconciliationContext.Database.OpenConnectionAsync();
        var competingPid = await BackendPidAsync(reconciliationContext);
        var deletionTask = CaptureAsync(() => Service(deletionContext, confirmation).DeletePlaceAsync(
            seeded.TripId, seeded.WaypointId, seeded.UserId, placeToken, CancellationToken.None));
        await gate.SaveEntered.Task;
        var reconciliationTask = CaptureAsync(async () =>
        {
            var result = await SegmentRouteReconciler.ReconcileAsync(
                reconciliationContext,
                Proposal(seeded),
                CancellationToken.None);
            return result.Succeeded;
        });

        await WaitUntilBlockedAsync(competingPid);
        gate.ReleaseSave.TrySetResult();
        var deletion = await deletionTask;
        var reconciliation = await reconciliationTask;

        Assert.True(deletion.Result!.Succeeded);
        AssertConcurrentOutcome(reconciliation);
        await using var verification = fixture.CreateContext();
        Assert.False(await verification.Places.AnyAsync(item => item.Id == seeded.WaypointId));
        var segment = await verification.Segments.Include(item => item.Waypoints)
            .SingleAsync(item => item.Id == seeded.SegmentId);
        Assert.Empty(segment.Waypoints);
        Assert.Equal(new Coordinate(2, 2), segment.RouteGeometry!.Coordinates[1]);
    }

    /// <summary>Serializes confirmed Region deletion against movement of a child Place.</summary>
    [PostgresFact]
    public async Task RegionDeletion_VersusPlaceMovement_DoesNotPersistIntermediateState()
    {
        var seeded = await SeedAsync();
        var confirmation = new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider());
        var regionToken = await RegionTokenAsync(seeded, confirmation);
        var gate = new SaveGateInterceptor();
        await using var deletionContext = fixture.CreateContext(gate);
        await using var movementContext = fixture.CreateContext();
        await movementContext.Database.OpenConnectionAsync();
        var competingPid = await BackendPidAsync(movementContext);
        var deletionTask = CaptureAsync(() => Service(deletionContext, confirmation).DeleteRegionAsync(
            seeded.TripId, seeded.DeletedRegionId, seeded.UserId, regionToken, CancellationToken.None));
        await gate.SaveEntered.Task;
        var movementTask = CaptureAsync(() => Service(movementContext, confirmation).UpdatePlaceAsync(
            seeded.TripId,
            seeded.WaypointId,
            seeded.UserId,
            new(seeded.OutsideRegionId, "Moved", "", "", "marker", "bg-blue", Point(8, 8)),
            CancellationToken.None));

        await WaitUntilBlockedAsync(competingPid);
        gate.ReleaseSave.TrySetResult();
        var deletion = await deletionTask;
        var movement = await movementTask;

        Assert.True(deletion.Result!.Succeeded);
        AssertConcurrentOutcome(movement);
        await using var verification = fixture.CreateContext();
        Assert.False(await verification.Regions.AnyAsync(item => item.Id == seeded.DeletedRegionId));
        Assert.False(await verification.Places.AnyAsync(item => item.Id == seeded.WaypointId));
        var segment = await verification.Segments.Include(item => item.Waypoints)
            .SingleAsync(item => item.Id == seeded.SegmentId);
        Assert.Empty(segment.Waypoints);
        Assert.Equal(new Coordinate(2, 2), segment.RouteGeometry!.Coordinates[1]);
    }

    /// <summary>Returns a fresh stale token when confirmed Region deletion overlaps Place deletion.</summary>
    [PostgresFact]
    public async Task OverlappingPlaceAndRegionDeletion_ReturnsFreshDestructiveDriftToken()
    {
        var seeded = await SeedAsync();
        var confirmation = new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider());
        var placeToken = await PlaceTokenAsync(seeded, confirmation);
        var regionToken = await RegionTokenAsync(seeded, confirmation);
        var gate = new SaveGateInterceptor();
        await using var placeContext = fixture.CreateContext(gate);
        await using var regionContext = fixture.CreateContext();
        await regionContext.Database.OpenConnectionAsync();
        var competingPid = await BackendPidAsync(regionContext);
        var placeTask = CaptureAsync(() => Service(placeContext, confirmation).DeletePlaceAsync(
            seeded.TripId, seeded.WaypointId, seeded.UserId, placeToken, CancellationToken.None));
        await gate.SaveEntered.Task;
        var regionTask = CaptureAsync(() => Service(regionContext, confirmation).DeleteRegionAsync(
            seeded.TripId, seeded.DeletedRegionId, seeded.UserId, regionToken, CancellationToken.None));

        await WaitUntilBlockedAsync(competingPid);
        gate.ReleaseSave.TrySetResult();
        var place = await placeTask;
        var region = await regionTask;

        Assert.True(place.Result!.Succeeded);
        if (region.Exception == null)
        {
            Assert.False(region.Result!.Succeeded);
            Assert.Equal("lifecycle-confirmation-stale", region.Result.Warning!.Code);
            Assert.False(string.IsNullOrWhiteSpace(region.Result.Warning.ConfirmationToken));
        }
        else
        {
            AssertSerialization(region.Exception);
        }
        await using var verification = fixture.CreateContext();
        Assert.False(await verification.Places.AnyAsync(item => item.Id == seeded.WaypointId));
        Assert.True(await verification.Regions.AnyAsync(item => item.Id == seeded.DeletedRegionId));
    }

    private async Task<DestructiveSeed> SeedAsync()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Destructive concurrency" };
        fixture.RegisterTrip(trip.Id);
        var deletedRegion = Region(trip, user.Id, "Deleted", 1);
        var outsideRegion = Region(trip, user.Id, "Outside", 2);
        var waypoint = Place(deletedRegion, user.Id, "Waypoint", 2, 2);
        var from = Place(outsideRegion, user.Id, "From", 1, 1);
        var to = Place(outsideRegion, user.Id, "To", 3, 3);
        var segment = new Segment
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id,
            FromPlaceId = from.Id, ToPlaceId = to.Id, DisplayOrder = 1,
            RouteGeometry = new LineString([new(1, 1), new(2, 2), new(3, 3)]) { SRID = 4326 }
        };
        segment.Waypoints.Add(new SegmentWaypoint
        {
            Segment = segment, SegmentId = segment.Id, Place = waypoint, PlaceId = waypoint.Id,
            Position = 0, RouteVertexIndex = 1
        });
        trip.Segments.Add(segment);
        await using var context = fixture.CreateContext();
        context.Trips.Add(trip);
        await context.SaveChangesAsync();
        return new(user.Id, trip.Id, deletedRegion.Id, outsideRegion.Id, waypoint.Id, from.Id, to.Id, segment.Id);
    }

    private async Task<string> PlaceTokenAsync(
        DestructiveSeed seeded,
        LifecycleDependencyConfirmation confirmation)
    {
        await using var context = fixture.CreateContext();
        var challenge = await Service(context, confirmation).DeletePlaceAsync(
            seeded.TripId, seeded.WaypointId, seeded.UserId, null, CancellationToken.None);
        return challenge.Warning!.ConfirmationToken;
    }

    private async Task<string> RegionTokenAsync(
        DestructiveSeed seeded,
        LifecycleDependencyConfirmation confirmation)
    {
        await using var context = fixture.CreateContext();
        var challenge = await Service(context, confirmation).DeleteRegionAsync(
            seeded.TripId, seeded.DeletedRegionId, seeded.UserId, null, CancellationToken.None);
        return challenge.Warning!.ConfirmationToken;
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
        throw new TimeoutException("The destructive competitor did not enter a PostgreSQL lock wait.");
    }

    private static void AssertConcurrentOutcome<T>(Captured<T> captured)
    {
        if (captured.Exception == null) return;
        AssertSerialization(captured.Exception);
    }

    private static void AssertSerialization(Exception exception)
    {
        var postgres = exception as PostgresException ?? exception.InnerException as PostgresException;
        Assert.NotNull(postgres);
        Assert.Equal(PostgresErrorCodes.SerializationFailure, postgres!.SqlState);
    }

    private static async Task<Captured<T>> CaptureAsync<T>(Func<Task<T>> operation)
    {
        try { return new(await operation(), null); }
        catch (Exception exception) { return new(default, exception); }
    }

    private static Task<int> BackendPidAsync(ApplicationDbContext context) =>
        context.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();

    private static SegmentRouteProposal Proposal(DestructiveSeed seeded) => new(
        seeded.SegmentId,
        seeded.FromPlaceId,
        seeded.ToPlaceId,
        [new(seeded.WaypointId, 0, 1)],
        new LineString([new(1, 1), new(2, 2), new(3, 3)]) { SRID = 4326 });

    private static PlaceRegionLifecycleService Service(
        ApplicationDbContext context,
        LifecycleDependencyConfirmation confirmation) => new(context, confirmation);

    private static Region Region(Trip trip, string userId, string name, int order)
    {
        var region = new Region
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId,
            Name = name, DisplayOrder = order
        };
        trip.Regions.Add(region);
        return region;
    }

    private static Place Place(Region region, string userId, string name, double x, double y)
    {
        var place = new Place
        {
            Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = userId,
            Name = name, DisplayOrder = region.Places.Count + 1, Location = Point(x, y)
        };
        region.Places.Add(place);
        return place;
    }

    private static Point Point(double x, double y) => new(x, y) { SRID = 4326 };

    private sealed record Captured<T>(T? Result, Exception? Exception);

    private sealed record DestructiveSeed(
        string UserId,
        Guid TripId,
        Guid DeletedRegionId,
        Guid OutsideRegionId,
        Guid WaypointId,
        Guid FromPlaceId,
        Guid ToPlaceId,
        Guid SegmentId);

    private sealed class SaveGateInterceptor : SaveChangesInterceptor
    {
        internal TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            SaveEntered.TrySetResult();
            await ReleaseSave.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}
