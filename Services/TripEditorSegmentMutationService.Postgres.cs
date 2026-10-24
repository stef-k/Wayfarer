using System.Data;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

/// <summary>PostgreSQL transaction, lock, and recovery orchestration for editor Segment updates.</summary>
public sealed partial class TripEditorSegmentMutationService
{
    /// <summary>Creates one complete editor Segment inside its caller-specific Serializable transaction.</summary>
    private async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>> CreateRelationalAsync(
        Guid tripId,
        string userId,
        EditorSegmentSaveRequest request,
        (string Key, Guid? ProfileId) mode,
        CancellationToken cancellationToken)
    {
        var trackerSnapshot = SegmentEditorTrackerSnapshot.Capture(_dbContext);
        var segmentId = _segmentIdFactory();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await SegmentRouteReconciler.LockProfilesAsync(
                _dbContext, mode.ProfileId.HasValue ? [mode.ProfileId.Value] : [], cancellationToken);
            var placeIds = request.WaypointPlaceIds.Select(item => (Guid?)item)
                .Append(request.FromPlaceId).Append(request.ToPlaceId)
                .Where(item => item.HasValue).Select(item => item!.Value).Distinct().Order().ToArray();
            await SegmentRouteReconciler.LockPlacesAndRegionsAsync(_dbContext, placeIds, cancellationToken);
            var canonicalTrip = await _dbContext.Trips.AsNoTracking()
                .Include(item => item.Regions).ThenInclude(item => item.Places)
                .Include(item => item.Segments)
                .SingleOrDefaultAsync(item => item.Id == tripId && item.UserId == userId, cancellationToken);
            if (canonicalTrip == null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.NotFound();
            }

            var referenceErrors = ValidatePlaceReferences(request, canonicalTrip);
            if (referenceErrors.Count > 0)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(
                    referenceErrors, SegmentValidationCode(referenceErrors));
            }

