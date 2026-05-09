using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Parsers;

namespace Wayfarer.Services;

/// <summary>
/// Executes Trip Editor place mutations and builds their mutation result envelopes.
/// </summary>
public sealed class TripEditorPlaceMutationService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IWebHostEnvironment _environment;
    private readonly IIconColorProvider _iconColorProvider;
    private readonly ReverseGeocodingService _reverseGeocodingService;

    /// <summary>
    /// Initializes a new place mutation service for the editor API.
    /// </summary>
    public TripEditorPlaceMutationService(
        ApplicationDbContext dbContext,
        IWebHostEnvironment environment,
        IIconColorProvider iconColorProvider,
        ReverseGeocodingService reverseGeocodingService)
    {
        _dbContext = dbContext;
        _environment = environment;
        _iconColorProvider = iconColorProvider;
        _reverseGeocodingService = reverseGeocodingService;
    }

    /// <summary>
    /// Creates a place in a normal owned region and appends it to that region.
    /// </summary>
    public async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDto>>> CreatePlaceAsync(
        Guid tripId,
        Guid regionId,
        string userId,
        Stream requestBody,
        CancellationToken cancellationToken)
    {
        var trip = await LoadTripGraphAsync(tripId, userId, cancellationToken);
        if (trip == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDto>>.NotFound();
        }

        var region = trip.Regions.FirstOrDefault(r => r.Id == regionId);
        if (region == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDto>>.NotFound();
        }

        if (IsShadowRegion(region))
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDto>>.Forbidden("Places cannot be created in the shadow region from this editor action.");
        }

        var parsed = await ParseAndValidateCreateAsync(requestBody, cancellationToken);
        if (parsed.ValidationErrors != null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDto>>.ValidationFailed(parsed.ValidationErrors);
        }

        var place = new Place
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RegionId = region.Id,
            Region = region,
            Name = parsed.Value!.Name.Trim(),
            Notes = parsed.Value.NotesHtml ?? string.Empty,
            IconName = parsed.Value.IconName,
            MarkerColor = parsed.Value.MarkerColor,
            Location = ToPoint(parsed.Value.Location),
            DisplayOrder = NextPlaceOrder(region)
        };
        var address = await ResolveAddressAsync(userId, place.Id, parsed.Value.Address, parsed.Value.Location, parsed.Value.ReverseGeocode, cancellationToken);
        place.Address = address.Value;

        _dbContext.Places.Add(place);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = await LoadPlaceDtoAsync(place.Id, tripId, userId, cancellationToken);
        var affected = await BuildPlaceAffectedAsync(tripId, userId, new[] { dto }, new[] { region.Id }, null, null, true, cancellationToken);
        return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDto>>.Succeeded(
            new EditorMutationResult<EditorPlaceDto>(true, dto, affected, EditorDeletedIdsDto.Empty, address.Warnings));
    }

    /// <summary>
    /// Updates a place, optionally moving it to a different normal owned region.
    /// </summary>
    public async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDto>>> UpdatePlaceAsync(
        Guid tripId,
        Guid placeId,
        string userId,
        Stream requestBody,
        CancellationToken cancellationToken)
    {
        var trip = await LoadTripGraphAsync(tripId, userId, cancellationToken);
        if (trip == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDto>>.NotFound();
        }

        var place = trip.Regions.SelectMany(r => r.Places).FirstOrDefault(p => p.Id == placeId);
        if (place == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDto>>.NotFound();
        }

        var parsed = await ParseAndValidateUpdateAsync(requestBody, cancellationToken);
        if (parsed.ValidationErrors != null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDto>>.ValidationFailed(parsed.ValidationErrors);
        }

        var update = parsed.Value!;
        var targetRegion = trip.Regions.FirstOrDefault(r => r.Id == update.RegionId);
        if (targetRegion == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDto>>.NotFound();
        }

        if (IsShadowRegion(targetRegion))
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDto>>.Forbidden("Places cannot be moved to the shadow region from this editor action.");
        }

        var oldRegionId = place.RegionId;
        var oldLocation = place.Location == null ? null : new EditorCoordinateDto(place.Location.Y, place.Location.X);
        var locationChanged = !CoordinatesEqual(oldLocation, update.Location);
        var moved = oldRegionId != targetRegion.Id;

        place.Name = update.Name.Trim();
        place.Notes = update.NotesHtml ?? string.Empty;
        place.IconName = update.IconName;
        place.MarkerColor = update.MarkerColor;
        place.Location = ToPoint(update.Location);
        var address = await ResolveAddressAsync(userId, place.Id, update.Address, update.Location, update.ReverseGeocode, cancellationToken);
        place.Address = address.Value;

        if (moved)
        {
            place.RegionId = targetRegion.Id;
            place.Region = targetRegion;
            place.DisplayOrder = NextPlaceOrder(targetRegion);
        }

        var affectedSegments = locationChanged ? RewriteEndpointRoutes(trip, place.Id, update.Location) : Array.Empty<Segment>();
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (moved)
        {
            await NormalizePlaceOrdersAsync(oldRegionId, cancellationToken);
            await NormalizePlaceOrdersAsync(targetRegion.Id, cancellationToken);
        }

        var dto = await LoadPlaceDtoAsync(place.Id, tripId, userId, cancellationToken);
        var orderRegions = moved ? new[] { oldRegionId, targetRegion.Id } : Array.Empty<Guid>();
        var segmentOrder = locationChanged ? await LoadSegmentOrderAsync(tripId, userId, cancellationToken) : null;
        var segmentDtos = affectedSegments.Count > 0
            ? await LoadSegmentDtosAsync(affectedSegments.Select(s => s.Id).ToArray(), tripId, cancellationToken)
            : Array.Empty<EditorSegmentDto>();
        var affected = await BuildPlaceAffectedAsync(tripId, userId, new[] { dto }, orderRegions, segmentDtos, segmentOrder, locationChanged, cancellationToken);

        return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDto>>.Succeeded(
            new EditorMutationResult<EditorPlaceDto>(true, dto, affected, EditorDeletedIdsDto.Empty, address.Warnings));
    }

    /// <summary>
    /// Deletes a place and endpoint segments that reference it.
    /// </summary>
    public async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDeleteResult>>> DeletePlaceAsync(
        Guid tripId,
        Guid placeId,
        string userId,
        CancellationToken cancellationToken)
    {
        var trip = await LoadTripGraphAsync(tripId, userId, cancellationToken);
        if (trip == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDeleteResult>>.NotFound();
        }

        var place = trip.Regions.SelectMany(r => r.Places).FirstOrDefault(p => p.Id == placeId);
        if (place == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDeleteResult>>.NotFound();
        }

        var regionId = place.RegionId;
        var deletedSegmentIds = trip.Segments
            .Where(s => s.FromPlaceId == placeId || s.ToPlaceId == placeId)
            .OrderBy(s => s.DisplayOrder)
            .ThenBy(s => s.Id)
            .Select(s => s.Id)
            .ToList();

        _dbContext.Segments.RemoveRange(trip.Segments.Where(s => deletedSegmentIds.Contains(s.Id)));
        _dbContext.Places.Remove(place);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await NormalizePlaceOrdersAsync(regionId, cancellationToken);
        await NormalizeSegmentOrdersAsync(tripId, userId, cancellationToken);

        var affected = await BuildPlaceAffectedAsync(
            tripId,
            userId,
            Array.Empty<EditorPlaceDto>(),
            new[] { regionId },
            Array.Empty<EditorSegmentDto>(),
            await LoadSegmentOrderAsync(tripId, userId, cancellationToken),
            true,
            cancellationToken);
        var deletedIds = new EditorDeletedIdsDto(Array.Empty<Guid>(), new[] { placeId }, Array.Empty<Guid>(), deletedSegmentIds, Array.Empty<string>());

        return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDeleteResult>>.Succeeded(
            new EditorMutationResult<EditorPlaceDeleteResult>(true, new EditorPlaceDeleteResult(placeId), affected, deletedIds, Array.Empty<EditorWarningDto>()));
    }

    /// <summary>
    /// Persists the complete desired order for places inside one normal region.
    /// </summary>
    public async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceOrderResult>>> OrderPlacesAsync(
        Guid tripId,
        Guid regionId,
        string userId,
        Stream requestBody,
        CancellationToken cancellationToken)
    {
        var trip = await LoadTripGraphAsync(tripId, userId, cancellationToken);
        if (trip == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceOrderResult>>.NotFound();
        }

        var region = trip.Regions.FirstOrDefault(r => r.Id == regionId);
        if (region == null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceOrderResult>>.NotFound();
        }

        if (IsShadowRegion(region))
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceOrderResult>>.Forbidden("Places in the shadow region cannot be reordered from this editor action.");
        }

        var request = await ParseJsonBodyAsync(requestBody, "Place order request must be valid JSON.", cancellationToken);
        if (request.ValidationErrors != null)
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceOrderResult>>.ValidationFailed(request.ValidationErrors);
        }

        if (!EditorPlaceRequestParser.TryParseOrder(request.Value!.Value, out var orderRequest, out var errors))
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceOrderResult>>.ValidationFailed(errors);
        }

        var currentIds = region.Places.OrderBy(p => p.DisplayOrder).ThenBy(p => p.Name).Select(p => p.Id).ToList();
        if (orderRequest.PlaceIds.Count != currentIds.Count
            || orderRequest.PlaceIds.Distinct().Count() != orderRequest.PlaceIds.Count
            || orderRequest.PlaceIds.Any(id => !currentIds.Contains(id)))
        {
            return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceOrderResult>>.ValidationFailed(new Dictionary<string, string[]>
            {
                ["placeIds"] = new[] { "Place IDs must include every place in this region exactly once." }
            });
        }

        var placesById = region.Places.ToDictionary(p => p.Id);
        for (var i = 0; i < orderRequest.PlaceIds.Count; i++)
        {
            placesById[orderRequest.PlaceIds[i]].DisplayOrder = i + 1;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var placeOrder = await LoadPlaceOrderAsync(region.Id, cancellationToken);
        var placeDtos = await LoadPlaceDtosAsync(orderRequest.PlaceIds, tripId, userId, cancellationToken);
        var affected = await BuildPlaceAffectedAsync(tripId, userId, placeDtos, new[] { region.Id }, null, null, false, cancellationToken);
        var data = new EditorPlaceOrderResult(region.Id, placeOrder);

        return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceOrderResult>>.Succeeded(
            new EditorMutationResult<EditorPlaceOrderResult>(true, data, affected, EditorDeletedIdsDto.Empty, Array.Empty<EditorWarningDto>()));
    }

    private async Task<(EditorPlaceCreateRequest? Value, Dictionary<string, string[]>? ValidationErrors)> ParseAndValidateCreateAsync(
        Stream requestBody,
        CancellationToken cancellationToken)
    {
        var request = await ParseJsonBodyAsync(requestBody, "Place create request must be valid JSON.", cancellationToken);
        if (request.ValidationErrors != null)
        {
            return (null, request.ValidationErrors);
        }

        return EditorPlaceRequestParser.TryParseCreate(request.Value!.Value, IconNames(), MarkerColors(), out var value, out var errors)
            ? (value, null)
            : (null, errors);
    }

    private async Task<(EditorPlaceUpdateRequest? Value, Dictionary<string, string[]>? ValidationErrors)> ParseAndValidateUpdateAsync(
        Stream requestBody,
        CancellationToken cancellationToken)
    {
        var request = await ParseJsonBodyAsync(requestBody, "Place update request must be valid JSON.", cancellationToken);
        if (request.ValidationErrors != null)
        {
            return (null, request.ValidationErrors);
        }

        return EditorPlaceRequestParser.TryParseUpdate(request.Value!.Value, IconNames(), MarkerColors(), out var value, out var errors)
            ? (value, null)
            : (null, errors);
    }

    private async Task<(string Value, IReadOnlyList<EditorWarningDto> Warnings)> ResolveAddressAsync(
        string userId,
        Guid placeId,
        string? manualAddress,
        EditorCoordinateDto? location,
        bool reverseGeocode,
        CancellationToken cancellationToken)
    {
        var fallback = manualAddress?.Trim() ?? string.Empty;
        if (!reverseGeocode || location == null)
        {
            return (fallback, Array.Empty<EditorWarningDto>());
        }

        var token = await _dbContext.ApiTokens
            .AsNoTracking()
            .Where(t => t.UserId == userId && t.Name == "Mapbox")
            .Select(t => t.Token)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return (fallback, ReverseGeocodeWarning(placeId));
        }

        var result = await _reverseGeocodingService.GetReverseGeocodingDataAsync(location.Latitude, location.Longitude, token, "Mapbox");
        var address = string.IsNullOrWhiteSpace(result.FullAddress) ? result.Address : result.FullAddress;
        return string.IsNullOrWhiteSpace(address)
            ? (fallback, ReverseGeocodeWarning(placeId))
            : (address.Trim(), Array.Empty<EditorWarningDto>());
    }

    private static IReadOnlyList<EditorWarningDto> ReverseGeocodeWarning(Guid placeId) =>
        new[]
        {
            new EditorWarningDto(
                "reverse-geocode-unavailable",
                "Reverse geocoding was unavailable; the manual address value was saved.",
                "place",
                placeId.ToString())
        };

    private async Task<Trip?> LoadTripGraphAsync(Guid tripId, string userId, CancellationToken cancellationToken) =>
        await _dbContext.Trips
            .Include(t => t.Regions).ThenInclude(r => r.Places)
            .Include(t => t.Segments)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);

    private async Task<EditorAffectedSlicesDto> BuildPlaceAffectedAsync(
        Guid tripId,
        string userId,
        IReadOnlyList<EditorPlaceDto> places,
        IReadOnlyList<Guid> placeOrderRegionIds,
        IReadOnlyList<EditorSegmentDto>? segments,
        IReadOnlyList<Guid>? segmentOrder,
        bool includeVisitProgress,
        CancellationToken cancellationToken)
    {
        var placeOrders = new Dictionary<Guid, IReadOnlyList<Guid>>();
        foreach (var regionId in placeOrderRegionIds.Distinct())
        {
            placeOrders[regionId] = await LoadPlaceOrderAsync(regionId, cancellationToken);
        }

        return new EditorAffectedSlicesDto(
            null,
            Array.Empty<EditorRegionDto>(),
            null,
            places,
            placeOrders,
            Array.Empty<EditorAreaDto>(),
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            segments ?? Array.Empty<EditorSegmentDto>(),
            segmentOrder,
            Array.Empty<EditorTagDto>(),
            null,
            includeVisitProgress ? await LoadVisitProgressAsync(tripId, userId, cancellationToken) : null,
            null);
    }

    private async Task<EditorPlaceDto> LoadPlaceDtoAsync(Guid placeId, Guid tripId, string userId, CancellationToken cancellationToken)
    {
        var place = await _dbContext.Places
            .AsNoTracking()
            .SingleAsync(p => p.Id == placeId && p.UserId == userId, cancellationToken);
        var visits = await LoadVisitSummariesAsync(new[] { place }, userId, cancellationToken);
        return EditorTripStateMapper.ToPlace(tripId, place.RegionId, place, visits[place.Id]);
    }

    private async Task<IReadOnlyList<EditorPlaceDto>> LoadPlaceDtosAsync(IReadOnlyList<Guid> ids, Guid tripId, string userId, CancellationToken cancellationToken)
    {
        var places = await _dbContext.Places
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id) && p.UserId == userId)
            .ToListAsync(cancellationToken);
        var visits = await LoadVisitSummariesAsync(places, userId, cancellationToken);
        var byId = places.ToDictionary(p => p.Id);
        return ids.Select(id => EditorTripStateMapper.ToPlace(tripId, byId[id].RegionId, byId[id], visits[id])).ToList();
    }

    private async Task<IReadOnlyDictionary<Guid, EditorPlaceVisitSummaryDto>> LoadVisitSummariesAsync(
        IReadOnlyList<Place> places,
        string userId,
        CancellationToken cancellationToken)
    {
        var placeIds = places.Select(p => p.Id).ToArray();
        var visits = await _dbContext.PlaceVisitEvents
            .AsNoTracking()
            .Where(v => v.UserId == userId && v.PlaceId != null && placeIds.Contains(v.PlaceId.Value))
            .ToListAsync(cancellationToken);
        var visitsByPlaceId = visits.GroupBy(v => v.PlaceId!.Value).ToDictionary(g => g.Key, g => (IReadOnlyList<PlaceVisitEvent>)g.ToList());
        return EditorTripStateMapper.ToVisitSummaries(places, visitsByPlaceId);
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
        var summaries = await LoadVisitSummariesAsync(places.Select(p => p.Place).ToList(), userId, cancellationToken);
        var visitsByPlaceId = await LoadVisitsByPlaceIdAsync(summaries.Keys, userId, cancellationToken);
        return EditorTripStateMapper.ToVisitProgress(places, summaries, visitsByPlaceId);
    }

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PlaceVisitEvent>>> LoadVisitsByPlaceIdAsync(
        IEnumerable<Guid> placeIds,
        string userId,
        CancellationToken cancellationToken)
    {
        var ids = placeIds.ToArray();
        var visits = await _dbContext.PlaceVisitEvents
            .AsNoTracking()
            .Where(v => v.UserId == userId && v.PlaceId != null && ids.Contains(v.PlaceId.Value))
            .ToListAsync(cancellationToken);
        return visits.GroupBy(v => v.PlaceId!.Value).ToDictionary(g => g.Key, g => (IReadOnlyList<PlaceVisitEvent>)g.ToList());
    }

    private async Task<IReadOnlyList<EditorSegmentDto>> LoadSegmentDtosAsync(IReadOnlyList<Guid> ids, Guid tripId, CancellationToken cancellationToken)
    {
        var segments = await _dbContext.Segments.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync(cancellationToken);
        var byId = segments.ToDictionary(s => s.Id);
        return ids.Select(id => EditorTripStateMapper.ToSegment(tripId, byId[id])).ToList();
    }

    private async Task<IReadOnlyList<Guid>> LoadPlaceOrderAsync(Guid regionId, CancellationToken cancellationToken) =>
        await _dbContext.Places
            .AsNoTracking()
            .Where(p => p.RegionId == regionId)
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

    private async Task<IReadOnlyList<Guid>> LoadSegmentOrderAsync(Guid tripId, string userId, CancellationToken cancellationToken) =>
        await _dbContext.Segments
            .AsNoTracking()
            .Where(s => s.TripId == tripId && s.UserId == userId)
            .OrderBy(s => s.DisplayOrder)
            .ThenBy(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

    private static IReadOnlyList<Segment> RewriteEndpointRoutes(Trip trip, Guid placeId, EditorCoordinateDto? location)
    {
        var affected = trip.Segments.Where(s => s.FromPlaceId == placeId || s.ToPlaceId == placeId).ToList();
        foreach (var segment in affected)
        {
            if (segment.RouteGeometry == null)
            {
                continue;
            }

            if (location == null || segment.RouteGeometry.NumPoints < 2)
            {
                segment.RouteGeometry = null;
                continue;
            }

            var coordinates = segment.RouteGeometry.Coordinates.ToArray();
            var endpoint = new Coordinate(location.Longitude, location.Latitude);
            if (segment.FromPlaceId == placeId)
            {
                coordinates[0] = endpoint;
            }

            if (segment.ToPlaceId == placeId)
            {
                coordinates[^1] = endpoint;
            }

            segment.RouteGeometry = new LineString(coordinates) { SRID = 4326 };
        }

        return affected;
    }

    private async Task NormalizePlaceOrdersAsync(Guid regionId, CancellationToken cancellationToken)
    {
        var places = await _dbContext.Places
            .Where(p => p.RegionId == regionId)
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .ToListAsync(cancellationToken);
        for (var i = 0; i < places.Count; i++)
        {
            places[i].DisplayOrder = i + 1;
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
            segments[i].DisplayOrder = i + 1;
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

    private IReadOnlySet<string> IconNames() => ReadIconNames().ToHashSet(StringComparer.Ordinal);

    private IReadOnlySet<string> MarkerColors() =>
        (_iconColorProvider.GetAvailableColors()?.Backgrounds ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);

    private IReadOnlyList<string> ReadIconNames()
    {
        var iconDir = Path.Combine(_environment.WebRootPath, "icons", "wayfarer-map-icons", "dist", "marker");
        return Directory.Exists(iconDir)
            ? Directory.GetFiles(iconDir, "*.svg").Select(Path.GetFileNameWithoutExtension).Where(n => n != null).Cast<string>().OrderBy(n => n).ToList()
            : Array.Empty<string>();
    }

    private static int NextPlaceOrder(Region region) =>
        (region.Places.Count == 0 ? 0 : region.Places.Max(p => p.DisplayOrder ?? 0)) + 1;

    private static Point? ToPoint(EditorCoordinateDto? coordinate) =>
        coordinate == null ? null : new Point(coordinate.Longitude, coordinate.Latitude) { SRID = 4326 };

    private static bool CoordinatesEqual(EditorCoordinateDto? left, EditorCoordinateDto? right) =>
        left == null && right == null
        || left != null && right != null && left.Latitude.Equals(right.Latitude) && left.Longitude.Equals(right.Longitude);

    private static bool IsShadowRegion(Region region) =>
        region.DisplayOrder == 0
        && string.Equals(region.Name, EditorRegionRequestParser.ShadowRegionName, StringComparison.Ordinal);
}
