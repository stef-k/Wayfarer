using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

public sealed partial class PlaceRegionLifecycleService
{
    /// <summary>Obtains a coherent read-only preview; absent canonical destinations remain unpersisted.</summary>
    public async Task<RegionDeletionPreviewDto> PreviewRegionDeletionAsync(Guid tripId, Guid regionId, string userId,
        RegionDeletionPreviewRequestDto choices, CancellationToken token)
    {
        await using var transaction = await BeginTransactionAsync(token, IsolationLevel.RepeatableRead);
        var snapshot = await ReadRegionDeletionAsync(tripId, regionId, userId, choices, Guid.NewGuid(), token);
        return BuildRegionDeletionPreview(snapshot, userId, choices);
    }

    /// <summary>Executes exactly one approved four-way operation, without destructive retry or legacy-token reuse.</summary>
    public async Task<EditorMutationResult<RegionDeletionResultDto>> ConfirmRegionDeletionAsync(
        Guid tripId, Guid regionId, string userId, RegionDeletionRequestDto request, CancellationToken token, Func<CancellationToken, Task<bool>>? authority = null)
    {
        if (string.IsNullOrWhiteSpace(request.ConfirmationToken))
            throw new RegionDeletionRejectedException(409, "region-deletion-confirmation-required", "Review and confirm the current consequences.",
                await PreviewRegionDeletionAsync(tripId, regionId, userId, request, token));
        var approval = _confirmation.ReadRegionDeletion(request.ConfirmationToken);
        if (_confirmation.IsRegionDeletionExpired(approval) || approval.UserId != userId || approval.TripId != tripId
            || approval.SourceId != regionId || approval.Places != request.Places || approval.Areas != request.Areas)
            throw StaleRegionDeletion();
        var candidates = await ReadRegionDeletionAsync(tripId, regionId, userId, request,
            approval.Destination.RegionId ?? Guid.NewGuid(), token);
        var snapshot = LifecycleTrackerSnapshot.Capture(_dbContext);
        var scope = new LifecycleRecoveryScope(candidates.Locks.PlaceIds, candidates.Locks.RegionIds, candidates.Locks.SegmentIds,
            candidates.AreaIds, candidates.Destination.Mode == "create" ? [candidates.Destination.RegionId!.Value] : []);
        var commitStarted = false;
        var trackerIsolated = false;
        await using var transaction = await BeginTransactionAsync(token, IsolationLevel.ReadCommitted);
        try
        {
            await LockRegionDeletionAsync(candidates, token);
            await LockDeletionAuthorityAsync(tripId, userId, authority, token);
            var current = await ReadRegionDeletionAsync(tripId, regionId, userId, request,
                approval.Destination.RegionId ?? Guid.NewGuid(), token);
            if (_confirmation.IsRegionDeletionExpired(approval) || current.Locks.RequiresLocksOutside(candidates.Locks) || current.AreaIds.Except(candidates.AreaIds).Any()
                || current.Destination != approval.Destination || RegionDeletionFingerprint(current) != approval.Fingerprint)
                throw StaleRegionDeletion();

            // Caller-owned pending work cannot be implicitly saved with this operation, nor supply stale scalar inputs.
            foreach (var entry in _dbContext.ChangeTracker.Entries().ToArray()) entry.State = EntityState.Detached;
            trackerIsolated = true;
            var result = await ApplyRegionDeletionAsync(current, userId, request, token);
            // Projection is inside the transaction. A successful envelope is sent only after confirmed commit.
            commitStarted = true;
            if (transaction != null) await transaction.CommitAsync(token);
            RestoreAfterRegionDeletion(scope, snapshot);
            return result;
        }
        catch (Exception original)
        {
            if (trackerIsolated) await RecoverAsync(original, transaction, scope, snapshot);
            else await RecoverUnappliedRegionDeletionAsync(original, transaction);
            if (!commitStarted && IsRegionDeletionContention(original)) throw StaleRegionDeletion();
            throw;
        }
    }

