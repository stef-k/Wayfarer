using System.Data;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

/// <summary>PostgreSQL transaction, lock, and recovery orchestration for editor Segment updates.</summary>
public sealed partial class TripEditorSegmentMutationService
{
    private async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>> UpdateRelationalAsync(
        Trip candidateTrip, Segment candidateSegment, EditorSegmentSaveRequest request, string userId,
        string? confirmationToken, CancellationToken cancellationToken, int lockAttempt = 1)
    {
        if (!_aggregateTokens.TryRead(request.AggregateConcurrencyToken, userId, candidateTrip.Id, candidateSegment.Id, out var submittedVersion))
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(
                new() { ["aggregateConcurrencyToken"] = ["The aggregate token is missing, malformed, or scoped to another Segment."] },
                "segment-aggregate-token-invalid");

        var mode = await ResolveModeAsync(request.Mode, candidateSegment.Mode, cancellationToken);
        if (mode == null)
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(
                new() { ["mode"] = ["Mode must match an active transport profile or preserve the current inactive profile."] }, "segment-mode-invalid");

        var profileIds = new[] { candidateSegment.TransportProfileId, mode.Value.ProfileId }
            .Where(item => item.HasValue).Select(item => item!.Value).Distinct().Order().ToArray();
        var aggregateTokenCompared = false;
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await SegmentRouteReconciler.LockProfilesAsync(_dbContext, profileIds, cancellationToken);
            await SegmentRouteReconciler.LockSegmentAsync(_dbContext, candidateSegment.Id, cancellationToken);
            var lockedSegmentState = await _dbContext.Segments.AsNoTracking()
                .Where(item => item.Id == candidateSegment.Id)
                .Select(item => new { item.FromPlaceId, item.ToPlaceId }).SingleAsync(cancellationToken);
            var currentWaypointIds = await _dbContext.Set<SegmentWaypoint>().AsNoTracking()
                .Where(item => item.SegmentId == candidateSegment.Id).Select(item => item.PlaceId).ToArrayAsync(cancellationToken);
            var placeIds = currentWaypointIds.Select(item => (Guid?)item)
                .Concat(request.WaypointPlaceIds.Select(item => (Guid?)item))
                .Append(lockedSegmentState.FromPlaceId).Append(lockedSegmentState.ToPlaceId)
                .Append(request.FromPlaceId).Append(request.ToPlaceId)
                .Where(item => item.HasValue).Select(item => item!.Value).Distinct().Order().ToArray();
            await SegmentRouteReconciler.LockPlacesAndRegionsAsync(_dbContext, placeIds, cancellationToken);
            var canonical = await SegmentRouteReconciler.LoadAggregateAsync(_dbContext, candidateSegment.Id, cancellationToken);
            if (canonical == null || canonical.TripId != candidateTrip.Id || canonical.UserId != userId)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.NotFound();
            }

