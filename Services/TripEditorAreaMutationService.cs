using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

/// <summary>
/// Executes Trip Editor area mutations and builds their mutation result envelopes.
/// </summary>
public sealed class TripEditorAreaMutationService
{
    private readonly ApplicationDbContext _dbContext;

    /// <summary>
    /// Initializes a new area mutation service for the editor API.
    /// </summary>
    public TripEditorAreaMutationService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Creates an area in a normal owned region and appends it to that region.
    /// </summary>
    public async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorAreaDto>>> CreateAreaAsync(
        Guid tripId,
        Guid regionId,
        string userId,
        Stream requestBody,
        CancellationToken cancellationToken)
    {
        var trip = await LoadTripGraphAsync(tripId, userId, cancellationToken);
        if (trip == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorAreaDto>>.NotFound();
        }

        var region = trip.Regions.FirstOrDefault(r => r.Id == regionId);
        if (region == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorAreaDto>>.NotFound();
        }

        if (IsShadowRegion(region))
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorAreaDto>>.Forbidden("Areas cannot be created in the shadow region.");
        }

        var parsed = await ParseAndValidateCreateAsync(requestBody, cancellationToken);
        if (parsed.ValidationErrors != null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorAreaDto>>.ValidationFailed(parsed.ValidationErrors);
        }

