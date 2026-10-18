using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Wayfarer.Models;

namespace Wayfarer.Services;

public sealed partial class PlaceRegionLifecycleService
{
    /// <summary>Rolls back with a non-cancelled token and detaches only affected lifecycle aggregates.</summary>
    private async Task RecoverAndRethrowAsync(
        Exception original,
        IDbContextTransaction? transaction,
        LifecycleRecoveryScope scope,
        LifecycleTrackerSnapshot trackerSnapshot)
    {
        await RecoverAsync(original, transaction, scope, trackerSnapshot);
        ExceptionDispatchInfo.Capture(original).Throw();
    }

    /// <summary>Performs mandatory non-cancelled cleanup and returns only when the context is coherent.</summary>
    private async Task RecoverAsync(
        Exception original,
        IDbContextTransaction? transaction,
        LifecycleRecoveryScope scope,
        LifecycleTrackerSnapshot trackerSnapshot)
    {
        var cleanupFailures = new List<Exception>();
        if (transaction != null)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception cleanupFailure)
            {
                cleanupFailures.Add(cleanupFailure);
            }
        }

        try
        {
            await VerifyRecoveredRowsAsync(scope);
            RestoreTracker(scope, trackerSnapshot);
        }
        catch (Exception cleanupFailure)
        {
            cleanupFailures.Add(cleanupFailure);
        }

        if (cleanupFailures.Count > 0)
        {
            try
            {
                await _dbContext.DisposeAsync();
            }
            catch (Exception cleanupFailure)
            {
                cleanupFailures.Add(cleanupFailure);
            }
            throw new AggregateException([original, .. cleanupFailures]);
        }
    }

    /// <summary>Verifies rollback restored every lifecycle-owned canonical row before tracker reuse.</summary>
    private async Task VerifyRecoveredRowsAsync(LifecycleRecoveryScope scope)
    {
        var placeCount = await _dbContext.Places.AsNoTracking().TagWith("LIFECYCLE RECOVERY")
            .CountAsync(item => scope.PlaceIds.Contains(item.Id), CancellationToken.None);
        var regionCount = await _dbContext.Regions.AsNoTracking().TagWith("LIFECYCLE RECOVERY")
            .CountAsync(item => scope.RegionIds.Contains(item.Id), CancellationToken.None);
        var segmentCount = await _dbContext.Segments.AsNoTracking().TagWith("LIFECYCLE RECOVERY")
            .CountAsync(item => scope.SegmentIds.Contains(item.Id), CancellationToken.None);
        if (placeCount != scope.PlaceIds.Distinct().Count()
            || regionCount != scope.RegionIds.Distinct().Count()
            || segmentCount != scope.SegmentIds.Distinct().Count())
        {
            throw new InvalidOperationException("Lifecycle rollback did not restore every affected canonical row.");
        }
    }

    /// <summary>Detaches lifecycle-created entries before restoring caller instances so fixup cannot undo restoration.</summary>
    private void RestoreTracker(LifecycleRecoveryScope scope, LifecycleTrackerSnapshot trackerSnapshot)
    {
        // Detaching a loaded principal can detach its dependents, including caller-owned entries.
        // Complete that relationship fixup before deciding which original instances need reattaching.
        foreach (var entry in _dbContext.ChangeTracker.Entries().ToArray())
        {
            if (!trackerSnapshot.Entries.ContainsKey(entry.Entity)
                && (entry.State != EntityState.Unchanged || IsAffected(entry.Entity, scope)))
                entry.State = EntityState.Detached;
        }

        var restored = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var entry in _dbContext.ChangeTracker.Entries().ToArray())
        {
            if (trackerSnapshot.Entries.TryGetValue(entry.Entity, out var original))
            {
                RestoreEntry(entry, original);
                restored.Add(entry.Entity);
            }
        }

        foreach (var pair in trackerSnapshot.Entries.Where(pair => !restored.Contains(pair.Key)))
        {
            var replacement = _dbContext.ChangeTracker.Entries()
                .FirstOrDefault(entry => HasSamePrimaryKey(entry, pair.Key, pair.Value.CurrentValues));
            if (replacement != null) replacement.State = EntityState.Detached;
            var entry = _dbContext.Entry(pair.Key);
            RestoreEntry(entry, pair.Value);
        }
    }

    /// <summary>Restores concurrency originals and caller intent after any accepted-but-rolled-back save.</summary>
    private static void RestoreEntry(EntityEntry entry, LifecycleTrackedEntrySnapshot snapshot)
    {
        // Finish Unchanged's acceptance/rejection before restoring concurrency originals.
        entry.State = EntityState.Unchanged;
        entry.OriginalValues.SetValues(snapshot.OriginalValues);
        // Restore state before current scalars so an Unchanged transition cannot reject those values.
        entry.State = snapshot.State;
        entry.CurrentValues.SetValues(snapshot.CurrentValues);
    }

    private static bool HasSamePrimaryKey(EntityEntry entry, object entity, PropertyValues values)
    {
        if (entry.Entity.GetType() != entity.GetType()) return false;
        var key = entry.Metadata.FindPrimaryKey();
        return key != null && key.Properties.All(property =>
            Equals(entry.Property(property.Name).CurrentValue, values[property.Name]));
    }

    private static bool IsAffected(object entity, LifecycleRecoveryScope scope) => entity switch
    {
        Place place => scope.PlaceIds.Contains(place.Id),
        Region region => scope.RegionIds.Contains(region.Id),
        Segment segment => scope.SegmentIds.Contains(segment.Id),
        SegmentWaypoint waypoint => scope.SegmentIds.Contains(waypoint.SegmentId) || scope.PlaceIds.Contains(waypoint.PlaceId),
        _ => false
    };
}

/// <summary>Exact aggregate identities permitted to change during lifecycle cleanup.</summary>
internal sealed record LifecycleRecoveryScope(
    IReadOnlyList<Guid> PlaceIds,
    IReadOnlyList<Guid> RegionIds,
    IReadOnlyList<Guid> SegmentIds);

/// <summary>Exact pre-operation tracker values used to preserve caller-owned state during recovery.</summary>
internal sealed record LifecycleTrackerSnapshot(
    IReadOnlyDictionary<object, LifecycleTrackedEntrySnapshot> Entries)
{
    /// <summary>Captures identity, original/current scalar values, and state before lifecycle work begins.</summary>
    internal static LifecycleTrackerSnapshot Capture(DbContext context) => new(
        context.ChangeTracker.Entries().ToDictionary(
            entry => entry.Entity,
            entry => new LifecycleTrackedEntrySnapshot(entry.State, entry.OriginalValues.Clone(), entry.CurrentValues.Clone()),
            ReferenceEqualityComparer.Instance));
}

/// <summary>One caller-owned tracked entry as it existed before lifecycle orchestration.</summary>
internal sealed record LifecycleTrackedEntrySnapshot(EntityState State, PropertyValues OriginalValues, PropertyValues CurrentValues);