    /// <summary>Locks profiles → Segments → Places → Areas → Regions → Trip, with stable identities in each class.</summary>
    private async Task LockRegionDeletionAsync(RegionDeletionSnapshot snapshot, CancellationToken token)
    {
        if (!_dbContext.Database.IsRelational()) return;
        var locks = snapshot.Locks;
        await SegmentRouteReconciler.LockProfilesAsync(_dbContext, locks.ProfileIds, token);
        foreach (var id in locks.SegmentIds) await SegmentRouteReconciler.LockSegmentAsync(_dbContext, id, token);
        foreach (var id in locks.PlaceIds)
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Places\" WHERE \"Id\" = {id} FOR UPDATE", token);
        foreach (var id in snapshot.AreaIds)
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Areas\" WHERE \"Id\" = {id} FOR UPDATE", token);
        foreach (var id in locks.RegionIds)
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Regions\" WHERE \"Id\" = {id} FOR UPDATE", token);
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Trips\" WHERE \"Id\" = {snapshot.Trip.Id} FOR UPDATE", token);
    }

    /// <summary>Applies only membership/order changes to kept children, and canonical reconciliation to deleted Places.</summary>
    private async Task<EditorMutationResult<RegionDeletionResultDto>> ApplyRegionDeletionAsync(
        RegionDeletionSnapshot s, string userId, RegionDeletionRequestDto request, CancellationToken token)
    {
        _dbContext.Attach(s.Trip);
        _dbContext.AttachRange(s.Regions);
        _dbContext.AttachRange(s.Places.Concat(s.DestinationPlaces));
        _dbContext.AttachRange(s.Areas.Concat(s.DestinationAreas));
        Region? destination = null;
        if (s.Destination.Mode == "existing") destination = s.Regions.Single(r => r.Id == s.Destination.RegionId);
        if (s.Destination.Mode == "create")
        {
            destination = new Region { Id = s.Destination.RegionId!.Value, TripId = s.Trip.Id,
                UserId = userId, Name = "Unassigned Places", DisplayOrder = 0 };
            _dbContext.Regions.Add(destination);
        }
        var movedPlaces = request.Places == "keep" ? s.Places : [];
        var movedAreas = request.Areas == "keep" ? s.Areas : [];
        var orderedPlaces = movedPlaces.Length > 0 ? s.DestinationPlaces.Concat(movedPlaces).ToArray() : [];
        var orderedAreas = movedAreas.Length > 0 ? s.DestinationAreas.Concat(movedAreas).ToArray() : [];
        for (var i = 0; i < orderedPlaces.Length; i++)
        {
            orderedPlaces[i].RegionId = destination!.Id;
            orderedPlaces[i].Region = destination;
            orderedPlaces[i].DisplayOrder = i + 1;
        }
        for (var i = 0; i < orderedAreas.Length; i++)
        {
            orderedAreas[i].RegionId = destination!.Id;
            orderedAreas[i].Region = destination;
            orderedAreas[i].DisplayOrder = i + 1;
        }
        var deletedPlaces = request.Places == "delete" ? s.Places.Select(p => p.Id).ToHashSet() : [];
        var deletedSegments = request.Places == "delete" ? s.EndpointIds.ToHashSet() : [];
        var surviving = request.Places == "delete" ? s.Segments.Where(x => !deletedSegments.Contains(x.Id)).Select(x => x.Id).ToArray() : [];
        var detached = s.Segments.Where(x => surviving.Contains(x.Id)).SelectMany(x => x.Waypoints
            .Where(w => deletedPlaces.Contains(w.PlaceId)).Select(w => new EditorLifecycleWaypointAssociationDto(x.Id, w.PlaceId)))
            .OrderBy(w => w.SegmentId).ThenBy(w => w.PlaceId).ToArray();
        if (deletedPlaces.Count > 0)
        {
            var segments = await LoadAffectedSegmentsAsync(s.Trip.Id, userId, deletedPlaces.ToArray(), token);
            _dbContext.Segments.RemoveRange(segments.Where(x => deletedSegments.Contains(x.Id)));
            foreach (var segment in segments.Where(x => surviving.Contains(x.Id)))
                await ReconcileAfterWaypointDeletionAsync(segment, deletedPlaces, token);
            _dbContext.Places.RemoveRange(s.Places);
        }
        var deletedAreas = request.Areas == "delete" ? s.Areas.Select(a => a.Id).ToArray() : [];
        if (deletedAreas.Length > 0) _dbContext.Areas.RemoveRange(s.Areas);
        _dbContext.Regions.Remove(s.Source);
        if (deletedSegments.Count > 0) await NormalizeSegmentOrdersAsync(s.Trip.Id, userId, deletedSegments, token);
        var regionOrder = s.Regions.Where(r => r.Id != s.Source.Id).Concat(s.Destination.Mode == "create" ? [destination!] : [])
            .OrderBy(r => r.Name == "Unassigned Places" ? 0 : 1).ThenBy(r => r.DisplayOrder).ThenBy(r => r.Id).ToArray();
        var nextOrder = 1;
        foreach (var region in regionOrder) region.DisplayOrder = region.Name == "Unassigned Places" ? 0 : nextOrder++;
        // Save only this operation's locked footprint and current-safe freshness.
        var now = DateTime.UtcNow;
        _dbContext.Entry(s.Trip).Property(t => t.UpdatedAt).CurrentValue = s.Trip.UpdatedAt > now ? s.Trip.UpdatedAt : now;
        _dbContext.Entry(s.Trip).Property(t => t.UpdatedAt).IsModified = true;
        await _dbContext.SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken: token);
        return await ProjectRegionDeletionAsync(s, userId, request, orderedPlaces, orderedAreas, regionOrder,
            deletedPlaces, deletedAreas, deletedSegments, surviving, detached, token);
    }

    /// <summary>Discards lifecycle state after commit and restores only caller work outside the changed aggregates.</summary>
    private void RestoreAfterRegionDeletion(LifecycleRecoveryScope scope, LifecycleTrackerSnapshot snapshot)
    {
        foreach (var entry in _dbContext.ChangeTracker.Entries().ToArray()) entry.State = EntityState.Detached;
        RestoreTracker(scope, new(snapshot.Entries.Where(pair => !IsAffected(pair.Key, scope))
            .ToDictionary(pair => pair.Key, pair => pair.Value, ReferenceEqualityComparer.Instance)));
    }

    private static RegionDeletionRejectedException StaleRegionDeletion() => new(409, "region-deletion-preview-stale",
        "The preview expired or the Trip contents changed. Get a fresh preview and confirm again.");

    private static bool IsRegionDeletionContention(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException!)
        {
            if (current is PostgresException pg && (pg.SqlState is PostgresErrorCodes.DeadlockDetected
                or PostgresErrorCodes.SerializationFailure || pg is { SqlState: PostgresErrorCodes.UniqueViolation,
                    ConstraintName: Wayfarer.Models.Configuration.RegionConfiguration.UnassignedIndex })) return true;
        }
        return false;
    }
}