            var canonicalTrip = await _dbContext.Trips.AsNoTracking()
                .Include(item => item.Regions).ThenInclude(item => item.Places)
                .SingleAsync(item => item.Id == candidateTrip.Id && item.UserId == userId, cancellationToken);
            var referenceErrors = ValidatePlaceReferences(request, canonicalTrip);
            if (referenceErrors.Count > 0)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(referenceErrors);
            }

            if (canonical.TransportProfileId.HasValue && !profileIds.Contains(canonical.TransportProfileId.Value))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                DetachTargetAggregate(canonical.Id);
                if (lockAttempt < 3)
                    return await UpdateRelationalAsync(
                        candidateTrip, canonical, request, userId, confirmationToken, cancellationToken, lockAttempt + 1);
                return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.Conflicted(
                    new EditorSegmentConflictDto("segment-write-conflict", "update",
                        await LoadSegmentDtoAsync(canonical.Id, canonical.TripId, userId, cancellationToken),
                        "The Segment profile changed while waiting. Reload before saving.", null, null));
            }

            if (submittedVersion != canonical.RowVersion)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                var current = EditorTripStateMapper.ToSegment(canonical.TripId, canonical,
                    _aggregateTokens.Issue(userId, canonical.TripId, canonical.Id, canonical.RowVersion), true);
                return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.Conflicted(
                    new EditorSegmentConflictDto("segment-aggregate-stale", "update", current,
                        "The Segment changed. Reload its authoritative state before saving.", null, null));
            }
            aggregateTokenCompared = true;

            if (RequiresRouteClearConfirmation(canonical, request))
            {
                var fingerprint = BuildConfirmationFingerprint(userId, candidateTrip.Id, canonical, request, mode.Value.ProfileId);
                if (!_routeConfirmation.IsValid(confirmationToken, canonical.Id, fingerprint))
                {
                    var issued = _routeConfirmation.Issue(canonical.Id, fingerprint);
                    var code = string.IsNullOrWhiteSpace(confirmationToken)
                        ? "segment-route-clear-confirmation-required" : "segment-route-clear-confirmation-stale";
                    var current = EditorTripStateMapper.ToSegment(canonical.TripId, canonical,
                        _aggregateTokens.Issue(userId, canonical.TripId, canonical.Id, canonical.RowVersion), true);
                    await transaction.RollbackAsync(CancellationToken.None);
                    return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.Conflicted(
                        new EditorSegmentConflictDto(code, "update", current,
                            "Saving this anchor change requires clearing the custom route.", issued.ExpiresAt, issued.Token));
                }
                request = request with { Route = null, WaypointRouteVertexIndices = request.WaypointRouteVertexIndices.Select(_ => (int?)null).ToArray() };
            }

            var reconciliation = await SegmentRouteReconciler.ReconcileLockedAsync(
                _dbContext, BuildProposal(canonical.Id, request, mode.Value), false, cancellationToken);
            if (!reconciliation.Succeeded)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                await SegmentRouteReconciler.RecoverAggregateAsync(_dbContext, canonical.Id, CancellationToken.None);
                return ReconciliationFailed(reconciliation);
            }
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception conflict) when (conflict is DbUpdateConcurrencyException
            || conflict is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected })
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await SegmentRouteReconciler.RecoverAggregateAsync(_dbContext, candidateSegment.Id, CancellationToken.None);
            var currentVersion = await _dbContext.Segments.AsNoTracking()
                .Where(item => item.Id == candidateSegment.Id)
                .Select(item => item.RowVersion)
                .SingleAsync(CancellationToken.None);
            if (!aggregateTokenCompared && submittedVersion != currentVersion)
            {
                return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.Conflicted(
                    new EditorSegmentConflictDto("segment-aggregate-stale", "update",
                        await LoadSegmentDtoAsync(candidateSegment.Id, candidateTrip.Id, userId, CancellationToken.None),
                        "The Segment changed. Reload its authoritative state before saving.", null, null));
            }
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.Conflicted(
                new EditorSegmentConflictDto("segment-write-conflict", "update",
                    await LoadSegmentDtoAsync(candidateSegment.Id, candidateTrip.Id, userId, CancellationToken.None),
                    "The Segment could not be committed because canonical state changed. Reload before saving.", null, null));
        }
        catch (Exception original)
        {
            var cleanup = new List<Exception>();
            try { await transaction.RollbackAsync(CancellationToken.None); } catch (Exception failure) { cleanup.Add(failure); }
            try { await SegmentRouteReconciler.RecoverAggregateAsync(_dbContext, candidateSegment.Id, CancellationToken.None); } catch (Exception failure) { cleanup.Add(failure); }
            if (cleanup.Count > 0)
            {
                try { await _dbContext.DisposeAsync(); } catch (Exception failure) { cleanup.Add(failure); }
                throw new AggregateException("Segment editor mutation failed and cleanup could not restore context coherence.", [original, .. cleanup]);
            }
            ExceptionDispatchInfo.Capture(original).Throw();
            throw;
        }

        var dto = await LoadSegmentDtoAsync(candidateSegment.Id, candidateTrip.Id, userId, cancellationToken);
        var affected = await BuildAffectedAsync(candidateTrip.Id, [dto], false, cancellationToken);
        return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.Succeeded(
            new EditorMutationResult<EditorSegmentDto>(true, dto, affected, EditorDeletedIdsDto.Empty, []));
    }

    private void DetachTargetAggregate(Guid segmentId)
    {
        foreach (var waypoint in _dbContext.ChangeTracker.Entries<SegmentWaypoint>()
                     .Where(entry => entry.Entity.SegmentId == segmentId).ToArray())
            waypoint.State = EntityState.Detached;
        var segment = _dbContext.ChangeTracker.Entries<Segment>()
            .SingleOrDefault(entry => entry.Entity.Id == segmentId);
        if (segment != null) segment.State = EntityState.Detached;
    }
}
