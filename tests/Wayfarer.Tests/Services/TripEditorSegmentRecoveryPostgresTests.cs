using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Executes destructive-replacement failure and tracker recovery scenarios for #407.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class TripEditorSegmentRecoveryPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>A tracker-restoration failure is retained after the real provider failure and recovery path.</summary>
    [PostgresFact]
    public async Task TrackerRestorationFailure_RetainsEveryFailureAndInvalidatesContext()
    {
        var plan = new FailurePlan();
        var seed = await SeedAsync();
        await using var context = fixture.CreateContext(new FailingSaveInterceptor(plan));
        var recovery = new FailingContextRecovery(failRestore: true, failDispose: false);

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => ReplaceAsync(context, seed, CancellationToken.None, recovery));

        Assert.Collection(exception.InnerExceptions,
            original => Assert.Equal("Deterministic #407 provider failure.", original.Message),
            restoration => Assert.Same(recovery.RestorationFailure, restoration));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.Segments.CountAsync());
        await AssertOriginalAggregateAsync(seed);
    }

    /// <summary>A disposal failure cannot replace any earlier cleanup failure.</summary>
    [PostgresFact]
    public async Task DisposalFailure_RetainsOperationCleanupAndDisposalFailuresInOrder()
    {
        var plan = new FailurePlan();
        var seed = await SeedAsync();
        var operation = new FailingSaveInterceptor(plan);
        var rollback = new RollbackFailureInterceptor(plan);
        var providerRecovery = new RecoveryFailureInterceptor(plan);
        var contextRecovery = new FailingContextRecovery(failRestore: true, failDispose: true);
        await using var context = fixture.CreateContext(operation, rollback, providerRecovery);

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => ReplaceAsync(context, seed, CancellationToken.None, contextRecovery));

        Assert.Collection(exception.InnerExceptions,
            original => Assert.Same(operation.Failure, original),
            rollbackFailure => Assert.Same(rollback.Failure, rollbackFailure),
            recoveryFailure => Assert.Same(providerRecovery.Failure, recoveryFailure),
            restorationFailure => Assert.Same(contextRecovery.RestorationFailure, restorationFailure),
            disposalFailure => Assert.Same(contextRecovery.DisposalFailure, disposalFailure));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.Segments.CountAsync());
        await AssertOriginalAggregateAsync(seed);
    }
    /// <summary>A provider failure after destructive replacement rolls back every aggregate field and restores tracker coherence.</summary>
    [PostgresFact]
    public async Task ProviderFailureAfterDestructiveReplacement_RollsBackAndRecoversTracker()
    {
        var plan = new FailurePlan();
        var seed = await SeedAsync();
        await using var context = fixture.CreateContext(new FailingSaveInterceptor(plan));
        var trackerBefore = TrackerSnapshot(context);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ReplaceAsync(context, seed, CancellationToken.None));

        Assert.Equal("Deterministic #407 provider failure.", exception.Message);
        Assert.Equal(trackerBefore, TrackerSnapshot(context));
        await AssertContextReusableAsync(context, seed);
        await AssertOriginalAggregateAsync(seed);
    }

    /// <summary>Cancellation after destructive replacement uses a non-cancelled rollback and persists no partial state.</summary>
    [PostgresFact]
    public async Task CancellationAfterDestructiveReplacement_RollsBackWithoutPartialState()
    {
        using var cancellation = new CancellationTokenSource();
        var interceptor = new CancellingSaveInterceptor();
        var seed = await SeedAsync();
        await using var context = fixture.CreateContext(interceptor);
        var trackerBefore = TrackerSnapshot(context);
        var mutation = ReplaceAsync(context, seed, cancellation.Token);
        await interceptor.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation);
        Assert.Equal(trackerBefore, TrackerSnapshot(context));
        await AssertContextReusableAsync(context, seed);
        await AssertOriginalAggregateAsync(seed);
    }

    /// <summary>Rollback failure retains the operation and cleanup failures and invalidates the unsafe context.</summary>
    [PostgresFact]
    public async Task RollbackFailure_RetainsBothFailuresAndInvalidatesContext()
    {
        var plan = new FailurePlan();
        var seed = await SeedAsync();
        await using var context = fixture.CreateContext(new FailingSaveInterceptor(plan), new RollbackFailureInterceptor(plan));
        var exception = await Assert.ThrowsAsync<AggregateException>(() => ReplaceAsync(context, seed, CancellationToken.None));

        Assert.Contains("Deterministic #407 provider failure.", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("Deterministic #407 rollback failure.", exception.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.Segments.CountAsync());
        await AssertOriginalAggregateAsync(seed);
    }

    /// <summary>Recovery failure retains the operation and recovery failures and invalidates the unsafe context.</summary>
    [PostgresFact]
    public async Task RecoveryFailure_RetainsBothFailuresAndInvalidatesContext()
    {
        var plan = new FailurePlan();
        var seed = await SeedAsync();
        await using var context = fixture.CreateContext(new FailingSaveInterceptor(plan), new RecoveryFailureInterceptor(plan));
        var exception = await Assert.ThrowsAsync<AggregateException>(() => ReplaceAsync(context, seed, CancellationToken.None));

        Assert.Contains("Deterministic #407 provider failure.", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("Deterministic #407 recovery failure.", exception.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.Segments.CountAsync());
        await AssertOriginalAggregateAsync(seed);
    }

    /// <summary>Combined operation, rollback, and recovery failures are all retained before context invalidation.</summary>
    [PostgresFact]
    public async Task RollbackAndRecoveryFailure_RetainsEveryFailureAndInvalidatesContext()
    {
        var plan = new FailurePlan();
        var seed = await SeedAsync();
        await using var context = fixture.CreateContext(
            new FailingSaveInterceptor(plan), new RollbackFailureInterceptor(plan), new RecoveryFailureInterceptor(plan));
        var exception = await Assert.ThrowsAsync<AggregateException>(() => ReplaceAsync(context, seed, CancellationToken.None));

        Assert.Contains("Deterministic #407 provider failure.", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("Deterministic #407 rollback failure.", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("Deterministic #407 recovery failure.", exception.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.Segments.CountAsync());
        await AssertOriginalAggregateAsync(seed);
    }

    /// <summary>A serialization failure after token comparison is classified only after exact cleanup restores a reusable context.</summary>
    [PostgresFact]
    public async Task SerializationFailure_SuccessfulCleanup_ReturnsWriteConflictAndReusableContext()
    {
        var plan = new FailurePlan();
        var seed = await SeedAsync();
        await using var context = fixture.CreateContext(new SerializationSaveInterceptor(plan));

        var outcome = await ReplaceOutcomeAsync(context, seed);

        Assert.Equal(EditorRegionMutationStatus.Conflict, outcome.Status);
        Assert.Equal("segment-write-conflict", Assert.IsType<EditorSegmentConflictDto>(outcome.Conflict).Code);
        await AssertContextReusableAsync(context, seed);
        await AssertOriginalAggregateAsync(seed);
    }

    /// <summary>A rollback failure cannot replace the original serialization failure and invalidates the context.</summary>
    [PostgresFact]
    public async Task SerializationAndRollbackFailure_RetainsBothAndInvalidatesContext()
    {
        var plan = new FailurePlan();
        var seed = await SeedAsync();
        await using var context = fixture.CreateContext(new SerializationSaveInterceptor(plan), new RollbackFailureInterceptor(plan));

        var exception = await Assert.ThrowsAsync<AggregateException>(() => ReplaceAsync(context, seed, CancellationToken.None));

        Assert.Contains(PostgresErrorCodes.SerializationFailure, exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("Deterministic #407 rollback failure.", exception.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.Segments.CountAsync());
        await AssertOriginalAggregateAsync(seed);
    }

    /// <summary>A recovery failure cannot replace the original serialization failure and invalidates the context.</summary>
    [PostgresFact]
    public async Task SerializationAndRecoveryFailure_RetainsBothAndInvalidatesContext()
    {
        var plan = new FailurePlan();
        var seed = await SeedAsync();
        await using var context = fixture.CreateContext(new SerializationSaveInterceptor(plan), new RecoveryFailureInterceptor(plan));

        var exception = await Assert.ThrowsAsync<AggregateException>(() => ReplaceAsync(context, seed, CancellationToken.None));

        Assert.Contains(PostgresErrorCodes.SerializationFailure, exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("Deterministic #407 recovery failure.", exception.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.Segments.CountAsync());
        await AssertOriginalAggregateAsync(seed);
    }

    /// <summary>Serialization, rollback, and recovery failures are retained together before invalidation.</summary>
    [PostgresFact]
    public async Task SerializationRollbackAndRecoveryFailure_RetainsAllAndInvalidatesContext()
    {
        var plan = new FailurePlan();
        var seed = await SeedAsync();
        await using var context = fixture.CreateContext(
            new SerializationSaveInterceptor(plan), new RollbackFailureInterceptor(plan), new RecoveryFailureInterceptor(plan));

        var exception = await Assert.ThrowsAsync<AggregateException>(() => ReplaceAsync(context, seed, CancellationToken.None));

        Assert.Contains(PostgresErrorCodes.SerializationFailure, exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("Deterministic #407 rollback failure.", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("Deterministic #407 recovery failure.", exception.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.Segments.CountAsync());
        await AssertOriginalAggregateAsync(seed);
    }

    private async Task<SegmentSeed> SeedAsync()
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        return await support.SeedAsync(customRoute: false);
    }

    private async Task ReplaceAsync(ApplicationDbContext context, SegmentSeed seed, CancellationToken cancellationToken)
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var token = await support.TokenAsync(context, seed);
        await support.Service(context).UpdateSegmentAsync(seed.TripId, seed.SegmentId!.Value, seed.UserId,
            TripEditorSegmentMutationPostgresTestSupport.Body(seed, [seed.AlternateId], [null], token,
                mode: seed.SecondProfileKey, notes: "must roll back"), null, cancellationToken);
    }

    private async Task ReplaceAsync(
        ApplicationDbContext context,
        SegmentSeed seed,
        CancellationToken cancellationToken,
        ISegmentEditorContextRecovery contextRecovery)
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var token = await support.TokenAsync(context, seed);
        await support.Service(context, contextRecovery).UpdateSegmentAsync(
            seed.TripId, seed.SegmentId!.Value, seed.UserId,
            TripEditorSegmentMutationPostgresTestSupport.Body(seed, [seed.AlternateId], [null], token,
                mode: seed.SecondProfileKey, notes: "must roll back"), null, cancellationToken);
    }

    private async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>> ReplaceOutcomeAsync(
        ApplicationDbContext context, SegmentSeed seed)
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var token = await support.TokenAsync(context, seed);
        return await support.Service(context).UpdateSegmentAsync(seed.TripId, seed.SegmentId!.Value, seed.UserId,
            TripEditorSegmentMutationPostgresTestSupport.Body(seed, [seed.AlternateId], [null], token,
                mode: seed.SecondProfileKey, notes: "must roll back"), null, CancellationToken.None);
    }

    private async Task AssertOriginalAggregateAsync(SegmentSeed seed)
    {
        await using var verification = fixture.CreateContext();
        var segment = await TripEditorSegmentMutationPostgresTestSupport.ReadAsync(verification, seed.SegmentId!.Value);
        Assert.Equal([seed.FirstWaypointId, seed.SecondWaypointId], segment.Waypoints.Select(item => item.PlaceId));
        Assert.All(segment.Waypoints, item => Assert.Null(item.RouteVertexIndex));
        Assert.Null(segment.RouteGeometry);
        Assert.Equal(seed.FirstProfileId, segment.TransportProfileId);
        Assert.Equal(EstimatedDurationSource.Manual, segment.EstimatedDurationSource);
        Assert.Equal(TimeSpan.FromMinutes(90), segment.EstimatedDuration);
        Assert.Equal(471.652, segment.EstimatedDistanceKm);
        Assert.Equal("original notes", segment.Notes);
    }

    /// <summary>Proves successful cleanup permits a later unrelated write through the same context.</summary>
    private static async Task AssertContextReusableAsync(ApplicationDbContext context, SegmentSeed seed)
    {
        var profile = await context.Set<TransportProfile>().SingleAsync(item => item.Id == seed.FirstProfileId);
        profile.Label = "context reuse verified";
        await context.SaveChangesAsync();
        Assert.Equal("context reuse verified", await context.Set<TransportProfile>().AsNoTracking()
            .Where(item => item.Id == seed.FirstProfileId).Select(item => item.Label).SingleAsync());
    }

    /// <summary>Captures the exact tracked type, key, state, and scalar values around provider cleanup.</summary>
    private static string[] TrackerSnapshot(ApplicationDbContext context) => context.ChangeTracker.Entries()
        .Select(entry => $"{entry.Metadata.ClrType.Name}|{string.Join(',', entry.Properties.Where(property => property.Metadata.IsPrimaryKey()).Select(property => property.CurrentValue))}|{entry.State}|{string.Join(',', entry.Properties.Select(property => $"{property.Metadata.Name}={property.CurrentValue}"))}")
        .Order(StringComparer.Ordinal)
        .ToArray();

    private sealed class FailurePlan
    {
        internal bool OperationFailed { get; set; }
    }

    private sealed class FailingSaveInterceptor(FailurePlan plan) : SaveChangesInterceptor
    {
        private bool _fail = true;
        internal InvalidOperationException Failure { get; } = new("Deterministic #407 provider failure.");

        /// <summary>Fails the first final save after the destructive replacement has changed tracked state.</summary>
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_fail) return ValueTask.FromResult(result);
            _fail = false;
            plan.OperationFailed = true;
            throw Failure;
        }
    }

    private sealed class SerializationSaveInterceptor(FailurePlan plan) : SaveChangesInterceptor
    {
        private bool _fail = true;

        /// <summary>Injects SQLSTATE 40001 only after the editor has completed its aggregate comparison.</summary>
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_fail) return ValueTask.FromResult(result);
            _fail = false;
            plan.OperationFailed = true;
            return ValueTask.FromException<InterceptionResult<int>>(new PostgresException(
                "serialization", "ERROR", "ERROR", PostgresErrorCodes.SerializationFailure,
                null!, null!, 0, 0, null!, null!, "public", null!, null!, null!,
                null!, "predicate.c", "1", "serialization_failure"));
        }
    }

    private sealed class CancellingSaveInterceptor : SaveChangesInterceptor
    {
        private bool _cancel = true;
        internal TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Waits only on the supplied cancellation token after proving final SaveChanges was entered.</summary>
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_cancel) return result;
            _cancel = false;
            SaveEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return result;
        }
    }

    private sealed class RollbackFailureInterceptor(FailurePlan plan) : DbTransactionInterceptor
    {
        internal InvalidOperationException Failure { get; } = new("Deterministic #407 rollback failure.");

        /// <summary>Fails cleanup rollback only after the deterministic operation failure.</summary>
        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            plan.OperationFailed
                ? ValueTask.FromException<InterceptionResult>(Failure)
                : ValueTask.FromResult(result);
    }

    private sealed class RecoveryFailureInterceptor(FailurePlan plan) : DbCommandInterceptor
    {
        internal InvalidOperationException Failure { get; } = new("Deterministic #407 recovery failure.");

        /// <summary>Fails the first recovery read after rollback without affecting setup or pre-mutation reads.</summary>
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (plan.OperationFailed && command.CommandText.Contains("FROM \"Segments\" AS", StringComparison.Ordinal))
                return ValueTask.FromException<InterceptionResult<DbDataReader>>(
                    Failure);
            return ValueTask.FromResult(result);
        }
    }


    private sealed class FailingContextRecovery(bool failRestore, bool failDispose) : ISegmentEditorContextRecovery
    {
        internal InvalidOperationException RestorationFailure { get; } = new("Deterministic #407 restoration failure.");
        internal InvalidOperationException DisposalFailure { get; } = new("Deterministic #407 disposal failure.");

        /// <summary>Restores normally or injects the exact restoration-seam failure.</summary>
        public void RestoreTracker(
            ApplicationDbContext context,
            TripEditorSegmentMutationService.SegmentEditorTrackerSnapshot snapshot)
        {
            if (failRestore) throw RestorationFailure;
            snapshot.Restore(context);
        }

        /// <summary>Invalidates the real context before optionally injecting disposal failure.</summary>
        public async ValueTask InvalidateAsync(ApplicationDbContext context)
        {
            await context.DisposeAsync();
            if (failDispose) throw DisposalFailure;
        }
    }
}
