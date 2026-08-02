using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Proves deterministic PostgreSQL serialization and rollback for issue 405 measurement batches.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class SegmentMeasurementConcurrencyPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>A Segment edit waits for its referenced profile edit and then uses the committed canonical speed.</summary>
    [PostgresFact]
    public async Task SegmentEditVersusReferencedProfileSpeedChange_SerializesOneCoherentState()
    {
        var data = await SeedAsync(segmentCount: 1);
        var gate = new SaveGateInterceptor();
        await using var profileContext = fixture.CreateContext(gate);
        await using var segmentContext = fixture.CreateContext();
        await segmentContext.Database.OpenConnectionAsync();
        var profileTask = TransportProfileMeasurementReconciler.ReconcileAsync(
            profileContext, data.FirstProfile.Id, 20, data.User.Id, CancellationToken.None);
        await gate.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var secondPid = await BackendPidAsync(segmentContext);
        var segmentTask = SegmentRouteReconciler.ReconcileAsync(segmentContext,
            Proposal(data.Segments[0], data.FirstProfile, data.To, data.From), CancellationToken.None);
        await WaitUntilBlockedAsync(secondPid);
        gate.ReleaseSave.TrySetResult();

        Assert.True((await profileTask).Succeeded);
        Assert.True((await segmentTask).Succeeded);
        await AssertCoherentAutomaticAsync(data.Segments[0].Id, data.FirstProfile.Id, 20, data.To.Id, data.From.Id);
    }

    /// <summary>A mode change locks the current/proposed profile union and uses the final linked profile speed.</summary>
    [PostgresFact]
    public async Task ProfileSpeedChangeVersusSegmentModeChange_UsesCanonicalProfileWithoutDeadlock()
    {
        var data = await SeedAsync(segmentCount: 1, includeSecondProfile: true);
        var gate = new SaveGateInterceptor();
        await using var speedContext = fixture.CreateContext(gate);
        await using var modeContext = fixture.CreateContext();
        await modeContext.Database.OpenConnectionAsync();
        var speedTask = TransportProfileMeasurementReconciler.ReconcileAsync(
            speedContext, data.FirstProfile.Id, 12, data.User.Id, CancellationToken.None);
        await gate.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var pid = await BackendPidAsync(modeContext);
        var modeTask = SegmentRouteReconciler.ReconcileAsync(modeContext,
            Proposal(data.Segments[0], data.SecondProfile!, data.From, data.To), CancellationToken.None);
        await WaitUntilBlockedAsync(pid);
        gate.ReleaseSave.TrySetResult();

        Assert.True((await speedTask).Succeeded);
        Assert.True((await modeTask).Succeeded);
        await AssertCoherentAutomaticAsync(data.Segments[0].Id, data.SecondProfile!.Id, 30, data.From.Id, data.To.Id);
    }

    /// <summary>A waiter that observes A to C drift retries before applying proposed B with the complete profile union.</summary>
    [PostgresFact]
    public async Task WaitingSegmentChangesFromAToC_ProposedBLocksPostWaitUnionBeforeMutation()
    {
        var data = await SeedAsync(segmentCount: 1, includeSecondProfile: true);
        var thirdProfile = Profile("third", 45);
        fixture.RegisterTransportProfile(thirdProfile.Id);
        await using (var seed = fixture.CreateContext())
        {
            seed.Add(thirdProfile);
            await seed.SaveChangesAsync();
        }
        await using var blocker = fixture.CreateContext();
        await using var blockerTransaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM public.\"TransportProfiles\" WHERE \"Id\" = {data.FirstProfile.Id} FOR UPDATE");
        await using var waiter = fixture.CreateContext();
        await waiter.Database.OpenConnectionAsync();
        var waiterPid = await BackendPidAsync(waiter);
        var operation = SegmentRouteReconciler.ReconcileAsync(waiter,
            Proposal(data.Segments[0], data.SecondProfile!, data.From, data.To), CancellationToken.None);
        await WaitUntilBlockedAsync(waiterPid);
        await using (var drift = fixture.CreateContext())
            await drift.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE public.\"Segments\" SET \"Mode\" = {thirdProfile.Key}, \"TransportProfileId\" = {thirdProfile.Id} WHERE \"Id\" = {data.Segments[0].Id}");
        await blockerTransaction.CommitAsync();

        Assert.True((await operation).Succeeded);
        await AssertCoherentAutomaticAsync(data.Segments[0].Id, data.SecondProfile!.Id, 30, data.From.Id, data.To.Id);
    }

    /// <summary>Opposing profile moves acquire the same profile union order and cannot deadlock or mix measurements.</summary>
    [PostgresFact]
    public async Task OpposingModeChanges_LockProfileUnionInOneOrder()
    {
        var data = await SeedAsync(segmentCount: 2, includeSecondProfile: true, alternateProfiles: true);
        var gate = new SaveGateInterceptor();
        await using var first = fixture.CreateContext(gate);
        await using var second = fixture.CreateContext();
        await second.Database.OpenConnectionAsync();
        var firstTask = SegmentRouteReconciler.ReconcileAsync(first,
            Proposal(data.Segments[0], data.SecondProfile!, data.From, data.To), CancellationToken.None);
        await gate.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var pid = await BackendPidAsync(second);
        var secondTask = SegmentRouteReconciler.ReconcileAsync(second,
            Proposal(data.Segments[1], data.FirstProfile, data.To, data.From), CancellationToken.None);
        await WaitUntilBlockedAsync(pid);
        gate.ReleaseSave.TrySetResult();

        Assert.True((await firstTask).Succeeded);
        Assert.True((await secondTask).Succeeded);
        await AssertCoherentAutomaticAsync(data.Segments[0].Id, data.SecondProfile!.Id, 30, data.From.Id, data.To.Id);
        await AssertCoherentAutomaticAsync(data.Segments[1].Id, data.FirstProfile.Id, 5, data.To.Id, data.From.Id);
    }

    /// <summary>Two edits of one profile serialize or one receives PostgreSQL's bounded serialization rejection.</summary>
    [PostgresFact]
    public async Task ConcurrentEditsOfSameTransportProfile_DoNotLoseSuccessfulUpdate()
    {
        var data = await SeedAsync(segmentCount: 2);
        var outcomes = await RunOpposingProfileEditsAsync(data, 10, 20);
        Assert.Equal(1, outcomes.Count(item => item.Succeeded));
        var speed = await CurrentSpeedAsync(data.FirstProfile.Id);
        Assert.True(speed is 10d or 20d);
        await AssertAllAutomaticMatchAsync(data, speed!.Value);
    }

    /// <summary>A positive speed edit and speed clear cannot leave a partial batch or alter Manual duration.</summary>
    [PostgresFact]
    public async Task ProfileWidePositiveSpeedEditVersusClear_CommitsOneCompleteBatch()
    {
        var data = await SeedAsync(segmentCount: 3, includeManual: true);
        var outcomes = await RunOpposingProfileEditsAsync(data, 18, null);
        Assert.Equal(1, outcomes.Count(item => item.Succeeded));
        var speed = await CurrentSpeedAsync(data.FirstProfile.Id);
        await AssertAllAutomaticMatchAsync(data, speed);
        await using var verify = fixture.CreateContext();
        Assert.Equal(TimeSpan.FromSeconds(91), (await verify.Segments.AsNoTracking()
            .SingleAsync(item => item.Id == data.ManualSegmentId)).EstimatedDuration);
    }

    /// <summary>Reversed seeded IDs still produce ascending Segment row locks during profile reconciliation.</summary>
    [PostgresFact]
    public async Task MultiSegmentProfileReconciliation_LocksSegmentsInAscendingGuidOrder()
    {
        var data = await SeedAsync(segmentCount: 4, reverseSegmentIds: true);
        var recorder = new SegmentLockRecorder();
        await using var context = fixture.CreateContext(recorder);
        Assert.True((await TransportProfileMeasurementReconciler.ReconcileAsync(
            context, data.FirstProfile.Id, 11, data.User.Id, CancellationToken.None)).Succeeded);
        Assert.Equal(data.Segments.Select(item => item.Id).Order().ToArray(), recorder.LockedIds.ToArray());
        await AssertAllAutomaticMatchAsync(data, 11);
    }

    /// <summary>Overlapping profile-wide batches supplied from opposite callers serialize without deadlock.</summary>
    [PostgresFact]
    public async Task OpposingMultiSegmentOperations_HaveCoherentSerializedOutcome()
    {
        var data = await SeedAsync(segmentCount: 5, reverseSegmentIds: true);
        var outcomes = await RunOpposingProfileEditsAsync(data, 9, 27);
        Assert.Equal(1, outcomes.Count(item => item.Succeeded));
        var speed = await CurrentSpeedAsync(data.FirstProfile.Id);
        await AssertAllAutomaticMatchAsync(data, speed!.Value);
    }

    /// <summary>A profile batch re-reads dependencies after its known lock wait instead of using the pre-wait set.</summary>
    [PostgresFact]
    public async Task DependencyDriftAfterLockWait_RevalidatesMutatedDependency()
    {
        var data = await SeedAsync(segmentCount: 1, includeSecondProfile: true);
        await using var blocker = fixture.CreateContext();
        await using var blockerTransaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM public.\"TransportProfiles\" WHERE \"Id\" = {data.FirstProfile.Id} FOR UPDATE");
        await using var waiter = fixture.CreateContext();
        await waiter.Database.OpenConnectionAsync();
        var waiterPid = await BackendPidAsync(waiter);
        var operation = TransportProfileMeasurementReconciler.ReconcileAsync(
            waiter, data.FirstProfile.Id, 16, data.User.Id, CancellationToken.None);
        await WaitUntilBlockedAsync(waiterPid);

        await using (var drift = fixture.CreateContext())
            await drift.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE public.\"Segments\" SET \"Mode\" = {data.SecondProfile!.Key}, \"TransportProfileId\" = {data.SecondProfile.Id}, \"EstimatedDistanceKm\" = {11.119d}, \"EstimatedDuration\" = {ExpectedDuration(30)} WHERE \"Id\" = {data.Segments[0].Id}");
        await blockerTransaction.CommitAsync();

        var outcome = await CaptureAsync(() => operation);
        Assert.False(outcome.Succeeded);
        Assert.True(outcome.SerializationFailure);
        await AssertCoherentAutomaticAsync(data.Segments[0].Id, data.SecondProfile!.Id, 30, data.From.Id, data.To.Id);
        Assert.Equal(5, await CurrentSpeedAsync(data.FirstProfile.Id));
    }

    /// <summary>A provider failure rolls profile, every Segment, and audit back and leaves no tracked intermediate work.</summary>
    [PostgresFact]
    public async Task ProviderFailureDuringBatch_RollsBackAndRecoversTracker()
    {
        var data = await SeedAsync(segmentCount: 3);
        var failure = new ThrowingSaveInterceptor();
        await using var context = fixture.CreateContext(failure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => TransportProfileMeasurementReconciler.ReconcileAsync(
            context, data.FirstProfile.Id, 14, data.User.Id, CancellationToken.None));
        Assert.All(context.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
        await context.SaveChangesAsync();
        Assert.Equal(5d, await CurrentSpeedAsync(data.FirstProfile.Id));
        await AssertAllAutomaticMatchAsync(data, 5);
        Assert.Equal(0, await AuditCountAsync(data.FirstProfile.Id));
    }

    /// <summary>A rollback failure preserves the provider failure and deterministically invalidates the context.</summary>
    [PostgresFact]
    public async Task ProfileBatchRollbackFailure_PreservesCombinedFailuresAndInvalidatesContext()
    {
        var data = await SeedAsync(segmentCount: 2);
        var saveFailure = new ThrowingSaveInterceptor();
        await using var context = fixture.CreateContext(saveFailure, new RollbackFailureInterceptor());

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => TransportProfileMeasurementReconciler.ReconcileAsync(
            context, data.FirstProfile.Id, 14, data.User.Id, CancellationToken.None));

        Assert.Contains("Deterministic provider failure.", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("Deterministic profile rollback failure.", exception.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.Segments.CountAsync());
        Assert.Equal(5, await CurrentSpeedAsync(data.FirstProfile.Id));
        Assert.Equal(0, await AuditCountAsync(data.FirstProfile.Id));
    }

    /// <summary>Cancellation during batch persistence uses non-cancelled rollback and leaves no partial state.</summary>
    [PostgresFact]
    public async Task CancellationDuringBatchReconciliation_RollsBackWithoutPartialState()
    {
        var data = await SeedAsync(segmentCount: 3);
        using var cancellation = new CancellationTokenSource();
        var interceptor = new CancellationSaveInterceptor();
        await using var context = fixture.CreateContext(interceptor);
        var task = TransportProfileMeasurementReconciler.ReconcileAsync(
            context, data.FirstProfile.Id, 14, data.User.Id, cancellation.Token);
        await interceptor.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.All(context.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
        Assert.Equal(5d, await CurrentSpeedAsync(data.FirstProfile.Id));
        await AssertAllAutomaticMatchAsync(data, 5);
        Assert.Equal(0, await AuditCountAsync(data.FirstProfile.Id));
    }

    private async Task<EditOutcome[]> RunOpposingProfileEditsAsync(
        FixtureData data, double? firstSpeed, double? secondSpeed)
    {
        var gate = new SaveGateInterceptor();
        await using var first = fixture.CreateContext(gate);
        await using var second = fixture.CreateContext();
        await second.Database.OpenConnectionAsync();
        var firstTask = CaptureAsync(() => TransportProfileMeasurementReconciler.ReconcileAsync(
            first, data.FirstProfile.Id, firstSpeed, data.User.Id, CancellationToken.None));
        await gate.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var pid = await BackendPidAsync(second);
        var secondTask = CaptureAsync(() => TransportProfileMeasurementReconciler.ReconcileAsync(
            second, data.FirstProfile.Id, secondSpeed, data.User.Id, CancellationToken.None));
        await WaitUntilBlockedAsync(pid);
        gate.ReleaseSave.TrySetResult();
        return [await firstTask, await secondTask];
    }

    private static async Task<EditOutcome> CaptureAsync(Func<Task<TransportProfileMeasurementResult>> action)
    {
        try { return new((await action()).Succeeded, false); }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        { return new(false, true); }
    }

    private async Task<FixtureData> SeedAsync(
        int segmentCount, bool includeSecondProfile = false, bool alternateProfiles = false,
        bool includeManual = false, bool reverseSegmentIds = false)
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Measurement concurrency" };
        fixture.RegisterTrip(trip.Id);
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id, Name = "region" };
        var from = new Place { Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = user.Id, Name = "from", Location = new Point(0, 0) { SRID = 4326 } };
        var to = new Place { Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = user.Id, Name = "to", Location = new Point(0.1, 0) { SRID = 4326 } };
        var firstProfile = Profile("first", 5);
        var secondProfile = includeSecondProfile ? Profile("second", 30) : null;
        fixture.RegisterTransportProfile(firstProfile.Id);
        if (secondProfile != null) fixture.RegisterTransportProfile(secondProfile.Id);
        var ids = Enumerable.Range(0, segmentCount).Select(_ => Guid.NewGuid()).Order().ToArray();
        if (reverseSegmentIds) Array.Reverse(ids);
        var segments = ids.Select((id, index) => Segment(id, trip,
            alternateProfiles && index % 2 == 1 ? secondProfile! : firstProfile, from, to)).ToArray();
        Guid? manualId = null;
        if (includeManual)
        {
            var manual = Segment(Guid.NewGuid(), trip, firstProfile, from, to);
            manual.EstimatedDurationSource = EstimatedDurationSource.Manual;
            manual.EstimatedDuration = TimeSpan.FromSeconds(91);
            segments = [.. segments, manual];
            manualId = manual.Id;
        }
        await using var context = fixture.CreateContext();
        context.AddRange(trip, region, from, to, firstProfile);
        if (secondProfile != null) context.Add(secondProfile);
        context.AddRange(segments);
        await context.SaveChangesAsync();
        return new(user, firstProfile, secondProfile, from, to, segments, manualId);
    }

    private static TransportProfile Profile(string name, double speed) => new()
    {
        Id = Guid.NewGuid(), Key = $"measure-{name}-{Guid.NewGuid():N}"[..40], Label = name,
        Category = "Test", PlanningSpeedKmh = speed, IsActive = true
    };

    private static Segment Segment(Guid id, Trip trip, TransportProfile profile, Place from, Place to) => new()
    {
        Id = id, Trip = trip, TripId = trip.Id, UserId = trip.UserId, FromPlace = from, FromPlaceId = from.Id,
        ToPlace = to, ToPlaceId = to.Id, Mode = profile.Key, TransportProfile = profile,
        TransportProfileId = profile.Id, EstimatedDurationSource = EstimatedDurationSource.Automatic,
        EstimatedDuration = ExpectedDuration(5), EstimatedDistanceKm = ExpectedDistance.RoundedKilometres
    };

    private static SegmentRouteProposal Proposal(Segment segment, TransportProfile profile, Place from, Place to) =>
        new(segment.Id, from.Id, to.Id, [], null,
            new(profile.Key, profile.Id, EstimatedDurationSource.Automatic, null));

    private async Task AssertCoherentAutomaticAsync(Guid segmentId, Guid profileId, double speed, Guid fromId, Guid toId)
    {
        await using var context = fixture.CreateContext();
        var segment = await context.Segments.AsNoTracking().SingleAsync(item => item.Id == segmentId);
        Assert.Equal(profileId, segment.TransportProfileId);
        Assert.Equal((fromId, toId), (segment.FromPlaceId, segment.ToPlaceId));
        Assert.Equal(EstimatedDurationSource.Automatic, segment.EstimatedDurationSource);
        Assert.Equal(ExpectedDistance.RoundedKilometres, segment.EstimatedDistanceKm);
        Assert.Equal(ExpectedDuration(speed), segment.EstimatedDuration);
    }

    private async Task AssertAllAutomaticMatchAsync(FixtureData data, double? speed)
    {
        await using var context = fixture.CreateContext();
        var segments = await context.Segments.AsNoTracking().Where(item => item.TripId == data.Segments[0].TripId).ToArrayAsync();
        foreach (var segment in segments.Where(item => item.EstimatedDurationSource == EstimatedDurationSource.Automatic))
        {
            Assert.Equal(ExpectedDistance.RoundedKilometres, segment.EstimatedDistanceKm);
            Assert.Equal(speed.HasValue ? ExpectedDuration(speed.Value) : null, segment.EstimatedDuration);
        }
    }

    private const double IndependentExpectedMetres = 11_119.492664455875d;
    private static SegmentDistanceMeasurement ExpectedDistance => new(IndependentExpectedMetres, 11.119d);

    private static TimeSpan ExpectedDuration(double speed) =>
        TimeSpan.FromSeconds(Math.Round(
            IndependentExpectedMetres / (speed * 1000d / 3600d),
            MidpointRounding.AwayFromZero));

    private async Task<double?> CurrentSpeedAsync(Guid profileId)
    {
        await using var context = fixture.CreateContext();
        return await context.Set<TransportProfile>().Where(item => item.Id == profileId)
            .Select(item => item.PlanningSpeedKmh).SingleAsync();
    }

    private async Task<int> AuditCountAsync(Guid profileId)
    {
        await using var context = fixture.CreateContext();
        return await context.AuditLogs.CountAsync(item => item.Details.Contains($"ProfileId={profileId}"));
    }

    private async Task WaitUntilBlockedAsync(int backendPid)
    {
        await using var context = fixture.CreateContext();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var count = await context.Database.SqlQueryRaw<int>(
                "SELECT cardinality(pg_blocking_pids({0})) AS \"Value\"", backendPid).SingleAsync();
            if (count > 0) return;
            await Task.Yield();
        }
        throw new TimeoutException("The competing measurement operation did not enter a PostgreSQL lock wait.");
    }

    private static Task<int> BackendPidAsync(ApplicationDbContext context) =>
        context.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();

    private sealed class SaveGateInterceptor : SaveChangesInterceptor
    {
        internal TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            SaveEntered.TrySetResult();
            await ReleaseSave.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class SegmentLockRecorder : DbCommandInterceptor
    {
        internal List<Guid> LockedIds { get; } = [];
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM public.\"Segments\"") && command.CommandText.Contains("FOR UPDATE"))
                LockedIds.Add((Guid)command.Parameters[0].Value!);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowingSaveInterceptor : SaveChangesInterceptor
    {
        private bool _throw = true;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_throw) { _throw = false; throw new InvalidOperationException("Deterministic provider failure."); }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CancellationSaveInterceptor : SaveChangesInterceptor
    {
        internal TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            SaveEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return result;
        }
    }

    private sealed class RollbackFailureInterceptor : DbTransactionInterceptor
    {
        /// <summary>Fails mandatory rollback so combined failure preservation and invalidation are observable.</summary>
        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult>(
                new InvalidOperationException("Deterministic profile rollback failure."));
    }

    private sealed record FixtureData(
        ApplicationUser User, TransportProfile FirstProfile, TransportProfile? SecondProfile,
        Place From, Place To, Segment[] Segments, Guid? ManualSegmentId);
    private sealed record EditOutcome(bool Succeeded, bool SerializationFailure);
}