        var area = new Area
        {
            Id = Guid.NewGuid(),
            Region = region,
            RegionId = region.Id,
            Name = parsed.Value!.Name,
            Notes = parsed.Value.NotesHtml ?? string.Empty,
            FillHex = parsed.Value.FillHex,
            Geometry = parsed.Value.Geometry,
            DisplayOrder = NextAreaOrder(region)
        };
        _dbContext.Areas.Add(area);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = await LoadAreaDtoAsync(area.Id, tripId, cancellationToken);
        var affected = await BuildAffectedAsync(tripId, new[] { dto }, new[] { region.Id }, cancellationToken);
        return EditorRegionMutationOutcome<EditorMutationResult<EditorAreaDto>>.Succeeded(
            new EditorMutationResult<EditorAreaDto>(true, dto, affected, EditorDeletedIdsDto.Empty, Array.Empty<EditorWarningDto>()));
    }

    /// <summary>
    /// Updates complete editable fields for one owned area.
    /// </summary>
    public async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorAreaDto>>> UpdateAreaAsync(
        Guid tripId,
        Guid areaId,
        string userId,
        Stream requestBody,
        CancellationToken cancellationToken)
    {
        var area = await LoadOwnedAreaAsync(tripId, areaId, userId, cancellationToken);
        if (area == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorAreaDto>>.NotFound();
        }

        var parsed = await ParseAndValidateUpdateAsync(requestBody, cancellationToken);
        if (parsed.ValidationErrors != null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorAreaDto>>.ValidationFailed(parsed.ValidationErrors);
        }

        area.Name = parsed.Value!.Name;
        area.Notes = parsed.Value.NotesHtml ?? string.Empty;
        area.FillHex = parsed.Value.FillHex;
        area.Geometry = parsed.Value.Geometry;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = await LoadAreaDtoAsync(areaId, tripId, cancellationToken);
        var affected = await BuildAffectedAsync(tripId, new[] { dto }, Array.Empty<Guid>(), cancellationToken);
        return EditorRegionMutationOutcome<EditorMutationResult<EditorAreaDto>>.Succeeded(
            new EditorMutationResult<EditorAreaDto>(true, dto, affected, EditorDeletedIdsDto.Empty, Array.Empty<EditorWarningDto>()));
    }

    /// <summary>
    /// Replaces only the polygon geometry for one owned area.
    /// </summary>
    public async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorAreaDto>>> UpdateAreaGeometryAsync(
        Guid tripId,
        Guid areaId,
        string userId,
        Stream requestBody,
        CancellationToken cancellationToken)
    {
        var area = await LoadOwnedAreaAsync(tripId, areaId, userId, cancellationToken);
        if (area == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorAreaDto>>.NotFound();
        }

        var parsed = await ParseAndValidateGeometryAsync(requestBody, cancellationToken);
        if (parsed.ValidationErrors != null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorAreaDto>>.ValidationFailed(parsed.ValidationErrors);
        }

        area.Geometry = parsed.Value!.Geometry;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = await LoadAreaDtoAsync(areaId, tripId, cancellationToken);
        var affected = await BuildAffectedAsync(tripId, new[] { dto }, Array.Empty<Guid>(), cancellationToken);
        return EditorRegionMutationOutcome<EditorMutationResult<EditorAreaDto>>.Succeeded(
            new EditorMutationResult<EditorAreaDto>(true, dto, affected, EditorDeletedIdsDto.Empty, Array.Empty<EditorWarningDto>()));
    }

    /// <summary>
    /// Deletes one owned area without touching sibling entities.
    /// </summary>
    public async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorAreaDeleteResult>>> DeleteAreaAsync(
        Guid tripId,
        Guid areaId,
        string userId,
        CancellationToken cancellationToken)
    {
        var area = await LoadOwnedAreaAsync(tripId, areaId, userId, cancellationToken);
        if (area == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorAreaDeleteResult>>.NotFound();
        }

        var regionId = area.RegionId;
        _dbContext.Areas.Remove(area);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await NormalizeAreaOrdersAsync(regionId, cancellationToken);

        var affected = await BuildAffectedAsync(tripId, Array.Empty<EditorAreaDto>(), new[] { regionId }, cancellationToken);
        var deletedIds = new EditorDeletedIdsDto(Array.Empty<Guid>(), Array.Empty<Guid>(), new[] { areaId }, Array.Empty<Guid>(), Array.Empty<string>());
        return EditorRegionMutationOutcome<EditorMutationResult<EditorAreaDeleteResult>>.Succeeded(
            new EditorMutationResult<EditorAreaDeleteResult>(true, new EditorAreaDeleteResult(areaId), affected, deletedIds, Array.Empty<EditorWarningDto>()));
    }

    /// <summary>
    /// Persists the complete desired order for areas inside one normal region.
    /// </summary>
    public async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorAreaOrderResult>>> OrderAreasAsync(
        Guid tripId,
        Guid regionId,
        string userId,
        Stream requestBody,
        CancellationToken cancellationToken)
    {
        var trip = await LoadTripGraphAsync(tripId, userId, cancellationToken);
        if (trip == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorAreaOrderResult>>.NotFound();
        }

        var region = trip.Regions.FirstOrDefault(r => r.Id == regionId);
        if (region == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorAreaOrderResult>>.NotFound();
        }

        if (IsShadowRegion(region))
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorAreaOrderResult>>.Forbidden("Areas in the shadow region cannot be reordered.");
        }

        var request = await ParseJsonBodyAsync(requestBody, "Area order request must be valid JSON.", cancellationToken);
        if (request.ValidationErrors != null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorAreaOrderResult>>.ValidationFailed(request.ValidationErrors);
        }

        if (!EditorAreaRequestParser.TryParseOrder(request.Value!.Value, out var orderRequest, out var errors))
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorAreaOrderResult>>.ValidationFailed(errors);
        }

        var currentIds = region.Areas.OrderBy(a => a.DisplayOrder).ThenBy(a => a.Name).Select(a => a.Id).ToList();
        if (orderRequest.AreaIds.Count != currentIds.Count
            || orderRequest.AreaIds.Distinct().Count() != orderRequest.AreaIds.Count
            || orderRequest.AreaIds.Any(id => !currentIds.Contains(id)))
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorAreaOrderResult>>.ValidationFailed(new Dictionary<string, string[]>
            {
                ["areaIds"] = new[] { "Area IDs must include every area in this region exactly once." }
            });
        }

        var areasById = region.Areas.ToDictionary(a => a.Id);
        for (var i = 0; i < orderRequest.AreaIds.Count; i++)
        {
            areasById[orderRequest.AreaIds[i]].DisplayOrder = i + 1;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var areaOrder = await LoadAreaOrderAsync(region.Id, cancellationToken);
        var areaDtos = await LoadAreaDtosAsync(orderRequest.AreaIds, tripId, cancellationToken);
        var affected = await BuildAffectedAsync(tripId, areaDtos, new[] { region.Id }, cancellationToken);
        var data = new EditorAreaOrderResult(region.Id, areaOrder);
        return EditorRegionMutationOutcome<EditorMutationResult<EditorAreaOrderResult>>.Succeeded(
            new EditorMutationResult<EditorAreaOrderResult>(true, data, affected, EditorDeletedIdsDto.Empty, Array.Empty<EditorWarningDto>()));
    }

    private async Task<(EditorAreaSaveRequest? Value, Dictionary<string, string[]>? ValidationErrors)> ParseAndValidateCreateAsync(Stream body, CancellationToken token)
    {
        var request = await ParseJsonBodyAsync(body, "Area create request must be valid JSON.", token);
        if (request.ValidationErrors != null)
        {
            return (null, request.ValidationErrors);
        }

        return EditorAreaRequestParser.TryParseCreate(request.Value!.Value, out var value, out var errors) ? (value, null) : (null, errors);
    }

    private async Task<(EditorAreaSaveRequest? Value, Dictionary<string, string[]>? ValidationErrors)> ParseAndValidateUpdateAsync(Stream body, CancellationToken token)
    {
        var request = await ParseJsonBodyAsync(body, "Area update request must be valid JSON.", token);
        if (request.ValidationErrors != null)
        {
            return (null, request.ValidationErrors);
        }

        return EditorAreaRequestParser.TryParseUpdate(request.Value!.Value, out var value, out var errors) ? (value, null) : (null, errors);
    }

    private async Task<(EditorAreaGeometryUpdateRequest? Value, Dictionary<string, string[]>? ValidationErrors)> ParseAndValidateGeometryAsync(Stream body, CancellationToken token)
    {
        var request = await ParseJsonBodyAsync(body, "Area geometry request must be valid JSON.", token);
        if (request.ValidationErrors != null)
        {
            return (null, request.ValidationErrors);
        }

        return EditorAreaRequestParser.TryParseGeometryUpdate(request.Value!.Value, out var value, out var errors) ? (value, null) : (null, errors);
    }

    private async Task<Trip?> LoadTripGraphAsync(Guid tripId, string userId, CancellationToken cancellationToken) =>
        await _dbContext.Trips
            .Include(t => t.Regions).ThenInclude(r => r.Areas)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);

    private async Task<Area?> LoadOwnedAreaAsync(Guid tripId, Guid areaId, string userId, CancellationToken cancellationToken) =>
        await _dbContext.Areas
            .Include(a => a.Region)
            .ThenInclude(r => r.Trip)
            .FirstOrDefaultAsync(a => a.Id == areaId && a.Region.TripId == tripId && a.Region.Trip.UserId == userId, cancellationToken);

    private async Task<EditorAreaDto> LoadAreaDtoAsync(Guid areaId, Guid tripId, CancellationToken cancellationToken)
    {
        var area = await _dbContext.Areas.AsNoTracking().SingleAsync(a => a.Id == areaId, cancellationToken);
        return EditorTripStateMapper.ToArea(tripId, area.RegionId, area);
    }

    private async Task<IReadOnlyList<EditorAreaDto>> LoadAreaDtosAsync(IReadOnlyList<Guid> ids, Guid tripId, CancellationToken cancellationToken)
    {
        var areas = await _dbContext.Areas.AsNoTracking().Where(a => ids.Contains(a.Id)).ToListAsync(cancellationToken);
        var byId = areas.ToDictionary(a => a.Id);
        return ids.Select(id => EditorTripStateMapper.ToArea(tripId, byId[id].RegionId, byId[id])).ToList();
    }

    private async Task<EditorAffectedSlicesDto> BuildAffectedAsync(
        Guid tripId,
        IReadOnlyList<EditorAreaDto> areas,
        IReadOnlyList<Guid> areaOrderRegionIds,
        CancellationToken cancellationToken)
    {
        var areaOrders = new Dictionary<Guid, IReadOnlyList<Guid>>();
        foreach (var regionId in areaOrderRegionIds.Distinct())
        {
            areaOrders[regionId] = await LoadAreaOrderAsync(regionId, cancellationToken);
        }

        return new EditorAffectedSlicesDto(
            null,
            Array.Empty<EditorRegionDto>(),
            null,
            Array.Empty<EditorPlaceDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            areas,
            areaOrders,
            Array.Empty<EditorSegmentDto>(),
            null,
            Array.Empty<EditorTagDto>(),
            null,
            null,
            null);
    }

    private async Task<IReadOnlyList<Guid>> LoadAreaOrderAsync(Guid regionId, CancellationToken cancellationToken) =>
        await _dbContext.Areas
            .AsNoTracking()
            .Where(a => a.RegionId == regionId)
            .OrderBy(a => a.DisplayOrder)
            .ThenBy(a => a.Name)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

    private async Task NormalizeAreaOrdersAsync(Guid regionId, CancellationToken cancellationToken)
    {
        var areas = await _dbContext.Areas.Where(a => a.RegionId == regionId).OrderBy(a => a.DisplayOrder).ThenBy(a => a.Name).ToListAsync(cancellationToken);
        for (var i = 0; i < areas.Count; i++)
        {
            areas[i].DisplayOrder = i + 1;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
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

    private static int NextAreaOrder(Region region) =>
        (region.Areas.Count == 0 ? 0 : region.Areas.Max(a => a.DisplayOrder ?? 0)) + 1;

    private static bool IsShadowRegion(Region region) =>
        region.DisplayOrder == 0
        && string.Equals(region.Name, EditorRegionRequestParser.ShadowRegionName, StringComparison.Ordinal);
}
