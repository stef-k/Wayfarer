using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

/// <summary>
/// Executes Trip Editor region mutations and builds their mutation result envelopes.
/// </summary>
public sealed class TripEditorRegionMutationService
{
    private readonly ApplicationDbContext _dbContext;

    /// <summary>
    /// Initializes a new region mutation service for the editor API.
    /// </summary>
    public TripEditorRegionMutationService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Creates a normal region for an owned trip.
    /// </summary>
    public async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorRegionDto>>> CreateRegionAsync(
        Guid tripId,
        string userId,
        Stream requestBody,
        CancellationToken cancellationToken)
    {
        var trip = await _dbContext.Trips
            .Include(t => t.Regions)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);
        if (trip == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorRegionDto>>.NotFound();
        }

        var request = await ParseJsonBodyAsync(requestBody, "Region save request must be valid JSON.", cancellationToken);
        if (request.ValidationErrors != null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorRegionDto>>.ValidationFailed(request.ValidationErrors);
        }

        if (!EditorRegionRequestParser.TryParseSave(request.Value!.Value, out var update, out var errors))
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorRegionDto>>.ValidationFailed(errors);
        }

        var normalRegions = trip.Regions.Where(r => !IsShadowRegion(r)).ToList();
        var region = new Region
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            Trip = trip,
            UserId = userId,
            Name = update.Name.Trim(),
            Notes = update.NotesHtml ?? string.Empty,
            CoverImageUrl = NormalizeOptionalUrl(update.CoverImage?.RawUrl),
            Center = ToPoint(update.Center),
            DisplayOrder = normalRegions.Count == 0 ? 1 : normalRegions.Max(r => r.DisplayOrder) + 1
        };

        _dbContext.Regions.Add(region);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var regionOrder = await LoadRegionOrderAsync(tripId, userId, cancellationToken);
        var dto = EditorTripStateMapper.ToRegion(region);
        var affected = new EditorAffectedSlicesDto(
            null,
            new[] { dto },
            regionOrder,
            Array.Empty<EditorPlaceDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>> { [region.Id] = Array.Empty<Guid>() },
            Array.Empty<EditorAreaDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>> { [region.Id] = Array.Empty<Guid>() },
            Array.Empty<EditorSegmentDto>(),
            null,
            Array.Empty<EditorTagDto>(),
            null,
            null,
            null);

        return EditorRegionMutationOutcome<EditorMutationResult<EditorRegionDto>>.Succeeded(
            new EditorMutationResult<EditorRegionDto>(true, dto, affected, EditorDeletedIdsDto.Empty, Array.Empty<EditorWarningDto>()));
    }

    /// <summary>
    /// Updates a normal region for an owned trip.
    /// </summary>
    public async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorRegionDto>>> UpdateRegionAsync(
        Guid tripId,
        Guid regionId,
        string userId,
        Stream requestBody,
        CancellationToken cancellationToken)
    {
        var trip = await _dbContext.Trips
            .Include(t => t.Regions)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);
        if (trip == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorRegionDto>>.NotFound();
        }

        var region = trip.Regions.FirstOrDefault(r => r.Id == regionId);
        if (region == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorRegionDto>>.NotFound();
        }

        if (IsShadowRegion(region))
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorRegionDto>>.Forbidden("The shadow region cannot be updated.");
        }

        var request = await ParseJsonBodyAsync(requestBody, "Region save request must be valid JSON.", cancellationToken);
        if (request.ValidationErrors != null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorRegionDto>>.ValidationFailed(request.ValidationErrors);
        }

        if (!EditorRegionRequestParser.TryParseSave(request.Value!.Value, out var update, out var errors))
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorRegionDto>>.ValidationFailed(errors);
        }

        region.Name = update.Name.Trim();
        region.Notes = update.NotesHtml ?? string.Empty;
        region.CoverImageUrl = NormalizeOptionalUrl(update.CoverImage?.RawUrl);
        region.Center = ToPoint(update.Center);

        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = EditorTripStateMapper.ToRegion(region);
        var affected = new EditorAffectedSlicesDto(
            null,
            new[] { dto },
            null,
            Array.Empty<EditorPlaceDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            Array.Empty<EditorAreaDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            Array.Empty<EditorSegmentDto>(),
            null,
            Array.Empty<EditorTagDto>(),
            null,
            null,
            null);

        return EditorRegionMutationOutcome<EditorMutationResult<EditorRegionDto>>.Succeeded(
            new EditorMutationResult<EditorRegionDto>(true, dto, affected, EditorDeletedIdsDto.Empty, Array.Empty<EditorWarningDto>()));
    }

    /// <summary>
    /// Deletes a normal region and returns authoritative deleted IDs and affected slices.
    /// </summary>
    public async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorRegionDto?>>> DeleteRegionAsync(
        Guid tripId,
        Guid regionId,
        string userId,
        CancellationToken cancellationToken)
    {
        var trip = await _dbContext.Trips
            .Include(t => t.Regions).ThenInclude(r => r.Places)
            .Include(t => t.Regions).ThenInclude(r => r.Areas)
            .Include(t => t.Segments)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);
        if (trip == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorRegionDto?>>.NotFound();
        }

        var region = trip.Regions.FirstOrDefault(r => r.Id == regionId);
        if (region == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorRegionDto?>>.NotFound();
        }

        if (IsShadowRegion(region))
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorRegionDto?>>.Forbidden("The shadow region cannot be deleted.");
        }

        var deletedPlaceIds = region.Places.OrderBy(p => p.DisplayOrder).ThenBy(p => p.Id).Select(p => p.Id).ToList();
        var deletedAreaIds = region.Areas.OrderBy(a => a.DisplayOrder).ThenBy(a => a.Id).Select(a => a.Id).ToList();
        var deletedSegmentIds = trip.Segments
            .Where(s => (s.FromPlaceId.HasValue && deletedPlaceIds.Contains(s.FromPlaceId.Value))
                || (s.ToPlaceId.HasValue && deletedPlaceIds.Contains(s.ToPlaceId.Value)))
            .OrderBy(s => s.DisplayOrder)
            .ThenBy(s => s.Id)
            .Select(s => s.Id)
            .ToList();

        var deletedSegments = trip.Segments.Where(s => deletedSegmentIds.Contains(s.Id)).ToList();
        _dbContext.Segments.RemoveRange(deletedSegments);
        _dbContext.Areas.RemoveRange(region.Areas);
        _dbContext.Places.RemoveRange(region.Places);
        _dbContext.Regions.Remove(region);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await NormalizeRegionOrdersAsync(tripId, userId, cancellationToken);
        await NormalizeSegmentOrdersAsync(tripId, userId, cancellationToken);

        var regionOrder = await LoadRegionOrderAsync(tripId, userId, cancellationToken);
        var segmentOrder = await LoadSegmentOrderAsync(tripId, userId, cancellationToken);
        var visitProgress = await LoadVisitProgressAsync(tripId, userId, cancellationToken);
        var affected = new EditorAffectedSlicesDto(
            null,
            Array.Empty<EditorRegionDto>(),
            regionOrder,
            Array.Empty<EditorPlaceDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            Array.Empty<EditorAreaDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            Array.Empty<EditorSegmentDto>(),
            segmentOrder,
            Array.Empty<EditorTagDto>(),
            null,
            visitProgress,
            null);
        var deletedIds = new EditorDeletedIdsDto(new[] { regionId }, deletedPlaceIds, deletedAreaIds, deletedSegmentIds, Array.Empty<string>());

        return EditorRegionMutationOutcome<EditorMutationResult<EditorRegionDto?>>.Succeeded(
            new EditorMutationResult<EditorRegionDto?>(true, null, affected, deletedIds, Array.Empty<EditorWarningDto>()));
    }

    /// <summary>
    /// Persists the complete desired order for normal regions.
    /// </summary>
    public async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorRegionOrderResult>>> OrderRegionsAsync(
        Guid tripId,
        string userId,
        Stream requestBody,
        CancellationToken cancellationToken)
    {
        var trip = await _dbContext.Trips
            .Include(t => t.Regions)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);
        if (trip == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorRegionOrderResult>>.NotFound();
        }

        var request = await ParseJsonBodyAsync(requestBody, "Region order request must be valid JSON.", cancellationToken);
        if (request.ValidationErrors != null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorRegionOrderResult>>.ValidationFailed(request.ValidationErrors);
        }

        if (!EditorRegionRequestParser.TryParseOrder(request.Value!.Value, out var orderRequest, out var errors))
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorRegionOrderResult>>.ValidationFailed(errors);
        }

        var shadow = trip.Regions.FirstOrDefault(IsShadowRegion);
        if (shadow != null && orderRequest.RegionIds.Contains(shadow.Id))
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorRegionOrderResult>>.Forbidden("The shadow region cannot be reordered.");
        }

        var normalRegions = trip.Regions.Where(r => !IsShadowRegion(r)).OrderBy(r => r.DisplayOrder).ThenBy(r => r.Name).ToList();
        var normalIds = normalRegions.Select(r => r.Id).ToList();
        if (orderRequest.RegionIds.Count != normalIds.Count
            || orderRequest.RegionIds.Distinct().Count() != orderRequest.RegionIds.Count
            || orderRequest.RegionIds.Any(id => !normalIds.Contains(id)))
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorRegionOrderResult>>.ValidationFailed(new Dictionary<string, string[]>
            {
                ["regionIds"] = new[] { "Region IDs must include every normal region in this trip exactly once." }
            });
        }

        if (shadow != null)
        {
            shadow.DisplayOrder = 0;
        }

        var byId = normalRegions.ToDictionary(r => r.Id);
        for (var i = 0; i < orderRequest.RegionIds.Count; i++)
        {
            byId[orderRequest.RegionIds[i]].DisplayOrder = i + 1;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var regionOrder = await LoadRegionOrderAsync(tripId, userId, cancellationToken);
        var updatedRegions = await LoadRegionsByIdsAsync(orderRequest.RegionIds, cancellationToken);
        var affected = new EditorAffectedSlicesDto(
            null,
            updatedRegions,
            regionOrder,
            Array.Empty<EditorPlaceDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            Array.Empty<EditorAreaDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            Array.Empty<EditorSegmentDto>(),
            null,
            Array.Empty<EditorTagDto>(),
            null,
            null,
            null);
        var data = new EditorRegionOrderResult(regionOrder);

        return EditorRegionMutationOutcome<EditorMutationResult<EditorRegionOrderResult>>.Succeeded(
            new EditorMutationResult<EditorRegionOrderResult>(true, data, affected, EditorDeletedIdsDto.Empty, Array.Empty<EditorWarningDto>()));
    }

    private async Task<IReadOnlyList<Guid>> LoadRegionOrderAsync(Guid tripId, string userId, CancellationToken cancellationToken) =>
        await _dbContext.Regions
            .AsNoTracking()
            .Where(r => r.TripId == tripId && r.UserId == userId)
            .OrderBy(r => r.DisplayOrder)
            .ThenBy(r => r.Name)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

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

    private async Task<IReadOnlyList<Guid>> LoadSegmentOrderAsync(Guid tripId, string userId, CancellationToken cancellationToken) =>
        await _dbContext.Segments
            .AsNoTracking()
            .Where(s => s.TripId == tripId && s.UserId == userId)
            .OrderBy(s => s.DisplayOrder)
            .ThenBy(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

    private async Task<IReadOnlyList<EditorRegionDto>> LoadRegionsByIdsAsync(IReadOnlyList<Guid> regionIds, CancellationToken cancellationToken)
    {
        var regions = await _dbContext.Regions
            .AsNoTracking()
            .Where(r => regionIds.Contains(r.Id))
            .ToListAsync(cancellationToken);
        var byId = regions.ToDictionary(r => r.Id);

        return regionIds.Select(id => EditorTripStateMapper.ToRegion(byId[id])).ToList();
    }

    private async Task<EditorVisitProgressDto> LoadVisitProgressAsync(Guid tripId, string userId, CancellationToken cancellationToken)
    {
        var regions = await _dbContext.Regions
            .AsNoTracking()
            .Include(r => r.Places)
            .Where(r => r.TripId == tripId && r.UserId == userId)
            .OrderBy(r => r.DisplayOrder)
            .ThenBy(r => r.Name)
            .ToListAsync(cancellationToken);
        var places = regions.SelectMany(r => r.Places.Select(p => (Region: r, Place: p))).ToList();
        var placeIds = places.Select(p => p.Place.Id).ToArray();
        var visits = await _dbContext.PlaceVisitEvents
            .AsNoTracking()
            .Where(v => v.UserId == userId && v.PlaceId != null && placeIds.Contains(v.PlaceId.Value))
            .ToListAsync(cancellationToken);
        var visitsByPlaceId = visits
            .GroupBy(v => v.PlaceId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PlaceVisitEvent>)g.ToList());
        var summaries = EditorTripStateMapper.ToVisitSummaries(places.Select(p => p.Place), visitsByPlaceId);

        return EditorTripStateMapper.ToVisitProgress(places, summaries, visitsByPlaceId);
    }

    private async Task NormalizeRegionOrdersAsync(Guid tripId, string userId, CancellationToken cancellationToken)
    {
        var regions = await _dbContext.Regions
            .Where(r => r.TripId == tripId && r.UserId == userId)
            .OrderBy(r => r.DisplayOrder)
            .ThenBy(r => r.Name)
            .ToListAsync(cancellationToken);
        var shadow = regions.FirstOrDefault(IsShadowRegion);
        if (shadow != null)
        {
            shadow.DisplayOrder = 0;
        }

        var order = 1;
        foreach (var region in regions.Where(r => !IsShadowRegion(r)))
        {
            region.DisplayOrder = order++;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task NormalizeSegmentOrdersAsync(Guid tripId, string userId, CancellationToken cancellationToken)
    {
        var segments = await _dbContext.Segments
            .Where(s => s.TripId == tripId && s.UserId == userId)
            .OrderBy(s => s.DisplayOrder)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);
        for (var i = 0; i < segments.Count; i++)
        {
            segments[i].DisplayOrder = i;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string? NormalizeOptionalUrl(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Point? ToPoint(EditorCoordinateDto? coordinate) =>
        coordinate == null ? null : new Point(coordinate.Longitude, coordinate.Latitude) { SRID = 4326 };

    private static bool IsShadowRegion(Region region) =>
        region.DisplayOrder == 0
        && string.Equals(region.Name, EditorRegionRequestParser.ShadowRegionName, StringComparison.Ordinal);
}
