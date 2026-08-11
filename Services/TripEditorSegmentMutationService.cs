using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

/// <summary>
/// Executes Trip Editor segment mutations and builds their mutation result envelopes.
/// </summary>
public sealed partial class TripEditorSegmentMutationService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly SegmentAggregateTokenService _aggregateTokens;
    private readonly SegmentRouteClearConfirmation _routeConfirmation;

    /// <summary>
    /// Initializes a new segment mutation service for the editor API.
    /// </summary>
    public TripEditorSegmentMutationService(ApplicationDbContext dbContext)
        : this(dbContext, CreateFallbackServices())
    {
    }

    /// <summary>Issues the initial editor token after authoritative Segment loading.</summary>
    public string IssueAggregateToken(string userId, Guid tripId, Segment segment) =>
        _aggregateTokens.Issue(userId, tripId, segment.Id, segment.RowVersion);

    private TripEditorSegmentMutationService(
        ApplicationDbContext dbContext,
        (SegmentAggregateTokenService Tokens, SegmentRouteClearConfirmation Confirmation) services)
        : this(dbContext, services.Tokens, services.Confirmation)
    {
    }

    /// <summary>Initializes the production editor aggregate dependencies.</summary>
    public TripEditorSegmentMutationService(
        ApplicationDbContext dbContext,
        SegmentAggregateTokenService aggregateTokens,
        SegmentRouteClearConfirmation routeConfirmation)
    {
        _dbContext = dbContext;
        _aggregateTokens = aggregateTokens;
        _routeConfirmation = routeConfirmation;
    }

    /// <summary>
    /// Creates a trip-level segment after the owned trip is verified.
    /// </summary>
    public async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>> CreateSegmentAsync(
        Guid tripId,
        string userId,
        Stream requestBody,
        CancellationToken cancellationToken)
    {
        var trip = await LoadTripCandidateAsync(tripId, userId, cancellationToken);
        if (trip == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.NotFound();
        }

        var parsed = await ParseAndValidateSaveAsync(requestBody, cancellationToken);
        if (parsed.ValidationErrors != null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(
                parsed.ValidationErrors, SegmentValidationCode(parsed.ValidationErrors));
        }

        if (parsed.Value!.AggregateConcurrencyToken != null)
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(
                new() { ["aggregateConcurrencyToken"] = ["Create requires an explicit null aggregate token."] },
                "segment-aggregate-token-invalid");

        var referenceErrors = ValidatePlaceReferences(parsed.Value!, trip);
        if (referenceErrors.Count > 0)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(
                referenceErrors, SegmentValidationCode(referenceErrors));
        }

        var mode = await ResolveModeAsync(parsed.Value!.Mode, null, cancellationToken);
        if (mode == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(
                new Dictionary<string, string[]> { ["mode"] = ["Mode must match an active transport profile."] });
        }

        var segmentId = Guid.NewGuid();
        var creation = new SegmentCreation(segmentId, userId, trip.Id, NextSegmentOrder(trip));
        var proposal = BuildProposal(segmentId, parsed.Value!, mode.Value);
        var reconciliation = await SegmentRouteReconciler.CreateAsync(_dbContext, creation, proposal, cancellationToken);
        if (!reconciliation.Succeeded)
            return ReconciliationFailed(reconciliation);

        var dto = await LoadSegmentDtoAsync(segmentId, tripId, userId, cancellationToken);
        var affected = await BuildAffectedAsync(tripId, new[] { dto }, includeOrder: true, cancellationToken);
        return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.Succeeded(
            new EditorMutationResult<EditorSegmentDto>(true, dto, affected, EditorDeletedIdsDto.Empty, Array.Empty<EditorWarningDto>()));
    }

    /// <summary>
    /// Updates complete editable fields for one owned segment.
    /// </summary>
    public async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>> UpdateSegmentAsync(
        Guid tripId,
        Guid segmentId,
        string userId,
        Stream requestBody,
        string? confirmationToken,
        CancellationToken cancellationToken)
    {
        if (_dbContext.Database.IsRelational())
        {
            var candidate = await _dbContext.Segments.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == segmentId && item.TripId == tripId && item.UserId == userId, cancellationToken);
            if (candidate == null)
                return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.NotFound();
            var relationalRequest = await ParseAndValidateSaveAsync(requestBody, cancellationToken);
            if (relationalRequest.ValidationErrors != null)
                return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(
                    relationalRequest.ValidationErrors, SegmentValidationCode(relationalRequest.ValidationErrors));
            var candidateTrip = new Trip { Id = tripId, UserId = userId, Name = string.Empty, UpdatedAt = DateTime.UtcNow };
            return await UpdateRelationalAsync(candidateTrip, candidate, relationalRequest.Value!, userId, confirmationToken, cancellationToken);
        }

        var trip = await LoadTripGraphAsync(tripId, userId, cancellationToken);
        if (trip == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.NotFound();
        }

        var segment = trip.Segments.FirstOrDefault(s => s.Id == segmentId);
        if (segment == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.NotFound();
        }

        var parsed = await ParseAndValidateSaveAsync(requestBody, cancellationToken);
        if (parsed.ValidationErrors != null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(
                parsed.ValidationErrors, SegmentValidationCode(parsed.ValidationErrors));
        }

        var submittedVersion = segment.RowVersion;

        if (submittedVersion != segment.RowVersion)
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.Conflicted(
                new EditorSegmentConflictDto("segment-aggregate-stale", "update", await LoadSegmentDtoAsync(segmentId, tripId, userId, cancellationToken),
                    "The Segment changed. Reload its authoritative state before saving.", null, null));

        var referenceErrors = ValidatePlaceReferences(parsed.Value!, trip);
        if (referenceErrors.Count > 0)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(referenceErrors);
        }

        var mode = await ResolveModeAsync(parsed.Value!.Mode, segment.Mode, cancellationToken);
        if (mode == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(
                new Dictionary<string, string[]> { ["mode"] = ["Mode must match an active transport profile or preserve the segment's current inactive profile."] });
        }

        if (RequiresRouteClearConfirmation(segment, parsed.Value!))
        {
            var fingerprint = BuildConfirmationFingerprint(userId, tripId, segment, parsed.Value!, mode.Value.ProfileId);
            if (!_routeConfirmation.IsValid(confirmationToken, segmentId, fingerprint))
            {
                var issued = _routeConfirmation.Issue(segmentId, fingerprint);
                var code = string.IsNullOrWhiteSpace(confirmationToken)
                    ? "segment-route-clear-confirmation-required"
                    : "segment-route-clear-confirmation-stale";
                return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.Conflicted(
                    new EditorSegmentConflictDto(code, "update", await LoadSegmentDtoAsync(segmentId, tripId, userId, cancellationToken),
                        "Saving this anchor change requires clearing the custom route.", issued.ExpiresAt, issued.Token));
            }
            parsed = (parsed.Value! with
            {
                Route = null,
                WaypointRouteVertexIndices = parsed.Value.WaypointRouteVertexIndices.Select(_ => (int?)null).ToArray()
            }, null);
        }

        var proposal = BuildProposal(segment.Id, parsed.Value!, mode.Value);
        var reconciliation = await SegmentRouteReconciler.ReconcileAsync(_dbContext, proposal, cancellationToken);
        if (!reconciliation.Succeeded)
            return ReconciliationFailed(reconciliation);

        var dto = await LoadSegmentDtoAsync(segmentId, tripId, userId, cancellationToken);
        var affected = await BuildAffectedAsync(tripId, new[] { dto }, includeOrder: false, cancellationToken);
        return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.Succeeded(
            new EditorMutationResult<EditorSegmentDto>(true, dto, affected, EditorDeletedIdsDto.Empty, Array.Empty<EditorWarningDto>()));
    }

    /// <summary>
    /// Deletes one owned segment and returns the updated trip-level segment order.
    /// </summary>
    public async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDeleteResult>>> DeleteSegmentAsync(
        Guid tripId,
        Guid segmentId,
        string userId,
        CancellationToken cancellationToken)
    {
        var segment = await LoadOwnedSegmentAsync(tripId, segmentId, userId, cancellationToken);
        if (segment == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDeleteResult>>.NotFound();
        }

        _dbContext.Segments.Remove(segment);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await NormalizeSegmentOrdersAsync(tripId, userId, cancellationToken);

        var affected = await BuildAffectedAsync(tripId, Array.Empty<EditorSegmentDto>(), includeOrder: true, cancellationToken);
        var deletedIds = new EditorDeletedIdsDto(Array.Empty<Guid>(), Array.Empty<Guid>(), Array.Empty<Guid>(), new[] { segmentId }, Array.Empty<string>());
        return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDeleteResult>>.Succeeded(
            new EditorMutationResult<EditorSegmentDeleteResult>(true, new EditorSegmentDeleteResult(segmentId), affected, deletedIds, Array.Empty<EditorWarningDto>()));
    }

    /// <summary>
    /// Persists the complete desired trip-level segment order.
    /// </summary>
    public async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentOrderResult>>> OrderSegmentsAsync(
        Guid tripId,
        string userId,
        Stream requestBody,
        CancellationToken cancellationToken)
    {
        var trip = await LoadTripGraphAsync(tripId, userId, cancellationToken);
        if (trip == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentOrderResult>>.NotFound();
        }

        var request = await ParseJsonBodyAsync(requestBody, "Segment order request must be valid JSON.", cancellationToken);
        if (request.ValidationErrors != null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentOrderResult>>.ValidationFailed(request.ValidationErrors);
        }

        if (!EditorSegmentRequestParser.TryParseOrder(request.Value!.Value, out var orderRequest, out var errors))
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentOrderResult>>.ValidationFailed(errors);
        }

        var currentIds = trip.Segments.OrderBy(s => s.DisplayOrder).ThenBy(s => s.Id).Select(s => s.Id).ToList();
        if (orderRequest.SegmentIds.Count != currentIds.Count
            || orderRequest.SegmentIds.Distinct().Count() != orderRequest.SegmentIds.Count
            || orderRequest.SegmentIds.Any(id => !currentIds.Contains(id)))
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentOrderResult>>.ValidationFailed(new Dictionary<string, string[]>
            {
                ["segmentIds"] = new[] { "Segment IDs must include every segment in this trip exactly once." }
            });
        }

        var segmentsById = trip.Segments.ToDictionary(s => s.Id);
        for (var i = 0; i < orderRequest.SegmentIds.Count; i++)
        {
            segmentsById[orderRequest.SegmentIds[i]].DisplayOrder = i + 1;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var segmentOrder = await LoadSegmentOrderAsync(tripId, cancellationToken);
        var segmentDtos = await LoadSegmentDtosAsync(orderRequest.SegmentIds, tripId, userId, cancellationToken);
        var affected = await BuildAffectedAsync(tripId, segmentDtos, includeOrder: true, cancellationToken);
        var data = new EditorSegmentOrderResult(segmentOrder);
        return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentOrderResult>>.Succeeded(
            new EditorMutationResult<EditorSegmentOrderResult>(true, data, affected, EditorDeletedIdsDto.Empty, Array.Empty<EditorWarningDto>()));
    }

    private async Task<(EditorSegmentSaveRequest? Value, Dictionary<string, string[]>? ValidationErrors)> ParseAndValidateSaveAsync(Stream body, CancellationToken token)
    {
        var request = await ParseJsonBodyAsync(body, "Segment mutation request must be valid JSON.", token);
        if (request.ValidationErrors != null)
        {
            return (null, request.ValidationErrors);
        }

        return EditorSegmentRequestParser.TryParseSave(request.Value!.Value, out var value, out var errors) ? (value, null) : (null, errors);
    }

    private async Task<Trip?> LoadTripGraphAsync(Guid tripId, string userId, CancellationToken cancellationToken) =>
        await _dbContext.Trips
            .Include(t => t.Regions).ThenInclude(r => r.Places)
            .Include(t => t.Segments).ThenInclude(s => s.FromPlace)
            .Include(t => t.Segments).ThenInclude(s => s.ToPlace)
            .Include(t => t.Segments).ThenInclude(s => s.Waypoints).ThenInclude(w => w.Place)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);

    private async Task<Trip?> LoadTripCandidateAsync(Guid tripId, string userId, CancellationToken cancellationToken) =>
        await _dbContext.Trips.AsNoTracking()
            .Include(t => t.Regions).ThenInclude(r => r.Places)
            .Include(t => t.Segments)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);

    private async Task<Segment?> LoadOwnedSegmentAsync(Guid tripId, Guid segmentId, string userId, CancellationToken cancellationToken) =>
        await _dbContext.Segments
            .Include(s => s.Trip)
            .FirstOrDefaultAsync(s => s.Id == segmentId && s.TripId == tripId && s.Trip.UserId == userId, cancellationToken);

    private static SegmentRouteProposal BuildProposal(
        Guid segmentId,
        EditorSegmentSaveRequest request,
        (string Key, Guid? ProfileId) mode) =>
        new(segmentId, request.FromPlaceId, request.ToPlaceId,
            request.WaypointPlaceIds.Select((id, index) => new SegmentWaypointProposal(id, index, request.WaypointRouteVertexIndices[index])).ToArray(), request.Route,
            new(mode.Key, mode.ProfileId, request.EstimatedDurationSource, request.EstimatedDurationMinutes),
            ApplyNotes: true, NotesHtml: request.NotesHtml);

    private static EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>> ReconciliationFailed(
        SegmentRouteReconciliationResult result)
    {
        var automaticUnavailable = result.Errors.Any(error => error.StartsWith("Automatic duration requires", StringComparison.Ordinal));
        return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(
            new Dictionary<string, string[]>
            {
                [automaticUnavailable ? "estimatedDurationSource" : "segment"] = result.Errors.ToArray()
            });
    }

    /// <summary>Resolves database-backed mode semantics for a create or edit operation.</summary>
    private async Task<(string Key, Guid? ProfileId)?> ResolveModeAsync(string requestedMode, string? currentMode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestedMode))
        {
            return (string.Empty, null);
        }

        var resolved = await new TransportProfileCatalog(_dbContext)
            .ResolveEditorModeAsync(requestedMode, currentMode, cancellationToken);
        if (resolved == null)
        {
            return null;
        }

        var profileId = await _dbContext.Set<TransportProfile>()
            .Where(profile => profile.Key == resolved)
            .Select(profile => (Guid?)profile.Id)
            .SingleAsync(cancellationToken);
        var preserveCurrent = !string.IsNullOrWhiteSpace(currentMode)
            && string.Equals(TransportProfile.NormalizeKey(requestedMode), TransportProfile.NormalizeKey(currentMode), StringComparison.Ordinal);
        return (preserveCurrent ? currentMode! : resolved, profileId);
    }

    private async Task<EditorSegmentDto> LoadSegmentDtoAsync(Guid segmentId, Guid tripId, string userId, CancellationToken cancellationToken)
    {
        var segment = await _dbContext.Segments.AsNoTracking()
            .Include(item => item.FromPlace).Include(item => item.ToPlace)
            .Include(item => item.Waypoints.OrderBy(waypoint => waypoint.Position)).ThenInclude(waypoint => waypoint.Place)
            .SingleAsync(s => s.Id == segmentId, cancellationToken);
        return EditorTripStateMapper.ToSegment(tripId, segment, _aggregateTokens.Issue(userId, tripId, segmentId, segment.RowVersion), true);
    }

    private async Task<IReadOnlyList<EditorSegmentDto>> LoadSegmentDtosAsync(
        IReadOnlyList<Guid> ids, Guid tripId, string userId, CancellationToken cancellationToken)
    {
        var segments = await _dbContext.Segments.AsNoTracking().Include(item => item.FromPlace).Include(item => item.ToPlace)
            .Include(item => item.Waypoints.OrderBy(waypoint => waypoint.Position)).ThenInclude(waypoint => waypoint.Place)
            .Where(s => ids.Contains(s.Id)).ToListAsync(cancellationToken);
        var byId = segments.ToDictionary(s => s.Id);
        return ids.Select(id => EditorTripStateMapper.ToSegment(tripId, byId[id],
            _aggregateTokens.Issue(userId, tripId, id, byId[id].RowVersion), true)).ToList();
    }

    private async Task<EditorAffectedSlicesDto> BuildAffectedAsync(
        Guid tripId,
        IReadOnlyList<EditorSegmentDto> segments,
        bool includeOrder,
        CancellationToken cancellationToken) =>
        new(
            null,
            Array.Empty<EditorRegionDto>(),
            null,
            Array.Empty<EditorPlaceDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            Array.Empty<EditorAreaDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            segments,
            includeOrder ? await LoadSegmentOrderAsync(tripId, cancellationToken) : null,
            Array.Empty<EditorTagDto>(),
            null,
            null,
            null);

    private async Task<IReadOnlyList<Guid>> LoadSegmentOrderAsync(Guid tripId, CancellationToken cancellationToken) =>
        await _dbContext.Segments
            .AsNoTracking()
            .Where(s => s.TripId == tripId)
            .OrderBy(s => s.DisplayOrder)
            .ThenBy(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

    private async Task NormalizeSegmentOrdersAsync(Guid tripId, string userId, CancellationToken cancellationToken)
    {
        var segments = await _dbContext.Segments
            .Where(s => s.TripId == tripId && s.UserId == userId)
            .OrderBy(s => s.DisplayOrder)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);
        for (var i = 0; i < segments.Count; i++)
        {
            segments[i].DisplayOrder = i + 1;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static int NextSegmentOrder(Trip trip) =>
        (trip.Segments.Count == 0 ? 0 : trip.Segments.Max(s => s.DisplayOrder)) + 1;

    private static bool RequiresRouteClearConfirmation(Segment current, EditorSegmentSaveRequest proposed)
    {
        if (current.RouteGeometry == null || (current.Waypoints.Count == 0 && proposed.WaypointPlaceIds.Count == 0)) return false;
        var currentIds = current.Waypoints.OrderBy(item => item.Position).Select(item => item.PlaceId).ToArray();
        var pureRemoval = current.FromPlaceId == proposed.FromPlaceId && current.ToPlaceId == proposed.ToPlaceId
            && proposed.WaypointPlaceIds.Count < currentIds.Length
            && IsOrderPreservingSubsequence(proposed.WaypointPlaceIds, currentIds);
        return !pureRemoval && (current.FromPlaceId != proposed.FromPlaceId || current.ToPlaceId != proposed.ToPlaceId
            || !currentIds.SequenceEqual(proposed.WaypointPlaceIds));
    }

    private static bool IsOrderPreservingSubsequence(IReadOnlyList<Guid> proposed, IReadOnlyList<Guid> current)
    {
        var cursor = 0;
        foreach (var id in current)
            if (cursor < proposed.Count && proposed[cursor] == id) cursor++;
        return cursor == proposed.Count;
    }

    private static string BuildConfirmationFingerprint(string userId, Guid tripId, Segment current,
        EditorSegmentSaveRequest proposed, Guid? profileId)
    {
        var geometryIdentity = current.RouteGeometry == null
            ? "none"
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(current.RouteGeometry.AsBinary()));
        var currentState = new SegmentRouteClearState(current.RowVersion, current.FromPlaceId, current.ToPlaceId,
            current.Waypoints.OrderBy(item => item.Position).Select(item => item.PlaceId).ToArray(), current.TransportProfileId, geometryIdentity);
        var proposedState = new SegmentRouteClearState(current.RowVersion, proposed.FromPlaceId, proposed.ToPlaceId,
            proposed.WaypointPlaceIds, profileId, geometryIdentity);
        return SegmentRouteClearConfirmation.Fingerprint(userId, tripId, currentState, proposedState);
    }

    private static (SegmentAggregateTokenService Tokens, SegmentRouteClearConfirmation Confirmation) CreateFallbackServices()
    {
        var provider = new EphemeralDataProtectionProvider();
        return (new SegmentAggregateTokenService(provider), new SegmentRouteClearConfirmation(provider, TimeProvider.System));
    }

    private static async Task<(JsonElement? Value, Dictionary<string, string[]>? ValidationErrors)> ParseJsonBodyAsync(
        Stream requestBody,
        string invalidJsonMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(requestBody, cancellationToken: cancellationToken);
            return (document.RootElement.Clone(), null);
        }
        catch (JsonException)
        {
            return (null, new Dictionary<string, string[]> { ["request"] = new[] { invalidJsonMessage } });
        }
    }
}
