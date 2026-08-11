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

    /// <summary>A failed recovery reread after a failed final save retains both failures and all persisted boundaries.</summary>
    [PostgresFact]
    public async Task RecoveryReadFailure_RetainsOriginalAndInvalidatesContextWithoutResidue()
    {
        var support = new TripEditorSegmentMutationPostgresTestSupport(fixture);
        var seed = await support.SeedAsync(includeSegment: false);
        var plan = new FailurePlan();
        var generatedId = Guid.NewGuid();
        var save = new PlannedSaveFailureInterceptor(plan);
        var recovery = new CreateRecoveryReadFailureInterceptor(plan);
        var before = await ReadProviderSnapshotAsync(seed);
        await using var context = fixture.CreateContext(
            save, recovery);

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => support.Service(context, () => generatedId).CreateSegmentAsync(
                seed.TripId, seed.UserId,
                TripEditorSegmentMutationPostgresTestSupport.Body(
                    seed, [seed.FirstWaypointId, seed.SecondWaypointId], [1, 2], null, customRoute: true),
                CancellationToken.None));

        Assert.Collection(exception.InnerExceptions,
            original => Assert.Same(save.Failure, original),
            recoveryFailure => Assert.Same(recovery.Failure, recoveryFailure));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.Segments.CountAsync());
        await using var verification = fixture.CreateContext();
        Assert.False(await verification.Segments.AsNoTracking().AnyAsync(item => item.Id == generatedId));
        Assert.False(await verification.Set<SegmentWaypoint>().AsNoTracking().AnyAsync(item => item.SegmentId == generatedId));
        AssertProviderSnapshotEqual(before, await ReadProviderSnapshotAsync(seed));
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

    private async Task<CreateRecoveryProviderSnapshot> ReadProviderSnapshotAsync(SegmentSeed seed)
    {
        await using var context = fixture.CreateContext();
        var trip = await context.Trips.AsNoTracking().SingleAsync(item => item.Id == seed.TripId);
        var regions = await context.Regions.AsNoTracking().Where(item => item.TripId == seed.TripId)
            .OrderBy(item => item.Id).ToListAsync();
        var regionIds = regions.Select(item => item.Id).ToArray();
        var places = await context.Places.AsNoTracking().Where(item => regionIds.Contains(item.RegionId))
            .OrderBy(item => item.Id).ToListAsync();
        var profiles = await context.Set<TransportProfile>().AsNoTracking()
            .Where(item => item.Id == seed.FirstProfileId || item.Id == seed.SecondProfileId)
            .OrderBy(item => item.Id).ToListAsync();
        var segments = await context.Segments.AsNoTracking().Where(item => item.TripId == seed.TripId)
            .Include(item => item.Waypoints.OrderBy(waypoint => waypoint.Position))
            .OrderBy(item => item.Id).ToListAsync();

        return new(
            new(trip.Id, trip.UserId, trip.Name, trip.Notes, trip.IsPublic, trip.ShareProgressEnabled,
                trip.CenterLat, trip.CenterLon, trip.Zoom, trip.CoverImageUrl, trip.UpdatedAt),
            regions.Select(item => new RegionSnapshot(item.Id, item.UserId, item.TripId, item.Name, item.Notes,
                item.Center?.X, item.Center?.Y, item.Center?.SRID, item.DisplayOrder, item.CoverImageUrl)).ToArray(),
            places.Select(item => new PlaceSnapshot(item.Id, item.UserId, item.RegionId, item.Name, item.Notes,
                item.Location?.X, item.Location?.Y, item.Location?.SRID, item.DisplayOrder,
                item.IconName, item.MarkerColor, item.Address)).ToArray(),
            profiles.Select(item => new ProfileSnapshot(item.Id, item.Key, item.Label, item.Category,
                item.Description, item.PlanningSpeedKmh, item.IsActive, item.IsSeeded, item.SortOrder)).ToArray(),
            segments.Select(item => new SegmentSnapshot(item.Id, item.UserId, item.TripId, item.FromPlaceId,
                item.ToPlaceId, item.TransportProfileId, item.Mode, item.Notes, item.DisplayOrder,
                item.RouteGeometry?.SRID, item.RouteGeometry?.Coordinates.Select(coordinate => $"{coordinate.X:R},{coordinate.Y:R}").ToArray() ?? [],
                item.EstimatedDistanceKm, item.EstimatedDuration, item.EstimatedDurationSource, item.RowVersion,
                item.Waypoints.Select(waypoint => new WaypointSnapshot(waypoint.SegmentId, waypoint.PlaceId,
                    waypoint.Position, waypoint.RouteVertexIndex)).ToArray())).ToArray(),
            regions.Count, places.Count, segments.Count, segments.Sum(item => item.Waypoints.Count));
    }

    private static void AssertProviderSnapshotEqual(
        CreateRecoveryProviderSnapshot expected, CreateRecoveryProviderSnapshot actual)
    {
        Assert.Equal(expected.Trip, actual.Trip);
        Assert.Equal(expected.RegionSnapshots, actual.RegionSnapshots);
        Assert.Equal(expected.PlaceSnapshots, actual.PlaceSnapshots);
        Assert.Equal(expected.ProfileSnapshots, actual.ProfileSnapshots);
        Assert.Equal(expected.RegionCount, actual.RegionCount);
        Assert.Equal(expected.PlaceCount, actual.PlaceCount);
        Assert.Equal(expected.SegmentCount, actual.SegmentCount);
        Assert.Equal(expected.WaypointCount, actual.WaypointCount);
        Assert.Equal(expected.SegmentSnapshots.Length, actual.SegmentSnapshots.Length);
        for (var index = 0; index < expected.SegmentSnapshots.Length; index++)
        {
            var before = expected.SegmentSnapshots[index];
            var after = actual.SegmentSnapshots[index];
            Assert.Equal(before with { RouteCoordinates = [], WaypointSnapshots = [] },
                after with { RouteCoordinates = [], WaypointSnapshots = [] });
            Assert.Equal(before.RouteCoordinates, after.RouteCoordinates);
            Assert.Equal(before.WaypointSnapshots, after.WaypointSnapshots);
        }
    }

    private sealed record CreateRecoveryProviderSnapshot(
        TripSnapshot Trip,
        RegionSnapshot[] RegionSnapshots,
        PlaceSnapshot[] PlaceSnapshots,
        ProfileSnapshot[] ProfileSnapshots,
        SegmentSnapshot[] SegmentSnapshots,
        int RegionCount,
        int PlaceCount,
        int SegmentCount,
        int WaypointCount);

    private sealed record TripSnapshot(
        Guid Id, string UserId, string Name, string? Notes, bool IsPublic, bool ShareProgressEnabled,
        double? CenterLat, double? CenterLon, int? Zoom, string? CoverImageUrl, DateTime UpdatedAt);

    private sealed record RegionSnapshot(
        Guid Id, string UserId, Guid TripId, string Name, string? Notes,
        double? CenterLongitude, double? CenterLatitude, int? CenterSrid, int DisplayOrder, string? CoverImageUrl);

    private sealed record PlaceSnapshot(
        Guid Id, string UserId, Guid RegionId, string Name, string? Notes,
        double? Longitude, double? Latitude, int? LocationSrid, int? DisplayOrder,
        string? IconName, string? MarkerColor, string? Address);

    private sealed record ProfileSnapshot(
        Guid Id, string Key, string Label, string Category, string? Description,
        double? PlanningSpeedKmh, bool IsActive, bool IsSeeded, int SortOrder);

    private sealed record SegmentSnapshot(
        Guid Id, string UserId, Guid TripId, Guid? FromPlaceId, Guid? ToPlaceId,
        Guid? TransportProfileId, string Mode, string? Notes, int DisplayOrder, int? RouteSrid,
        string[] RouteCoordinates, double? EstimatedDistanceKm, TimeSpan? EstimatedDuration,
        EstimatedDurationSource EstimatedDurationSource, uint RowVersion, WaypointSnapshot[] WaypointSnapshots);

    private sealed record WaypointSnapshot(Guid SegmentId, Guid PlaceId, int Position, int? RouteVertexIndex);

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
        internal InvalidOperationException Failure { get; } = new("Deterministic create save failure.");

        /// <summary>Arms rollback failure after preserving the original operation failure.</summary>
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            plan.Failed = true;
            return ValueTask.FromException<InterceptionResult<int>>(Failure);
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


    private sealed class CreateRecoveryReadFailureInterceptor(FailurePlan plan) : DbCommandInterceptor
    {
        internal InvalidOperationException Failure { get; } = new("Deterministic create recovery-read failure.");

        /// <summary>Fails only the generated-ID reload after the final create save has failed.</summary>
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (plan.Failed && command.CommandText.Contains("FROM \"Segments\" AS", StringComparison.Ordinal))
                return ValueTask.FromException<InterceptionResult<DbDataReader>>(
                    Failure);
            return ValueTask.FromResult(result);
        }
    }
}
