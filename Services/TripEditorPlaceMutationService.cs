using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Services;

/// <summary>
/// Executes Trip Editor place mutations and builds their mutation result envelopes.
/// </summary>
public sealed class TripEditorPlaceMutationService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IWebHostEnvironment _environment;
    private readonly IIconColorProvider _iconColorProvider;
    private readonly ILogger<TripEditorPlaceMutationService> _logger;
    private readonly TripEditorPlaceMutationReader _reader;
    private readonly PlaceRegionLifecycleService _lifecycle;
    private readonly ReverseGeocodingService _reverseGeocodingService;

    /// <summary>
    /// Initializes a new place mutation service for the editor API.
    /// </summary>
    public TripEditorPlaceMutationService(
        ApplicationDbContext dbContext,
        IWebHostEnvironment environment,
        IIconColorProvider iconColorProvider,
        ReverseGeocodingService reverseGeocodingService,
        TripEditorPlaceMutationReader? reader = null,
        ILogger<TripEditorPlaceMutationService>? logger = null,
        PlaceRegionLifecycleService? lifecycle = null)
    {
        _dbContext = dbContext;
        _environment = environment;
        _iconColorProvider = iconColorProvider;
        _reverseGeocodingService = reverseGeocodingService;
        _reader = reader ?? new TripEditorPlaceMutationReader(dbContext);
        _lifecycle = lifecycle ?? new PlaceRegionLifecycleService(
            dbContext,
            new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider()));
        _logger = logger ?? NullLogger<TripEditorPlaceMutationService>.Instance;
    }

    /// <summary>
    /// Creates a place in an owned region, including the built-in unassigned region, and appends it to that region.
    /// </summary>
    public async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDto>>> CreatePlaceAsync(
        Guid tripId,
        Guid regionId,
        string userId,
        Stream requestBody,
        CancellationToken cancellationToken)
    {
        if (!await _dbContext.Regions.AsNoTracking().AnyAsync(r => r.Id == regionId && r.TripId == tripId && r.UserId == userId && r.Trip.UserId == userId, cancellationToken))
            return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDto>>.NotFound();
        var request = await CreationConditionalRequest.ReadAsync(requestBody, true, cancellationToken);
        if (request.RegionId != regionId) throw CreationConditionalRequest.Invalid("The original destination must match the path.");
        CreationConditionalRequest.ValidateValue(request.Value, true, true);
        if (!EditorPlaceRequestParser.TryParseCreate(request.Value, IconNames(), MarkerColors(), out var value, out var errors))
            throw new TripMetadataMutationRejected(400, "mutation-request-invalid", "Check the creation fields.", errors);
        var place = new Place
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RegionId = regionId,
            Name = value.Name.Trim(),
            Notes = value.NotesHtml ?? string.Empty,
            IconName = value.IconName,
            MarkerColor = value.MarkerColor,
            Location = ToPoint(value.Location)
        };
        var address = await ResolveAddressAsync(userId, place.Id, value.Address, value.Location, value.ReverseGeocode, cancellationToken);
        place.Address = address.Value;
        ApplyAddressEnrichment(place, address);

        return await new CheckedCreationService(_dbContext).PlaceAsync(tripId, userId, request, place, async created =>
        {
            var dto = await _reader.LoadPlaceDtoAsync(place.Id, tripId, userId, cancellationToken);
            var affected = await _reader.BuildAffectedAsync(tripId, userId, new[] { dto }, new[] { created.RegionId }, null, null, true, cancellationToken);
            return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDto>>.Succeeded(
                new EditorMutationResult<EditorPlaceDto>(true, dto, affected, EditorDeletedIdsDto.Empty, address.Warnings) { Contract = "bd2-1" });
        }, null, cancellationToken);
    }

    /// <summary>
    /// Updates a place, optionally moving it to a different owned region.
    /// </summary>
    public async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDto>>> UpdatePlaceAsync(
        Guid tripId,
        Guid placeId,
        string userId,
        Stream requestBody,
        CancellationToken cancellationToken)
    {
        var result = await ApplyStructureAsync(tripId, placeId, userId, requestBody, "web-update", cancellationToken);
        return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDto>>.Succeeded(
            new(true, (EditorPlaceDto)result.Data, result.Affected, result.DeletedIds, result.Warnings) { Contract = "bd2-1" });
    }

    /// <summary>
    /// Deletes a place and endpoint segments that reference it.
    /// </summary>
    public async Task<EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDeleteResult>>> DeletePlaceAsync(
        Guid tripId,
        Guid placeId,
        string userId,
        string? confirmationToken,
        CancellationToken cancellationToken)
    {
        var result = await _lifecycle.DeletePlaceAsync(tripId, placeId, userId, confirmationToken, cancellationToken);
        if (result.Warning != null)
            return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDeleteResult>>.Conflicted(result.Warning);
        if (!result.Succeeded || !result.RegionId.HasValue)
            return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDeleteResult>>.NotFound();

        var survivingDtos = await _reader.LoadSegmentDtosAsync(result.SurvivingSegments.Select(segment => segment.Id).ToArray(), tripId, userId, cancellationToken);
        var affected = await _reader.BuildAffectedAsync(
            tripId,
            userId,
            Array.Empty<EditorPlaceDto>(),
            new[] { result.RegionId.Value },
            survivingDtos,
            await _reader.LoadSegmentOrderAsync(tripId, userId, cancellationToken),
            true,
            cancellationToken);
        var deletedIds = new EditorDeletedIdsDto(Array.Empty<Guid>(), new[] { placeId }, Array.Empty<Guid>(), result.DeletedSegmentIds, Array.Empty<string>());

        return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDeleteResult>>.Succeeded(
            new EditorMutationResult<EditorPlaceDeleteResult>(true, new EditorPlaceDeleteResult(placeId), affected, deletedIds, Array.Empty<EditorWarningDto>()));
    }

    /// <summary>Compatibility overload for callers without an HTTP confirmation header.</summary>
    public Task<EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceDeleteResult>>> DeletePlaceAsync(
        Guid tripId,
        Guid placeId,
        string userId,
        CancellationToken cancellationToken) =>
        DeletePlaceAsync(tripId, placeId, userId, null, cancellationToken);

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
        var result = await ApplyStructureAsync(tripId, regionId, userId, requestBody, "order", cancellationToken);
        return EditorRegionMutationOutcome<EditorMutationResult<EditorPlaceOrderResult>>.Succeeded(
            new(true, (EditorPlaceOrderResult)result.Data, result.Affected, result.DeletedIds, result.Warnings) { Contract = "bd2-1" });
    }

    /// <summary>Prepares the same lifecycle operation without adopting a newer edit base or contacting a provider.</summary>
    internal async Task<object> PrepareStructureAsync(Guid tripId, Guid targetId, string userId, Stream body, string operation, CancellationToken token)
    {
        await RequireStructureOwnerAsync(tripId, targetId, userId, operation, token);
        var request = await PlaceStructuralRequest.ReadAsync(body, operation, true, token);
        return await _lifecycle.PreparePlaceStructureAsync(tripId, targetId, userId, request, IconNames(), MarkerColors(), token);
    }

    /// <summary>Freezes only an explicit Web provider choice; all comparisons and writes stay in the lifecycle transaction.</summary>
    private async Task<EditorMutationResult<object>> ApplyStructureAsync(Guid tripId, Guid targetId, string userId,
        Stream body, string operation, CancellationToken token)
    {
        await RequireStructureOwnerAsync(tripId, targetId, userId, operation, token);
        var request = await PlaceStructuralRequest.ReadAsync(body, operation, true, token);
        async Task<PlaceStructuralAddress> Resolve(Place place)
        {
            var location = request.Changes.TryGetProperty("location", out _) ? request.Location : place.Location;
            var manual = request.Changes.TryGetProperty("address", out var address) ? address.GetString() : place.Address;
            var result = await ResolveAddressAsync(userId, targetId, manual, PlaceStructuralRequest.Pair(location), true, token);
            return new(result.Value, result.Metadata.HasValue ? new(EditorPlaceMetadataPolicy.Replace, result.Metadata.Value)
                : new(EditorPlaceMetadataPolicy.RetainCompatible), result.Warnings);
        }
        var explicitProvider = request.Changes.ValueKind == JsonValueKind.Object
            && request.Changes.TryGetProperty("reverseGeocode", out var reverse) && reverse.GetBoolean();
        return await _lifecycle.ApplyPlaceStructureAsync(tripId, targetId, userId, request, IconNames(), MarkerColors(), null,
            explicitProvider ? Resolve : null, token);
    }

    /// <summary>Conceals missing/foreign targets before parsing any body or contacting a provider.</summary>
    private async Task RequireStructureOwnerAsync(Guid tripId, Guid targetId, string userId, string operation, CancellationToken token)
    {
        var owned = operation == "order"
            ? await _dbContext.Regions.AsNoTracking().AnyAsync(r => r.Id == targetId && r.TripId == tripId && r.UserId == userId && r.Trip.UserId == userId, token)
            : await _dbContext.Places.AsNoTracking().AnyAsync(p => p.Id == targetId && p.UserId == userId && p.Region.TripId == tripId && p.Region.UserId == userId && p.Region.Trip.UserId == userId, token);
        if (!owned) throw new TripMetadataMutationRejected(404, "mutation-scope-unavailable", "Place or Region not found.");
    }

    /// <summary>Resolves the address once and freezes successful provenance before lifecycle retries.</summary>
    private async Task<(string Value, IReadOnlyList<EditorWarningDto> Warnings, ResolvedFeatureTuple? Metadata)> ResolveAddressAsync(
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
            return (fallback, Array.Empty<EditorWarningDto>(), null);
        }
        var result = await _reverseGeocodingService.EnrichAsync(userId, location.Latitude, location.Longitude,
            ReverseGeocodingIntent.PlaceAddress, cancellationToken);
        var address = result.Value == null ? null
            : string.IsNullOrWhiteSpace(result.Value.FullAddress) ? result.Value.Address : result.Value.FullAddress;
        if (string.IsNullOrWhiteSpace(address)) return (fallback, ReverseGeocodeWarning(placeId), null);
        var providerKey = result.Authority?.ProviderKey ?? "mapbox";
        var metadata = new ResolvedFeatureTuple(result.Value!.ResolvedFeatureName, result.Value.ResolvedFeatureType,
            providerKey, providerKey == "geoapify" ? "persistent" : "permanent", DateTimeOffset.UtcNow);
        return (address.Trim(), Array.Empty<EditorWarningDto>(), metadata);
    }

    /// <summary>Initializes metadata only for a new Place; existing rows are owned by lifecycle.</summary>
    private static void ApplyAddressEnrichment(Place place,
        (string Value, IReadOnlyList<EditorWarningDto> Warnings, ResolvedFeatureTuple? Metadata) result)
    {
        var metadata = result.Metadata.GetValueOrDefault();
        place.AddressEnrichmentProvider = metadata.Provider;
        place.AddressEnrichmentStorageMode = metadata.StorageMode;
        place.AddressEnrichedAt = metadata.EnrichedAt;
        place.ResolvedFeatureName = metadata.Name;
        place.ResolvedFeatureType = metadata.Type;
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

    private static Point? ToPoint(EditorCoordinateDto? coordinate) =>
        coordinate == null ? null : new Point(coordinate.Longitude, coordinate.Latitude) { SRID = 4326 };

    private static bool IsShadowRegion(Region region) =>
        region.DisplayOrder == 0
        && string.Equals(region.Name, EditorRegionRequestParser.ShadowRegionName, StringComparison.Ordinal);
}