            var creation = new SegmentCreation(segmentId, userId, tripId, NextSegmentOrder(canonicalTrip));
            var reconciliation = await SegmentRouteReconciler.ReconcileNewLockedAsync(
                _dbContext, creation, BuildProposal(segmentId, request, mode), cancellationToken);
            if (!reconciliation.Succeeded)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                trackerSnapshot.Restore(_dbContext);
                return ReconciliationFailed(reconciliation);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException collision) when (
            collision.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await CleanupRelationalFailureAsync(transaction, segmentId, trackerSnapshot, collision, creating: true);
            var ownedCollision = await _dbContext.Segments.AsNoTracking()
                .AnyAsync(item => item.Id == segmentId && item.TripId == tripId && item.UserId == userId, CancellationToken.None);
            if (!ownedCollision)
            {
                ExceptionDispatchInfo.Capture(collision).Throw();
                throw;
            }
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.Conflicted(
                new EditorSegmentConflictDto("segment-write-conflict", "create",
                    await LoadSegmentDtoAsync(segmentId, tripId, userId, CancellationToken.None),
                    "The Segment could not be created because its identity changed. Try saving again.", null, null));
        }
        catch (Exception original)
        {
            await CleanupRelationalFailureAsync(transaction, segmentId, trackerSnapshot, original, creating: true);
            ExceptionDispatchInfo.Capture(original).Throw();
            throw;
        }

        var dto = await LoadSegmentDtoAsync(segmentId, tripId, userId, cancellationToken);
        var affected = await BuildAffectedAsync(tripId, [dto], true, cancellationToken);
        return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.Succeeded(
            new EditorMutationResult<EditorSegmentDto>(true, dto, affected, EditorDeletedIdsDto.Empty, []));
    }

    private async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>> UpdateRelationalAsync(
        Trip candidateTrip, Segment candidateSegment, EditorSegmentSaveRequest request, string userId,
        string? confirmationToken, CancellationToken cancellationToken, int lockAttempt = 1,
        SegmentEditorTrackerSnapshot? trackerSnapshot = null)
    {
        trackerSnapshot ??= SegmentEditorTrackerSnapshot.Capture(_dbContext);
        var originalRequest = request;
        uint submittedVersion = 0;
        var validEvidence = request.IndependentRoute
            ? _aggregateTokens.TryReadRoute(request.OriginalRouteToken, userId, candidateTrip.Id, candidateSegment.Id, out _)
            : _aggregateTokens.TryRead(request.AggregateConcurrencyToken, userId, candidateTrip.Id, candidateSegment.Id, out submittedVersion);
        if (!validEvidence)
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(
                new() { [request.IndependentRoute ? "originalRouteToken" : "aggregateConcurrencyToken"] = ["Original evidence is missing, malformed, or scoped to another Segment."] },
                request.IndependentRoute ? "segment-route-token-invalid" : "segment-aggregate-token-invalid");

        var mode = await ResolveModeAsync(request.Mode, candidateSegment, cancellationToken);
        if (mode == null)
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(
                new() { ["mode"] = ["Mode must match an active transport profile or preserve the current inactive profile."] }, "segment-mode-invalid");

        var profileIds = new[] { candidateSegment.TransportProfileId, mode.Value.ProfileId }
            .Where(item => item.HasValue).Select(item => item!.Value).Distinct().Order().ToArray();
        var aggregateTokenCompared = false;
        var commitStarted = false;
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            Wayfarer.Services.ExternalRouting.ExternalRouteProposalBinding? binding = null;
            if (request.Proposal != null)
            {
                if (_proposalValidator == null) return ProposalFailed("route-proposal-unavailable");
                var authority = await _proposalValidator.LockAuthorityAsync(
                    userId, candidateTrip.Id, candidateSegment.Id, request, cancellationToken);
                if (authority.Error != null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    trackerSnapshot.Restore(_dbContext);
                    return ProposalFailed(authority.Error);
                }
                binding = authority.Binding;
            }
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
                .SingleOrDefaultAsync(item => item.Id == candidateTrip.Id && item.UserId == userId, cancellationToken);
            if (canonicalTrip == null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.NotFound();
            }
            if (request.IndependentRoute)
            {
                var active = await _dbContext.Users.FromSqlInterpolated($"SELECT * FROM public.\"AspNetUsers\" WHERE \"Id\" = {userId} AND \"IsActive\" FOR SHARE")
                    .AsNoTracking().ToListAsync(cancellationToken);
                if (active.Count == 0) throw new TripMetadataMutationRejected(401, "mutation-authority-unavailable", "Authentication is no longer valid.");
            }
            var referenceErrors = ValidatePlaceReferences(request, canonicalTrip, validateRouteCoordinates: false);
            if (referenceErrors.Count > 0)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(referenceErrors);
            }

            if (canonical.TransportProfileId.HasValue && !profileIds.Contains(canonical.TransportProfileId.Value))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                DetachTargetAggregate(canonical.Id);
                await transaction.DisposeAsync();
                if (lockAttempt < 3)
                    return await UpdateRelationalAsync(
                        candidateTrip, canonical, request, userId, confirmationToken, cancellationToken,
                        lockAttempt + 1, trackerSnapshot);
                return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.Conflicted(
                    new EditorSegmentConflictDto("segment-write-conflict", "update",
                        await LoadSegmentDtoAsync(canonical.Id, canonical.TripId, userId, cancellationToken),
                        "The Segment profile changed while waiting. Reload before saving.", null, null));
            }

            if (request.IndependentRoute
                ? !_aggregateTokens.MatchesRoute(request.OriginalRouteToken, userId, canonical.TripId, canonical)
                : submittedVersion != canonical.RowVersion)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                var current = EditorTripStateMapper.ToSegment(canonical.TripId, canonical,
                    _aggregateTokens.Issue(userId, canonical.TripId, canonical.Id, canonical.RowVersion), true, originalRouteToken: _aggregateTokens.IssueRoute(userId, canonical.TripId, canonical));
                return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.Conflicted(
                    new EditorSegmentConflictDto(request.IndependentRoute ? "segment-route-stale" : "segment-aggregate-stale", "update", current,
                        "The Segment changed. Reload its authoritative state before saving.", null, null));
            }
            aggregateTokenCompared = true;
            if (request.NotesIntent is { } notes && !string.Equals(notes.Expected, Wayfarer.Util.RichNotes.Normalize(canonical.Notes), StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.Conflicted(
                    new EditorSegmentConflictDto("segment-notes-stale", "update",
                        await LoadSegmentDtoAsync(canonical.Id, canonical.TripId, userId, cancellationToken),
                        "Segment Notes changed. Keep your draft and explicitly review current Notes before saving.", null, null));
            }

            if (binding != null && await _proposalValidator!.ValidateFinalAsync(
                binding, canonical, request, mode.Value.ProfileId, cancellationToken) is { } proposalError)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                trackerSnapshot.Restore(_dbContext);
                return ProposalFailed(proposalError);
            }

            if (RequiresRouteClearConfirmation(canonical, request))
            {
                var fingerprint = SegmentRouteClearConfirmation.Fingerprint(userId, candidateTrip.Id, canonical, request, mode.Value.ProfileId);
                if (!_routeConfirmation.IsValid(confirmationToken, canonical.Id, fingerprint))
                {
                    // A stale new-dialect consent never receives renewed expiry on the failed attempt.
                    (string Token, DateTimeOffset ExpiresAt)? issued = !request.IndependentRoute || string.IsNullOrWhiteSpace(confirmationToken)
                        ? _routeConfirmation.Issue(canonical.Id, fingerprint) : null;
                    var code = string.IsNullOrWhiteSpace(confirmationToken)
                        ? "segment-route-clear-confirmation-required" : "segment-route-clear-confirmation-stale";
                    var current = EditorTripStateMapper.ToSegment(canonical.TripId, canonical,
                        _aggregateTokens.Issue(userId, canonical.TripId, canonical.Id, canonical.RowVersion), true, originalRouteToken: _aggregateTokens.IssueRoute(userId, canonical.TripId, canonical));
                    await transaction.RollbackAsync(CancellationToken.None);
                    return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.Conflicted(
                        new EditorSegmentConflictDto(code, "update", current,
                            "Saving this anchor change requires clearing the custom route.", issued?.ExpiresAt, issued?.Token));
                }
                request = request with { Route = null, WaypointRouteVertexIndices = request.WaypointRouteVertexIndices.Select(_ => (int?)null).ToArray() };
            }

            referenceErrors = ValidatePlaceReferences(request, canonicalTrip);
            if (referenceErrors.Count > 0)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(referenceErrors);
            }

            var reconciliation = await SegmentRouteReconciler.ReconcileLockedAsync(
                _dbContext, BuildSavedRoute(canonical, request, mode.Value, binding), false, cancellationToken);
            if (!reconciliation.Succeeded)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                await SegmentRouteReconciler.RecoverAggregateAsync(_dbContext, canonical.Id, CancellationToken.None);
                return ReconciliationFailed(reconciliation);
            }
            if (binding != null)
            {
                if (!_proposalValidator!.IsCurrent(binding, request.Proposal!.ProtectedContext))
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    await SegmentRouteReconciler.RecoverAggregateAsync(_dbContext, canonical.Id, CancellationToken.None);
                    trackerSnapshot.Restore(_dbContext);
                    return ProposalFailed("route-proposal-invalid-or-expired");
                }
                ApplyProposalProvenance(canonical, binding);
            }
            await _dbContext.SaveChangesAsync(cancellationToken);
            commitStarted = true;
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception conflict) when (IsWriteConflict(conflict) && (!commitStarted || IsTransactionAbort(conflict)))
        {
            await CleanupRelationalFailureAsync(transaction, candidateSegment.Id, trackerSnapshot, conflict);
            // Only PostgreSQL's definitive abort plus successful rollback/recovery permits a bounded identical restart.
            if (request.IndependentRoute && IsTransactionAbort(conflict) && lockAttempt < 3)
            {
                await transaction.DisposeAsync();
                return await UpdateRelationalAsync(candidateTrip, candidateSegment, originalRequest, userId,
                    confirmationToken, cancellationToken, lockAttempt + 1, trackerSnapshot);
            }
            var currentVersion = await _dbContext.Segments.AsNoTracking()
                .Where(item => item.Id == candidateSegment.Id)
                .Select(item => item.RowVersion)
                .SingleAsync(CancellationToken.None);
            if (!request.IndependentRoute && !aggregateTokenCompared && submittedVersion != currentVersion)
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
            await CleanupRelationalFailureAsync(transaction, candidateSegment.Id, trackerSnapshot, original);
            ExceptionDispatchInfo.Capture(original).Throw();
            throw;
        }

        var dto = await LoadSegmentDtoAsync(candidateSegment.Id, candidateTrip.Id, userId, cancellationToken);
        var affected = await BuildAffectedAsync(candidateTrip.Id, [dto], false, cancellationToken);
        return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.Succeeded(
            new EditorMutationResult<EditorSegmentDto>(true, dto, affected, EditorDeletedIdsDto.Empty, []));
    }

    /// <summary>Preserves bounded conflict handling when EF wraps PostgreSQL serialization failures.</summary>
    private static bool IsWriteConflict(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
            if (current is DbUpdateConcurrencyException
                || current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected })
                return true;
        return false;
    }

    /// <summary>Recognizes only server-proven aborted transactions, never a transport or uncertain commit failure.</summary>
    private static bool IsTransactionAbort(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected }) return true;
        return false;
    }

    /// <summary>Performs mandatory rollback, aggregate recovery, and exact tracker restoration before a failure may be classified.</summary>
    private async Task CleanupRelationalFailureAsync(
        IDbContextTransaction transaction,
        Guid segmentId,
        SegmentEditorTrackerSnapshot trackerSnapshot,
        Exception original,
        bool creating = false)
    {
        var cleanup = new List<Exception>();
        try { await transaction.RollbackAsync(CancellationToken.None); } catch (Exception failure) { cleanup.Add(failure); }
        try
        {
            if (creating) await RecoverCreateAttemptAsync(segmentId);
            else await SegmentRouteReconciler.RecoverAggregateAsync(_dbContext, segmentId, CancellationToken.None);
        }
        catch (Exception failure) { cleanup.Add(failure); }
        try { _contextRecovery.RestoreTracker(_dbContext, trackerSnapshot); } catch (Exception failure) { cleanup.Add(failure); }
        if (cleanup.Count == 0) return;

        try { await _contextRecovery.InvalidateAsync(_dbContext); } catch (Exception failure) { cleanup.Add(failure); }
        throw new AggregateException(
            "Segment editor mutation failed and cleanup could not restore context coherence.",
            [original, .. cleanup]);
    }

    /// <summary>Detaches a never-committed create attempt before checking whether its application ID already exists.</summary>
    private async Task RecoverCreateAttemptAsync(Guid segmentId)
    {
        DetachTargetAggregate(segmentId);
        await SegmentRouteReconciler.LoadAggregateAsync(_dbContext, segmentId, CancellationToken.None);
    }

    /// <summary>Restores the exact caller-owned tracker after a failed relational editor mutation.</summary>
    internal sealed record SegmentEditorTrackerSnapshot(
        IReadOnlyDictionary<object, SegmentEditorTrackedEntrySnapshot> Entries)
    {
        internal static SegmentEditorTrackerSnapshot Capture(ApplicationDbContext context) => new(
            context.ChangeTracker.Entries().ToDictionary(
                entry => entry.Entity,
                entry => new SegmentEditorTrackedEntrySnapshot(entry.State, entry.CurrentValues.Clone()),
                ReferenceEqualityComparer.Instance));

        internal void Restore(ApplicationDbContext context)
        {
            var restored = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var entry in context.ChangeTracker.Entries().ToArray())
            {
                if (Entries.TryGetValue(entry.Entity, out var original))
                {
                    entry.CurrentValues.SetValues(original.Values);
                    entry.State = original.State;
                    restored.Add(entry.Entity);
                }
                else
                {
                    entry.State = EntityState.Detached;
                }
            }

            foreach (var pair in Entries.Where(pair => !restored.Contains(pair.Key)))
            {
                var replacement = context.ChangeTracker.Entries()
                    .FirstOrDefault(entry => HasSamePrimaryKey(entry, pair.Key, pair.Value.Values));
                if (replacement != null) replacement.State = EntityState.Detached;
                var entry = context.Entry(pair.Key);
                entry.State = EntityState.Unchanged;
                entry.CurrentValues.SetValues(pair.Value.Values);
                entry.State = pair.Value.State;
            }
        }

        private static bool HasSamePrimaryKey(EntityEntry entry, object entity, PropertyValues values)
        {
            if (entry.Entity.GetType() != entity.GetType()) return false;
            var key = entry.Metadata.FindPrimaryKey();
            return key != null && key.Properties.All(property =>
                Equals(entry.Property(property.Name).CurrentValue, values[property.Name]));
        }
    }

    /// <summary>One pre-operation entity state and scalar-value snapshot.</summary>
    internal sealed record SegmentEditorTrackedEntrySnapshot(EntityState State, PropertyValues Values);

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

/// <summary>Owns exact tracker restoration and invalidation of an incoherent editor context.</summary>
internal interface ISegmentEditorContextRecovery
{
    /// <summary>Restores the pre-operation tracked entities and scalar values.</summary>
    void RestoreTracker(
        ApplicationDbContext context,
        TripEditorSegmentMutationService.SegmentEditorTrackerSnapshot snapshot);

    /// <summary>Invalidates a context whose transaction cleanup could not establish coherence.</summary>
    ValueTask InvalidateAsync(ApplicationDbContext context);
}

/// <summary>Uses EF tracker restoration and disposal for production editor cleanup.</summary>
internal sealed class SegmentEditorContextRecovery : ISegmentEditorContextRecovery
{
    /// <inheritdoc />
    public void RestoreTracker(
        ApplicationDbContext context,
        TripEditorSegmentMutationService.SegmentEditorTrackerSnapshot snapshot) => snapshot.Restore(context);

    /// <inheritdoc />
    public ValueTask InvalidateAsync(ApplicationDbContext context) => context.DisposeAsync();
}
