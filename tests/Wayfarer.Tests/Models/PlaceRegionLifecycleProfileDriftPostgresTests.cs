using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Proves confirmed destructive requests never use post-wait Segment profiles without holding their locks.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class PlaceRegionLifecycleProfileDriftPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Covers linked and unlinked profile drift plus multi-Segment and already-held-union behavior.</summary>
    [PostgresTheory]
    [InlineData(ProfileState.A, ProfileState.B, 1, false, true)]
    [InlineData(ProfileState.A, ProfileState.None, 1, false, false)]
    [InlineData(ProfileState.None, ProfileState.B, 1, false, true)]
    [InlineData(ProfileState.A, ProfileState.B, 2, false, true)]
    public async Task ConfirmedPlaceDeletion_PostWaitProfileRequirements_AreHeldOrReturnStale(
        ProfileState initial,
        ProfileState replacement,
        int segmentCount,
        bool replacementInitiallyInUnion,
        bool expectStale)
    {
        var seeded = await SeedAsync(initial, segmentCount, replacementInitiallyInUnion);
        var confirmation = Confirmation();
        var token = await PlaceTokenAsync(seeded, confirmation);

        var result = await DriftDuringSegmentWaitAsync(
            seeded,
            replacement,
            context => Service(context, confirmation).DeletePlaceAsync(
                seeded.TripId, seeded.WaypointId, seeded.UserId, token, CancellationToken.None));

        Assert.Equal(!expectStale, result.Succeeded);
        if (!expectStale)
        {
            Assert.Null(result.Warning);
            return;
        }

        Assert.Equal("lifecycle-confirmation-stale", result.Warning!.Code);
        await AssertUnchangedAsync(seeded, replacement);
        var observer = new LockObservationInterceptor();
        await using var retryContext = fixture.CreateContext(observer);
        var retry = await Service(retryContext, confirmation).DeletePlaceAsync(
            seeded.TripId, seeded.WaypointId, seeded.UserId,
            result.Warning.ConfirmationToken, CancellationToken.None);
        Assert.True(retry.Succeeded);
        AssertRequiredProfilePrecedesSegments(observer, seeded, replacement);
    }

    /// <summary>Returns fresh stale confirmation for the same profile drift during confirmed Region deletion.</summary>
    [PostgresFact]
    public async Task ConfirmedRegionDeletion_ProfileChangesDuringSegmentWait_ReturnsFreshStaleToken()
    {
        var seeded = await SeedAsync(ProfileState.A, 1, false);
        var confirmation = Confirmation();
        var token = await RegionTokenAsync(seeded, confirmation);

        var result = await DriftDuringSegmentWaitAsync(
            seeded,
            ProfileState.B,
            context => Service(context, confirmation).DeleteRegionAsync(
                seeded.TripId, seeded.DeletedRegionId, seeded.UserId, token, CancellationToken.None));

        Assert.False(result.Succeeded);
        Assert.Equal("lifecycle-confirmation-stale", result.Warning!.Code);
        await AssertUnchangedAsync(seeded, ProfileState.B);
        var observer = new LockObservationInterceptor();
        await using var retryContext = fixture.CreateContext(observer);
        var retry = await Service(retryContext, confirmation).DeleteRegionAsync(
            seeded.TripId, seeded.DeletedRegionId, seeded.UserId,
            result.Warning.ConfirmationToken, CancellationToken.None);
        Assert.True(retry.Succeeded);
        AssertRequiredProfilePrecedesSegments(observer, seeded, ProfileState.B);
    }

    /// <summary>Allows drift to a replacement profile already included in the original locked union.</summary>
    [PostgresFact]
    public async Task ConfirmedPlaceDeletion_ReplacementProfileAlreadyInCandidateUnion_DoesNotReturnFalseStale()
    {
        var seeded = await SeedAsync(ProfileState.A, 2, true);
        var confirmation = Confirmation();
        var token = await PlaceTokenAsync(seeded, confirmation);
        var gate = new BeforeFirstLockGateInterceptor();
        await using var lifecycle = fixture.CreateContext(gate);
        var operation = Service(lifecycle, confirmation).DeletePlaceAsync(
            seeded.TripId, seeded.WaypointId, seeded.UserId, token, CancellationToken.None);
        await gate.Entered.Task;
        await using (var mutation = fixture.CreateContext())
        {
            await mutation.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE public.\"Segments\" SET \"Mode\" = {seeded.ProfileB.Key}, \"TransportProfileId\" = {seeded.ProfileB.Id} WHERE \"Id\" = {seeded.DriftSegmentId}");
        }
        gate.Release.TrySetResult();

        var result = await operation;

        Assert.True(result.Succeeded);
        Assert.Null(result.Warning);
        AssertRequiredProfilePrecedesSegments(gate.Observer, seeded, ProfileState.B);
    }

    private async Task<TResult> DriftDuringSegmentWaitAsync<TResult>(
        DriftSeed seeded,
        ProfileState replacement,
        Func<ApplicationDbContext, Task<TResult>> operation)
    {
        await using var blocker = fixture.CreateContext();
        await using var blockerTransaction = await blocker.Database.BeginTransactionAsync();
        await SegmentRouteReconciler.LockSegmentAsync(blocker, seeded.DriftSegmentId, CancellationToken.None);
        await using var lifecycle = fixture.CreateContext();
        await lifecycle.Database.OpenConnectionAsync();
        var lifecyclePid = await lifecycle.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        var task = operation(lifecycle);
        await WaitUntilBlockedAsync(lifecyclePid);
        var replacementId = ProfileId(seeded, replacement);
        var replacementKey = ProfileKey(seeded, replacement);
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE public.\"Segments\" SET \"Mode\" = {replacementKey}, \"TransportProfileId\" = {replacementId} WHERE \"Id\" = {seeded.DriftSegmentId}");
        await blockerTransaction.CommitAsync();
        return await task;
    }

    private async Task<DriftSeed> SeedAsync(ProfileState initial, int segmentCount, bool replacementInitiallyInUnion)
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var profileA = Profile("profile-a");
        var profileB = Profile("profile-b");
        fixture.RegisterTransportProfile(profileA.Id);
        fixture.RegisterTransportProfile(profileB.Id);
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Profile lifecycle drift" };
        fixture.RegisterTrip(trip.Id);
        var deleted = Region(trip, user.Id, "Deleted", 1);
        var outside = Region(trip, user.Id, "Outside", 2);
        var waypoint = Place(deleted, user.Id, "Waypoint", 2, 2);
        var from = Place(outside, user.Id, "From", 1, 1);
        var to = Place(outside, user.Id, "To", 3, 3);
        var segmentIds = Enumerable.Range(0, segmentCount).Select(_ => Guid.NewGuid()).Order().ToArray();
        foreach (var (id, index) in segmentIds.Select((id, index) => (id, index)))
        {
            var state = index == 1 && replacementInitiallyInUnion ? ProfileState.B : initial;
            var segment = new Segment
            {
                Id = id, Trip = trip, TripId = trip.Id, UserId = user.Id,
                FromPlaceId = from.Id, ToPlaceId = to.Id, DisplayOrder = index + 1,
                Mode = ProfileKey(profileA, profileB, state), TransportProfileId = ProfileId(profileA, profileB, state),
                EstimatedDistanceKm = 10, EstimatedDuration = TimeSpan.FromHours(2),
                EstimatedDurationSource = EstimatedDurationSource.Manual,
                RouteGeometry = Line()
            };
            segment.Waypoints.Add(new SegmentWaypoint
            {
                Segment = segment, SegmentId = segment.Id, Place = waypoint, PlaceId = waypoint.Id,
                Position = 0, RouteVertexIndex = 1
            });
            trip.Segments.Add(segment);
        }
        await using var context = fixture.CreateContext();
        context.Set<TransportProfile>().AddRange(profileA, profileB);
        context.Trips.Add(trip);
        await context.SaveChangesAsync();
        return new(user.Id, trip.Id, deleted.Id, waypoint.Id, segmentIds[0], segmentIds, profileA, profileB);
    }

    private async Task AssertUnchangedAsync(DriftSeed seeded, ProfileState replacement)
    {
        await using var context = fixture.CreateContext();
        Assert.True(await context.Places.AnyAsync(item => item.Id == seeded.WaypointId));
        Assert.True(await context.Regions.AnyAsync(item => item.Id == seeded.DeletedRegionId));
        var segments = await context.Segments.Include(item => item.Waypoints)
            .Where(item => seeded.SegmentIds.Contains(item.Id)).OrderBy(item => item.Id).ToArrayAsync();
        Assert.Equal(seeded.SegmentIds, segments.Select(item => item.Id));
        var drifted = segments[0];
        Assert.Equal(ProfileId(seeded, replacement), drifted.TransportProfileId);
        Assert.Equal(10, drifted.EstimatedDistanceKm);
        Assert.Equal(TimeSpan.FromHours(2), drifted.EstimatedDuration);
        Assert.Contains(drifted.Waypoints, item => item.PlaceId == seeded.WaypointId);
        Assert.Equal(new Coordinate(2, 2), drifted.RouteGeometry!.Coordinates[1]);
    }

    private async Task<string> PlaceTokenAsync(DriftSeed seeded, LifecycleDependencyConfirmation confirmation)
    {
        await using var context = fixture.CreateContext();
        var result = await Service(context, confirmation).DeletePlaceAsync(
            seeded.TripId, seeded.WaypointId, seeded.UserId, null, CancellationToken.None);
        return result.Warning!.ConfirmationToken;
    }

    private async Task<string> RegionTokenAsync(DriftSeed seeded, LifecycleDependencyConfirmation confirmation)
    {
        await using var context = fixture.CreateContext();
        var result = await Service(context, confirmation).DeleteRegionAsync(
            seeded.TripId, seeded.DeletedRegionId, seeded.UserId, null, CancellationToken.None);
        return result.Warning!.ConfirmationToken;
    }

    private async Task WaitUntilBlockedAsync(int backendPid)
    {
        await using var context = fixture.CreateContext();
        await context.Database.OpenConnectionAsync();
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var count = await context.Database.SqlQueryRaw<int>(
                "SELECT cardinality(pg_blocking_pids({0})) AS \"Value\"", backendPid).SingleAsync();
            if (count > 0) return;
            await Task.Yield();
        }
        throw new TimeoutException("Confirmed lifecycle deletion did not reach the gated Segment row lock.");
    }

    private static void AssertRequiredProfilePrecedesSegments(
        LockObservationInterceptor observer,
        DriftSeed seeded,
        ProfileState state)
    {
        var required = ProfileId(seeded, state);
        if (!required.HasValue) return;
        var profileIndex = observer.Locks.FindIndex(item => item.Class == "profile" && item.Id == required);
        var segmentIndex = observer.Locks.FindIndex(item => item.Class == "segment");
        Assert.True(profileIndex >= 0);
        Assert.True(segmentIndex > profileIndex);
    }

    private static LifecycleDependencyConfirmation Confirmation() =>
        new(new EphemeralDataProtectionProvider());

    private static PlaceRegionLifecycleService Service(
        ApplicationDbContext context,
        LifecycleDependencyConfirmation confirmation) => new(context, confirmation);

    private static TransportProfile Profile(string prefix) => new()
    {
        Id = Guid.NewGuid(), Key = $"{prefix}-{Guid.NewGuid():N}"[..30], Label = prefix,
        Category = "Test", PlanningSpeedKmh = 5, IsActive = false
    };

    private static Region Region(Trip trip, string userId, string name, int order)
    {
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = name, DisplayOrder = order };
        trip.Regions.Add(region);
        return region;
    }

    private static Place Place(Region region, string userId, string name, double x, double y)
    {
        var place = new Place { Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = userId, Name = name, DisplayOrder = region.Places.Count + 1, Location = new(x, y) { SRID = 4326 } };
        region.Places.Add(place);
        return place;
    }

    private static LineString Line() => new([new(1, 1), new(2, 2), new(3, 3)]) { SRID = 4326 };
    private static Guid? ProfileId(DriftSeed seed, ProfileState state) => ProfileId(seed.ProfileA, seed.ProfileB, state);
    private static Guid? ProfileId(TransportProfile a, TransportProfile b, ProfileState state) => state switch { ProfileState.A => a.Id, ProfileState.B => b.Id, _ => null };
    private static string ProfileKey(DriftSeed seed, ProfileState state) => ProfileKey(seed.ProfileA, seed.ProfileB, state);
    private static string ProfileKey(TransportProfile a, TransportProfile b, ProfileState state) => state switch { ProfileState.A => a.Key, ProfileState.B => b.Key, _ => string.Empty };

    public enum ProfileState { None, A, B }

    private sealed record DriftSeed(
        string UserId, Guid TripId, Guid DeletedRegionId, Guid WaypointId, Guid DriftSegmentId,
        Guid[] SegmentIds, TransportProfile ProfileA, TransportProfile ProfileB);

    private sealed record ObservedLock(string Class, Guid Id);

    /// <summary>Records exact production lock identities in acquisition order.</summary>
    private sealed class LockObservationInterceptor : DbCommandInterceptor
    {
        internal List<ObservedLock> Locks { get; } = [];

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var lockClass = command.CommandText switch
            {
                var text when text.Contains("\"TransportProfiles\"", StringComparison.Ordinal) => "profile",
                var text when text.Contains("\"Segments\"", StringComparison.Ordinal) => "segment",
                _ => null
            };
            var id = command.Parameters.Cast<DbParameter>().Select(item => item.Value).OfType<Guid>().FirstOrDefault();
            if (lockClass != null && id != Guid.Empty && command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase))
                Locks.Add(new(lockClass, id));
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Pauses immediately before the first production lock while retaining exact lock observations.</summary>
    private sealed class BeforeFirstLockGateInterceptor : DbCommandInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LockObservationInterceptor Observer { get; } = new();

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Entered.Task.IsCompleted && command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            await Observer.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
            return result;
        }
    }
}
