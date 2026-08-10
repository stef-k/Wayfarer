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

        ExceptionDispatchInfo.Capture(original).Throw();
    }

    private void RestoreTracker(LifecycleRecoveryScope scope, LifecycleTrackerSnapshot trackerSnapshot)
    {
        foreach (var entry in _dbContext.ChangeTracker.Entries().ToArray())
        {
            if (trackerSnapshot.Entries.TryGetValue(entry.Entity, out var original))
            {
                entry.CurrentValues.SetValues(original.Values);
                entry.State = original.State;
                continue;
            }

            if (entry.State != EntityState.Unchanged || IsAffected(entry.Entity, scope))
                entry.State = EntityState.Detached;
        }
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
    /// <summary>Captures entity identity, scalar values, and state before lifecycle work begins.</summary>
    internal static LifecycleTrackerSnapshot Capture(DbContext context) => new(
        context.ChangeTracker.Entries().ToDictionary(
            entry => entry.Entity,
            entry => new LifecycleTrackedEntrySnapshot(entry.State, entry.CurrentValues.Clone()),
            ReferenceEqualityComparer.Instance));
}

/// <summary>One caller-owned tracked entry as it existed before lifecycle orchestration.</summary>
internal sealed record LifecycleTrackedEntrySnapshot(EntityState State, PropertyValues Values);
