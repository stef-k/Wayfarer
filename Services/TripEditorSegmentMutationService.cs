using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

/// <summary>
/// Executes Trip Editor segment mutations and builds their mutation result envelopes.
/// </summary>
public sealed class TripEditorSegmentMutationService
{
    private readonly ApplicationDbContext _dbContext;

    /// <summary>
    /// Initializes a new segment mutation service for the editor API.
    /// </summary>
    public TripEditorSegmentMutationService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
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
        var trip = await LoadTripGraphAsync(tripId, userId, cancellationToken);
        if (trip == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.NotFound();
        }

        var parsed = await ParseAndValidateSaveAsync(requestBody, cancellationToken);
        if (parsed.ValidationErrors != null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(parsed.ValidationErrors);
        }

        var referenceErrors = ValidatePlaceReferences(parsed.Value!, trip);
        if (referenceErrors.Count > 0)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(referenceErrors);
        }

        var mode = await ResolveModeAsync(parsed.Value!.Mode, null, cancellationToken);
        if (mode == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(
                new Dictionary<string, string[]> { ["mode"] = ["Mode must match an active transport profile."] });
        }

        var segment = new Segment
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Trip = trip,
            TripId = trip.Id,
            DisplayOrder = NextSegmentOrder(trip),
        };
        Apply(segment, parsed.Value!);
        segment.Mode = mode.Value.Key;
        segment.TransportProfileId = mode.Value.ProfileId;
        _dbContext.Segments.Add(segment);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = await LoadSegmentDtoAsync(segment.Id, tripId, cancellationToken);
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
        CancellationToken cancellationToken)
    {
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
            return EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(parsed.ValidationErrors);
        }

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

        Apply(segment, parsed.Value!);
        segment.Mode = mode.Value.Key;
        segment.TransportProfileId = mode.Value.ProfileId;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = await LoadSegmentDtoAsync(segmentId, tripId, cancellationToken);
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
        var segmentDtos = await LoadSegmentDtosAsync(orderRequest.SegmentIds, tripId, cancellationToken);
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
            .Include(t => t.Segments)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);

    private async Task<Segment?> LoadOwnedSegmentAsync(Guid tripId, Guid segmentId, string userId, CancellationToken cancellationToken) =>
        await _dbContext.Segments
            .Include(s => s.Trip)
            .FirstOrDefaultAsync(s => s.Id == segmentId && s.TripId == tripId && s.Trip.UserId == userId, cancellationToken);

    private static Dictionary<string, string[]> ValidatePlaceReferences(EditorSegmentSaveRequest request, Trip trip)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var placeIds = trip.Regions.SelectMany(r => r.Places).Select(p => p.Id).ToHashSet();
        if (request.FromPlaceId.HasValue && !placeIds.Contains(request.FromPlaceId.Value))
        {
            errors["fromPlaceId"] = new[] { "From place must belong to this trip." };
        }

        if (request.ToPlaceId.HasValue && !placeIds.Contains(request.ToPlaceId.Value))
        {
            errors["toPlaceId"] = new[] { "To place must belong to this trip." };
        }

        return errors;
    }

    private static void Apply(Segment segment, EditorSegmentSaveRequest request)
    {
        segment.FromPlaceId = request.FromPlaceId;
        segment.ToPlaceId = request.ToPlaceId;
        segment.Mode = request.Mode;
        segment.EstimatedDistanceKm = request.EstimatedDistanceKm;
        segment.EstimatedDuration = request.EstimatedDurationMinutes.HasValue ? TimeSpan.FromMinutes(request.EstimatedDurationMinutes.Value) : null;
        segment.Notes = request.NotesHtml ?? string.Empty;
        segment.RouteGeometry = request.Route;
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

        var profileId = await _dbContext.TransportProfiles
            .Where(profile => profile.Key == resolved)
            .Select(profile => (Guid?)profile.Id)
            .SingleAsync(cancellationToken);
        return (resolved, profileId);
    }

    private async Task<EditorSegmentDto> LoadSegmentDtoAsync(Guid segmentId, Guid tripId, CancellationToken cancellationToken)
    {
        var segment = await _dbContext.Segments.AsNoTracking().SingleAsync(s => s.Id == segmentId, cancellationToken);
        return EditorTripStateMapper.ToSegment(tripId, segment);
    }

    private async Task<IReadOnlyList<EditorSegmentDto>> LoadSegmentDtosAsync(IReadOnlyList<Guid> ids, Guid tripId, CancellationToken cancellationToken)
    {
        var segments = await _dbContext.Segments.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync(cancellationToken);
        var byId = segments.ToDictionary(s => s.Id);
        return ids.Select(id => EditorTripStateMapper.ToSegment(tripId, byId[id])).ToList();
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
