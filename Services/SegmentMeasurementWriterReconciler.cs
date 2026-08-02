using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Reconciles current zero-waypoint clone and import writers inside their caller-owned transaction.</summary>
public static class SegmentMeasurementWriterReconciler
{
    /// <summary>Locks and reconciles every current zero-waypoint Segment for one already-persisted trip.</summary>
    public static async Task ReconcileTripAsync(
        ApplicationDbContext dbContext,
        Guid tripId,
        bool allowUnavailableAutomatic,
        CancellationToken cancellationToken = default)
    {
        var segmentIds = await dbContext.Segments.AsNoTracking()
            .Where(segment => segment.TripId == tripId && !segment.Waypoints.Any())
            .OrderBy(segment => segment.Id).Select(segment => segment.Id).ToArrayAsync(cancellationToken);
        var profileIds = await dbContext.Segments.AsNoTracking()
            .Where(segment => segmentIds.Contains(segment.Id) && segment.TransportProfileId != null)
            .Select(segment => segment.TransportProfileId!.Value).Distinct().OrderBy(id => id).ToArrayAsync(cancellationToken);
        if (dbContext.Database.IsRelational())
        {
            await SegmentRouteReconciler.LockProfilesAsync(dbContext, profileIds, cancellationToken);
            foreach (var segmentId in segmentIds)
                await SegmentRouteReconciler.LockSegmentAsync(dbContext, segmentId, cancellationToken);
        }

        foreach (var segmentId in segmentIds)
        {
            var segment = await SegmentRouteReconciler.LoadAggregateAsync(dbContext, segmentId, cancellationToken)
                ?? throw new InvalidOperationException("A Segment changed while measurements were being reconciled.");
            var proposal = new SegmentRouteProposal(
                segment.Id, segment.FromPlaceId, segment.ToPlaceId, [], segment.RouteGeometry,
                new(segment.Mode, segment.TransportProfileId, segment.EstimatedDurationSource,
                    segment.EstimatedDuration?.TotalMinutes, allowUnavailableAutomatic));
            var result = await SegmentRouteReconciler.ReconcileLockedAsync(
                dbContext, proposal, refreshCanonicalState: dbContext.Database.IsRelational(), cancellationToken);
            if (!result.Succeeded)
                throw new InvalidOperationException(string.Join(" ", result.Errors));
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
