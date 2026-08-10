using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wayfarer.Models;

namespace Wayfarer.Services;

public sealed partial class PlaceRegionLifecycleService
{
    /// <summary>Rolls back with a non-cancelled token and detaches only affected lifecycle aggregates.</summary>
    private async Task RecoverAndRethrowAsync(
        Exception original,
        IDbContextTransaction? transaction,
        LifecycleRecoveryScope scope)
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
            DetachAffectedEntries(scope);
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

    private void DetachAffectedEntries(LifecycleRecoveryScope scope)
    {
        foreach (var entry in _dbContext.ChangeTracker.Entries().Where(entry => IsAffected(entry.Entity, scope)).ToArray())
            entry.State = EntityState.Detached;
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
