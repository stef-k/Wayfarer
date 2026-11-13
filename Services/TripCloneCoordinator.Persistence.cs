using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services;

public sealed partial class TripCloneCoordinator
{
    /// <summary>Flushes anchor dependencies before the shared route core queries storage, inside the caller's transaction.</summary>
    internal async Task PersistConstructedAsync(Trip trip, CancellationToken token)
    {
        var segments = trip.Segments.ToArray();
        trip.Segments.Clear();
        dbContext.Trips.Add(trip);
        await dbContext.SaveChangesAsync(token);
        foreach (var source in segments)
        {
            var proposal = new SegmentRouteProposal(source.Id, source.FromPlaceId, source.ToPlaceId,
                source.Waypoints.OrderBy(w => w.Position).Select(w => new SegmentWaypointProposal(w.PlaceId, w.Position, w.RouteVertexIndex)).ToArray(),
                source.RouteGeometry, new(source.Mode, source.TransportProfileId, source.EstimatedDurationSource,
                    source.EstimatedDuration?.TotalMinutes, true), true, source.Notes, true, source.Title)
            {
                PreservedMeasurements = new(source.EstimatedDistanceKm, source.EstimatedDuration, source.EstimatedDurationSource)
            };
            var result = await SegmentRouteReconciler.ReconcileNewLockedAsync(dbContext,
                new(source.Id, source.UserId, trip.Id, source.DisplayOrder), proposal, token);
            if (!result.Succeeded) throw new InvalidOperationException(string.Join(" ", result.Errors));
            var added = dbContext.Segments.Local.Single(s => s.Id == source.Id);
            // Copy fidelity retains normalized null Notes; ordinary new routes default them to empty.
            added.Notes = source.Notes;
            added.Planning = source.Planning;
            SegmentRouteProvenance.Capture(source)?.ApplyTo(added);
            await dbContext.SaveChangesAsync(token);
        }
        // Every caller-owned row must be flushed before clearing; no helper commits the transaction.
        dbContext.ChangeTracker.Clear();
    }
}
