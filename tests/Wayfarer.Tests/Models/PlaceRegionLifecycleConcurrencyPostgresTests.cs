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

/// <summary>Executes deterministic lifecycle lock contention through PostgreSQL row waits.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class PlaceRegionLifecycleConcurrencyPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Serializes same-Place and shared-Segment movement with reversed affected Segment insertion.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentPlaceMovement_SerializesSamePlaceAndSharedSegmentSets(bool samePlace)
    {
        var seeded = await SeedAsync();
        var gate = new SaveGateInterceptor();
        await using var firstContext = fixture.CreateContext(gate);
        await using var secondContext = fixture.CreateContext();
        await secondContext.Database.OpenConnectionAsync();
        var secondPid = await BackendPidAsync(secondContext);
        var firstTask = CaptureAsync(() => Service(firstContext).UpdatePlaceAsync(
            seeded.TripId,
            seeded.FirstWaypointId,
            seeded.UserId,
            Update(seeded.RegionId, 8, 8),
            CancellationToken.None));
        await gate.SaveEntered.Task;
        var secondPlaceId = samePlace ? seeded.FirstWaypointId : seeded.SecondWaypointId;
        var secondTask = CaptureAsync(() => Service(secondContext).UpdatePlaceAsync(
            seeded.TripId,
            secondPlaceId,
            seeded.UserId,
            Update(seeded.RegionId, 9, 9),
            CancellationToken.None));

        await WaitUntilBlockedAsync(secondPid);
        gate.ReleaseSave.TrySetResult();
        var first = await firstTask;
        var second = await secondTask;

        Assert.NotNull(first.Result);
        Assert.True(first.Result!.Succeeded);
        AssertConcurrentOutcome(second);
        await using var verification = fixture.CreateContext();
        var firstPlace = await verification.Places.AsNoTracking()
            .SingleAsync(item => item.Id == seeded.FirstWaypointId);
        var secondPlace = await verification.Places.AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SecondWaypointId);
        foreach (var segment in await verification.Segments.AsNoTracking()
                     .Where(item => seeded.SegmentIds.Contains(item.Id)).ToArrayAsync())
        {
            Assert.Equal(firstPlace.Location!.Coordinate, segment.RouteGeometry!.Coordinates[1]);
            Assert.Equal(secondPlace.Location!.Coordinate, segment.RouteGeometry.Coordinates[2]);
        }
    }

    /// <summary>Serializes Place movement against profile-speed and direct Segment reconciliation writers.</summary>
    [PostgresTheory]
    [InlineData(CompetingWriter.ProfileSpeed)]
    [InlineData(CompetingWriter.SegmentReconciliation)]
    public async Task PlaceMovement_SerializesAgainstOtherAggregateWriters(CompetingWriter writer)
    {
        var seeded = await SeedAsync();
        var gate = new SaveGateInterceptor();
        await using var lifecycleContext = fixture.CreateContext(gate);
        await using var competingContext = fixture.CreateContext();
        await competingContext.Database.OpenConnectionAsync();
        var competingPid = await BackendPidAsync(competingContext);
        var lifecycleTask = CaptureAsync(() => Service(lifecycleContext).UpdatePlaceAsync(
            seeded.TripId,
            seeded.FirstWaypointId,
            seeded.UserId,
            Update(seeded.RegionId, 8, 8),
            CancellationToken.None));
        await gate.SaveEntered.Task;

        var competingTask = writer == CompetingWriter.ProfileSpeed
            ? CaptureAsync(async () =>
            {
                var result = await TransportProfileMeasurementReconciler.ReconcileAsync(
                    competingContext, seeded.ProfileId, 7, seeded.UserId, CancellationToken.None);
                return result.Succeeded;
            })
            : CaptureAsync(async () =>
            {
                var result = await SegmentRouteReconciler.ReconcileAsync(
                    competingContext,
                    new(
                        seeded.SegmentIds[0],
                        seeded.FromPlaceId,
                        seeded.ToPlaceId,
                        [
                            new(seeded.FirstWaypointId, 0, 1),
                            new(seeded.SecondWaypointId, 1, 2)
                        ],
                        new LineString([new(1, 1), new(8, 8), new(3, 3), new(4, 4)]) { SRID = 4326 },
                        new(seeded.ProfileKey, seeded.ProfileId, EstimatedDurationSource.Automatic, null)),
                    CancellationToken.None);
                return result.Succeeded;
            });

        await WaitUntilBlockedAsync(competingPid);
        gate.ReleaseSave.TrySetResult();
        var lifecycle = await lifecycleTask;
        var competing = await competingTask;

        Assert.NotNull(lifecycle.Result);
        Assert.True(lifecycle.Result!.Succeeded);
        AssertConcurrentOutcome(competing);
        await using var verification = fixture.CreateContext();
        var moved = await verification.Places.AsNoTracking()
            .SingleAsync(item => item.Id == seeded.FirstWaypointId);
        Assert.Equal(new Coordinate(8, 8), moved.Location!.Coordinate);
        var segment = await verification.Segments.AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SegmentIds[0]);
        Assert.Equal(moved.Location.Coordinate, segment.RouteGeometry!.Coordinates[1]);
        Assert.NotNull(segment.EstimatedDistanceKm);
        Assert.Equal(EstimatedDurationSource.Automatic, segment.EstimatedDurationSource);
    }

    private async Task<ConcurrencySeed> SeedAsync()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var profile = new TransportProfile
        {
            Id = Guid.NewGuid(), Key = $"life-concurrency-{Guid.NewGuid():N}",
            Label = "Lifecycle concurrency", Category = "Test", PlanningSpeedKmh = 5, IsActive = true
        };
        fixture.RegisterTransportProfile(profile.Id);
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Lifecycle concurrency" };
        fixture.RegisterTrip(trip.Id);
        var region = new Region
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id,
            Name = "Region", DisplayOrder = 1
        };
        trip.Regions.Add(region);
        var from = Place(region, user.Id, "From", 1, 1);
        var first = Place(region, user.Id, "First", 2, 2);
        var second = Place(region, user.Id, "Second", 3, 3);
        var to = Place(region, user.Id, "To", 4, 4);
        var segmentIds = new[] { Guid.NewGuid(), Guid.NewGuid() }.Order().ToArray();
        foreach (var segmentId in segmentIds.Reverse())
        {
            var segment = new Segment
            {
                Id = segmentId, Trip = trip, TripId = trip.Id, UserId = user.Id,
                FromPlaceId = from.Id, ToPlaceId = to.Id, DisplayOrder = trip.Segments.Count + 1,
                Mode = profile.Key, TransportProfile = profile, TransportProfileId = profile.Id,
                EstimatedDurationSource = EstimatedDurationSource.Automatic,
                RouteGeometry = new LineString([new(1, 1), new(2, 2), new(3, 3), new(4, 4)]) { SRID = 4326 }
            };
            segment.Waypoints.Add(Waypoint(segment, first, 0, 1));
            segment.Waypoints.Add(Waypoint(segment, second, 1, 2));
            trip.Segments.Add(segment);
        }
        await using var context = fixture.CreateContext();
        context.Trips.Add(trip);
        await context.SaveChangesAsync();
        return new(
            user.Id, trip.Id, region.Id, from.Id, first.Id, second.Id, to.Id,
            profile.Id, profile.Key, segmentIds);
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
        throw new TimeoutException("The competing lifecycle writer did not enter a PostgreSQL lock wait.");
    }

    private static void AssertConcurrentOutcome<T>(Captured<T> captured)
    {
        if (captured.Exception == null) return;
        var postgres = captured.Exception as PostgresException
            ?? captured.Exception.InnerException as PostgresException;
        Assert.NotNull(postgres);
        Assert.Contains(postgres!.SqlState, new[]
        {
            PostgresErrorCodes.SerializationFailure,
            PostgresErrorCodes.DeadlockDetected
        });
        Assert.NotEqual(PostgresErrorCodes.DeadlockDetected, postgres.SqlState);
    }

    private static async Task<Captured<T>> CaptureAsync<T>(Func<Task<T>> operation)
    {
        try { return new(await operation(), null); }
        catch (Exception exception) { return new(default, exception); }
    }

    private static Task<int> BackendPidAsync(ApplicationDbContext context) =>
        context.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();

    private static PlaceLifecycleUpdate Update(Guid regionId, double x, double y) =>
        new(regionId, "Moved", "", "", "marker", "bg-blue", new Point(x, y) { SRID = 4326 });

    private static PlaceRegionLifecycleService Service(ApplicationDbContext context) =>
        new(context, new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider()));

    private static SegmentWaypoint Waypoint(Segment segment, Place place, int position, int index) => new()
    {
        Segment = segment,
        SegmentId = segment.Id,
        Place = place,
        PlaceId = place.Id,
        Position = position,
        RouteVertexIndex = index
    };

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

    public enum CompetingWriter { ProfileSpeed, SegmentReconciliation }

    private sealed record Captured<T>(T? Result, Exception? Exception);

    private sealed record ConcurrencySeed(
        string UserId,
        Guid TripId,
        Guid RegionId,
        Guid FromPlaceId,
        Guid FirstWaypointId,
        Guid SecondWaypointId,
        Guid ToPlaceId,
        Guid ProfileId,
        string ProfileKey,
        Guid[] SegmentIds);

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
