using System.Data;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

public sealed partial class PlaceRegionLifecycleService
{
    /// <summary>Nonmutating, coherent content preview even when no dependent rows exist.</summary>
    public async Task<PlaceDeletionPreviewDto> PreviewPlaceDeletionAsync(Guid tripId, Guid placeId, string userId, CancellationToken token)
    {
        await using var transaction = await BeginTransactionAsync(token, IsolationLevel.RepeatableRead);
        return BuildPlaceDeletionPreview(await ReadPlaceDeletionAsync(tripId, placeId, userId, token), userId);
    }

    /// <summary>One locked compare/delete/reconcile transaction; no automatic retry or ambiguous success.</summary>
    public async Task<EditorMutationResult<PlaceDeletionResultDto>> ConfirmPlaceDeletionAsync(Guid tripId, Guid placeId,
        string userId, string? confirmationToken, CancellationToken token, Func<CancellationToken, Task<bool>>? authority = null)
    {
        if (string.IsNullOrWhiteSpace(confirmationToken))
            throw new TripMetadataMutationRejected(409, "place-deletion-confirmation-required", "Review the current deletion preview and explicitly confirm.");
        var approval = _confirmation.ReadPlaceDeletion(confirmationToken);
        if (approval.UserId != userId || approval.TripId != tripId || approval.PlaceId != placeId || _confirmation.IsPlaceDeletionExpired(approval)) throw StalePlaceDeletion();
        var candidates = await ReadPlaceDeletionAsync(tripId, placeId, userId, token);
        var tracker = LifecycleTrackerSnapshot.Capture(_dbContext);
        var scope = new LifecycleRecoveryScope(candidates.Locks.PlaceIds, candidates.Locks.RegionIds, candidates.Locks.SegmentIds);
        var isolated = false;
        var commitStarted = false;
        await using var transaction = await BeginTransactionAsync(token, IsolationLevel.ReadCommitted);
        try
        {
            await LockAsync(candidates.Locks, token);
            await LockDeletionAuthorityAsync(tripId, userId, authority, token);
            var current = await ReadPlaceDeletionAsync(tripId, placeId, userId, token);
            if (current.Locks.RequiresLocksOutside(candidates.Locks) || _confirmation.IsPlaceDeletionExpired(approval)
                || PlaceDeletionFingerprint(current) != approval.Fingerprint) throw StalePlaceDeletion();
            foreach (var entry in _dbContext.ChangeTracker.Entries().ToArray()) entry.State = EntityState.Detached;
            isolated = true;
            var result = await ApplyPlaceDeletionAsync(current, userId, token);
            commitStarted = true;
            if (transaction != null) await transaction.CommitAsync(token);
            RestoreAfterRegionDeletion(scope, tracker);
            return result;
        }
        catch (Exception original)
        {
            if (isolated) await RecoverAsync(original, transaction, scope, tracker);
            else await RecoverUnappliedRegionDeletionAsync(original, transaction);
            if (!commitStarted && IsPlaceStructureContention(original)) throw StalePlaceDeletion();
            throw;
        }
    }

    /// <summary>Retains lifecycle lock classes, then holds current Trip, active account and incoming bearer authority.</summary>
    private async Task LockDeletionAuthorityAsync(Guid tripId, string userId, Func<CancellationToken, Task<bool>>? authority, CancellationToken token)
    {
        if (_dbContext.Database.IsRelational())
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Trips\" WHERE \"Id\" = {tripId} FOR UPDATE", token);
            if (!await _dbContext.Users.FromSqlInterpolated($"SELECT * FROM public.\"AspNetUsers\" WHERE \"Id\" = {userId} AND \"IsActive\" FOR SHARE").AsNoTracking().AnyAsync(token))
                throw new TripMetadataMutationRejected(401, "mutation-authority-unavailable", "Authentication is no longer valid.");
        }
        if (authority != null && !await authority(token))
            throw new TripMetadataMutationRejected(401, "mutation-authority-unavailable", "Authentication is no longer valid.");
    }

    /// <summary>Reuses canonical route/order/visit behavior and projects only the committed affected scope.</summary>
    private async Task<EditorMutationResult<PlaceDeletionResultDto>> ApplyPlaceDeletionAsync(PlaceDeletionSnapshot s, string userId, CancellationToken token)
    {
        // Load tracked instances once; no stale caller state or duplicate anchor instance may supply writes.
        var place = (await LoadOwnedPlaceAsync(s.Trip.Id, s.Place.Id, userId, token))!;
        var affected = await LoadAffectedSegmentsAsync(s.Trip.Id, userId, [place.Id], token);
        var endpointIds = s.EndpointIds.ToHashSet();
        _dbContext.Segments.RemoveRange(affected.Where(x => endpointIds.Contains(x.Id)));
        foreach (var segment in affected.Where(x => !endpointIds.Contains(x.Id)))
            await ReconcileAfterWaypointDeletionAsync(segment, new HashSet<Guid> { place.Id }, token);
        _dbContext.Places.Remove(place);
        await NormalizePlaceOrdersAsync(place.RegionId, place.Id, token);
        if (endpointIds.Count > 0) await NormalizeSegmentOrdersAsync(s.Trip.Id, userId, endpointIds, token);
        // The explicit save avoids incidental writes from timestamp traversal; locked current freshness never moves backwards.
        await _dbContext.SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken: token);
        var now = DateTime.UtcNow;
        s.Trip.UpdatedAt = s.Trip.UpdatedAt > now ? s.Trip.UpdatedAt : now;
        await _dbContext.Trips.Where(t => t.Id == s.Trip.Id && t.UserId == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.UpdatedAt, t => t.UpdatedAt > now ? t.UpdatedAt : now), token);
        var reader = new TripEditorPlaceMutationReader(_dbContext, _confirmation.RegionDeletionAggregateTokens);
        var siblingIds = s.Siblings.Where(p => p.Id != place.Id).Select(p => p.Id).ToArray();
        var places = await reader.LoadPlaceDtosAsync(siblingIds, s.Trip.Id, userId, token);
        var segmentIds = endpointIds.Count > 0 ? s.SegmentOrders.Where(x => !endpointIds.Contains(x.Id)).Select(x => x.Id).ToArray() : s.Survivors.Select(x => x.Id).ToArray();
        var segments = await reader.LoadSegmentDtosAsync(segmentIds, s.Trip.Id, userId, token);
        var slices = await reader.BuildAffectedAsync(s.Trip.Id, userId, places, [place.RegionId], segments, null, true, token);
        if (endpointIds.Count > 0) slices = slices with { SegmentOrder = await _dbContext.Segments.AsNoTracking()
            .Where(x => x.TripId == s.Trip.Id && x.UserId == userId).OrderBy(x => x.DisplayOrder).ThenBy(x => x.Id).Select(x => x.Id).ToArrayAsync(token) };
        slices = slices with { Metadata = EditorTripStateMapper.ToMetadata(s.Trip, s.Trip.IsPublic ? $"/Public/Trips/{s.Trip.Id}" : null,
            s.Trip.IsPublic && s.Trip.ShareProgressEnabled ? $"/Public/Trips/{s.Trip.Id}?progress=1" : null) };
        return new(true, new(s.Trip.Id, place.Id, place.RegionId, s.Survivors.Select(x => x.Id).Order().ToArray(), s.Detached), slices,
            new([], [place.Id], [], endpointIds.Order().ToArray(), []), []);
    }

    private static TripMetadataMutationRejected StalePlaceDeletion() => new(409, "place-deletion-preview-stale",
        "The preview expired or the Place, ordering or dependencies changed. Review a fresh preview and confirm again.");
}
