using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Executes editor-owned create transaction and cleanup boundaries against PostgreSQL.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class TripEditorSegmentCreatePostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Editor create reaches exactly one final save inside one Serializable transaction.</summary>
    [PostgresFact]
    public async Task Success_UsesOneSerializableSaveAndReturnsCanonicalAggregate()
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(includeSegment: false);
        var recorder = new FinalSaveRecorder();
        await using var context = fixture.CreateContext(recorder);

        var outcome = await CreateAsync(context, support, seed, CancellationToken.None);

        Assert.Equal(EditorRegionMutationStatus.Success, outcome.Status);
        Assert.Equal(1, recorder.SaveCount);
        Assert.Equal(IsolationLevel.Serializable, recorder.IsolationLevel);
        var stored = await TripEditorSegmentMutationPostgresTestSupport.ReadAsync(context, outcome.Result!.Data.Id);
        Assert.Equal([seed.FirstWaypointId, seed.SecondWaypointId], stored.Waypoints.Select(item => item.PlaceId));
        Assert.Equal([1, 2], stored.Waypoints.Select(item => item.RouteVertexIndex));
        Assert.NotNull(stored.RouteGeometry);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Result.Data.AggregateConcurrencyToken));
    }

    /// <summary>A provider failure during canonical loading leaves no attempted aggregate.</summary>
    [PostgresFact]
    public async Task ProviderFailureBeforeSave_PersistsNothing()
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(includeSegment: false);
        await using var context = fixture.CreateContext(new CanonicalTripFailureInterceptor());

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAsync(context, support, seed, CancellationToken.None));

        await AssertNoSegmentsAsync(seed);
    }

    /// <summary>A provider failure at the only save rolls back the complete attempted aggregate.</summary>
    [PostgresFact]
    public async Task ProviderFailureDuringSave_PersistsNothing()
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(includeSegment: false);
        await using var context = fixture.CreateContext(new FinalSaveFailureInterceptor());

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAsync(context, support, seed, CancellationToken.None));

        await AssertNoSegmentsAsync(seed);
    }

    /// <summary>Cancellation at the only save rolls back the complete attempted aggregate.</summary>
    [PostgresFact]
    public async Task CancellationDuringSave_PersistsNothing()
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(includeSegment: false);
        using var cancellation = new CancellationTokenSource();
        var interceptor = new CancellingFinalSaveInterceptor(cancellation);
        await using var context = fixture.CreateContext(interceptor);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateAsync(context, support, seed, cancellation.Token));

        await AssertNoSegmentsAsync(seed);
    }

    /// <summary>A rollback failure retains the create failure and invalidates the unsafe context.</summary>
    [PostgresFact]
    public async Task CleanupFailure_RetainsOriginalAndInvalidatesContext()
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(includeSegment: false);
        var plan = new FailurePlan();
        await using var context = fixture.CreateContext(new PlannedSaveFailureInterceptor(plan), new PlannedRollbackFailureInterceptor(plan));

        var exception = await Assert.ThrowsAsync<AggregateException>(() => CreateAsync(context, support, seed, CancellationToken.None));

        Assert.Contains("Deterministic create save failure.", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("Deterministic create rollback failure.", exception.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.Segments.CountAsync());
        await AssertNoSegmentsAsync(seed);
    }

    /// <summary>An application-generated ID collision returns the bounded create conflict without partial state.</summary>
    [PostgresFact]
    public async Task GeneratedIdCollision_ReturnsWriteConflictWithoutPartialState()
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync();
        var collidingId = seed.SegmentId!.Value;
        await using var context = fixture.CreateContext();
        var before = await context.Segments.AsNoTracking().CountAsync(item => item.TripId == seed.TripId);

        var outcome = await support.Service(context, () => collidingId).CreateSegmentAsync(
            seed.TripId, seed.UserId,
            TripEditorSegmentMutationPostgresTestSupport.Body(
                seed, [seed.AlternateId], [1], null, customRoute: true,
                route: [(0, 0), (1.5, 1.5), (3, 3)]), CancellationToken.None);

        Assert.Equal(EditorRegionMutationStatus.Conflict, outcome.Status);
        Assert.Equal("segment-write-conflict", Assert.IsType<EditorSegmentConflictDto>(outcome.Conflict).Code);
        await using var verification = fixture.CreateContext();
        Assert.Equal(before, await verification.Segments.AsNoTracking().CountAsync(item => item.TripId == seed.TripId));
        var original = await TripEditorSegmentMutationPostgresTestSupport.ReadAsync(verification, collidingId);
        Assert.Equal([seed.FirstWaypointId, seed.SecondWaypointId], original.Waypoints.Select(item => item.PlaceId));
        Assert.Equal("original notes", original.Notes);
    }

    private static Task<EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>> CreateAsync(
        ApplicationDbContext context,
        TripEditorSegmentMutationPostgresTestSupport support,
        SegmentSeed seed,
        CancellationToken cancellationToken) => support.Service(context).CreateSegmentAsync(
            seed.TripId, seed.UserId,
            TripEditorSegmentMutationPostgresTestSupport.Body(
                seed, [seed.FirstWaypointId, seed.SecondWaypointId], [1, 2], null, customRoute: true),
            cancellationToken);

    private async Task AssertNoSegmentsAsync(SegmentSeed seed)
    {
        await using var verification = fixture.CreateContext();
        Assert.Empty(await verification.Segments.AsNoTracking().Where(item => item.TripId == seed.TripId).ToListAsync());
        Assert.Empty(await verification.Set<SegmentWaypoint>().AsNoTracking()
            .Where(item => verification.Segments.Where(segment => segment.TripId == seed.TripId)
                .Select(segment => segment.Id).Contains(item.SegmentId)).ToListAsync());
    }

    private sealed class FinalSaveRecorder : SaveChangesInterceptor
    {
        internal int SaveCount { get; private set; }
        internal IsolationLevel? IsolationLevel { get; private set; }

        /// <summary>Records the sole final save and its active relational transaction.</summary>
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            IsolationLevel = eventData.Context!.Database.CurrentTransaction!.GetDbTransaction().IsolationLevel;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CanonicalTripFailureInterceptor : DbCommandInterceptor
    {
        private int _tripReads;

        /// <summary>Fails the canonical in-transaction Trip reload, after ownership discovery has succeeded.</summary>
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"Trips\"", StringComparison.Ordinal) && ++_tripReads == 2)
                return ValueTask.FromException<InterceptionResult<DbDataReader>>(
                    new InvalidOperationException("Deterministic create canonical-load failure."));
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FinalSaveFailureInterceptor : SaveChangesInterceptor
    {
        /// <summary>Fails the editor's only aggregate save.</summary>
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult<int>>(new InvalidOperationException("Deterministic create save failure."));
    }

    private sealed class CancellingFinalSaveInterceptor(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        /// <summary>Cancels through the request token at the editor's only aggregate save.</summary>
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailurePlan { internal bool Failed { get; set; } }

    private sealed class PlannedSaveFailureInterceptor(FailurePlan plan) : SaveChangesInterceptor
    {
        /// <summary>Arms rollback failure after preserving the original operation failure.</summary>
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            plan.Failed = true;
            return ValueTask.FromException<InterceptionResult<int>>(new InvalidOperationException("Deterministic create save failure."));
        }
    }

    private sealed class PlannedRollbackFailureInterceptor(FailurePlan plan) : DbTransactionInterceptor
    {
        /// <summary>Fails mandatory rollback only after the create operation has failed.</summary>
        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default) => plan.Failed
            ? ValueTask.FromException<InterceptionResult>(new InvalidOperationException("Deterministic create rollback failure."))
            : ValueTask.FromResult(result);
    }
}
