using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services.TripMetadataReceipts;

namespace Wayfarer.Services;

internal sealed partial class TripMetadataMutationService
{
    /// <summary>Applies one operation-specific payload under the shared receipt transaction.</summary>
    private async Task<TripMetadataExecution> ApplyControlAsync(Trip trip, TripMetadataIdentity identity, object request,
        Func<Task> readmission, CancellationToken token)
    {
        if (request is TripSavedViewChange view) return new(null, await ApplySavedViewAsync(trip, view, token));
        if (request is bool sharing) return new(null, await ApplySharingAsync(trip, sharing, token));

        var tags = new TripTagService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<TripTagService>.Instance);
        try
        {
            var result = await tags.ApplyLockedAsync(trip.Id, trip.UserId, identity.Kind, (TripTagChange)request, readmission, token);
            return new(null, Success(result.Applied) with { TagResult = result });
        }
        catch (ValidationException error) { throw TripMetadataConditionalRequest.Invalid("tags", error.Message); }
    }

    /// <summary>Publication and sharing are read and written under the same Trip lock.</summary>
    private async Task<TripMetadataEvidence> ApplySharingAsync(Trip trip, bool enabled, CancellationToken token)
    {
        if (enabled && !trip.IsPublic)
            throw new TripMetadataMutationRejected(400, "share-progress-requires-public", "Share progress can only be enabled for public Trips.");
        var updated = NextTimestamp(trip.UpdatedAt);
        var applied = trip.ShareProgressEnabled != enabled || updated != trip.UpdatedAt;
        if (applied)
            await db.Trips.Where(t => t.Id == trip.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.ShareProgressEnabled, enabled).SetProperty(t => t.UpdatedAt, updated), token);
        return Success(applied) with { UpdatedAtUtc = updated, SharingResult = new(trip.IsPublic, enabled) };
    }

    /// <summary>Compares and writes the complete tuple, including partial legacy originals.</summary>
    private async Task<TripMetadataEvidence> ApplySavedViewAsync(Trip trip, TripSavedViewChange request, CancellationToken token)
    {
        var current = new TripSavedView(new(trip.CenterLat, trip.CenterLon), trip.Zoom);
        if (current != request.Expected)
            throw new TripMetadataMutationRejected(409, "mutation-base-stale", "The saved Trip view changed.");
        var applied = current != request.Value;
        var updated = applied ? NextTimestamp(trip.UpdatedAt) : trip.UpdatedAt;
        if (applied)
            await db.Trips.Where(t => t.Id == trip.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.CenterLat, request.Value.Center.Latitude)
                .SetProperty(t => t.CenterLon, request.Value.Center.Longitude)
                .SetProperty(t => t.Zoom, request.Value.Zoom).SetProperty(t => t.UpdatedAt, updated), token);
        return Success(applied) with { UpdatedAtUtc = updated, SavedView = request.Value };
    }

    /// <summary>Preserves monotone freshness at PostgreSQL's stored precision.</summary>
    private static DateTime NextTimestamp(DateTime previous)
    {
        var now = DateTime.UtcNow;
        var stamp = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        return previous > stamp ? previous : stamp;
    }

    /// <summary>Constructs a terminal success with no remaining possible effects.</summary>
    private static TripMetadataEvidence Success(bool applied)
    {
        var state = applied ? "applied" : "checkedNoOp";
        return new(state, state, false);
    }
}
