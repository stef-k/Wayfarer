using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services.TripMetadataReceipts;

namespace Wayfarer.Services;

public sealed partial class TripEditorPlaceMutationService
{
    /// <summary>Completes a checked append using the original executor's frozen result for explicitly metered saves.</summary>
    internal async Task<TripStructureResult> CreateWithReceiptAsync(TripStructureWrite write)
    {
        var (request, value) = await ReadProtectedCreationAsync(write);
        var provider = write.Provider;
        if (value.ReverseGeocode && provider == null) throw TripMetadataOperations.Unavailable();
        var address = provider?.Address;
        var place = new Place
        {
            Id = provider?.PlaceId ?? Guid.NewGuid(), UserId = write.Subject, RegionId = write.TargetId,
            Name = value.Name.Trim(), Notes = value.NotesHtml ?? string.Empty, IconName = value.IconName,
            MarkerColor = value.MarkerColor, Location = ToPoint(value.Location),
            Address = address?.Address ?? value.Address?.Trim() ?? string.Empty
        };
        var replaced = address?.Metadata.Policy == EditorPlaceMetadataPolicy.Replace;
        if (replaced) ApplyAddressEnrichment(place, (place.Address, address!.Warnings, address.Metadata.Replacement));
        var geocode = provider == null ? "notRequested" : replaced ? "resolved" : "fallback";
        return await new TripStructureCollections(_dbContext).CreatePlaceAsync(write, request, place, geocode, replaced ? "replaced" : "none");
    }

    /// <summary>Preflights checked creation against the exact stored siblings before claiming provider ownership.</summary>
    internal async Task<Func<Task<TripPlaceProviderResult>>> PrepareReceiptCreationProviderAsync(TripStructureWrite write,
        Func<CancellationToken, Task> checkAuthority)
    {
        var (request, value) = await ReadProtectedCreationAsync(write);
        var owned = await _dbContext.Regions.AsNoTracking().AnyAsync(row => row.Id == write.TargetId && row.TripId == write.TripId
            && row.UserId == write.Subject && row.Trip.UserId == write.Subject, write.Token);
        if (!owned) throw new TripMetadataMutationRejected(404, "mutation-scope-unavailable", "Place creation scope not found.");
        var siblings = await _dbContext.Places.AsNoTracking().Where(row => row.RegionId == write.TargetId)
            .OrderBy(row => row.Id).Select(row => new { row.Id, row.DisplayOrder, row.UserId }).ToArrayAsync(write.Token);
        if (siblings.Any(row => row.UserId != write.Subject) || !siblings.Select(row => new CreationPlaceOriginal(row.Id, row.DisplayOrder))
            .SequenceEqual(request.Places!.OrderBy(row => row.Id)))
            throw new TripMetadataMutationRejected(409, "mutation-base-stale", "The original Place membership changed.");
        return FreezeProvider(write, Guid.NewGuid(), value.Address, value.Location, checkAuthority);
    }

    /// <summary>Checks the actual prepared structural dependencies before authorizing the sole resolver.</summary>
    internal async Task<Func<Task<TripPlaceProviderResult>>> PrepareReceiptSaveProviderAsync(TripStructureWrite write,
        Func<CancellationToken, Task> checkAuthority)
    {
        using var body = TripStructureScalars.Body(write.Payload);
        var request = await PlaceStructuralRequest.ReadAsync(body, "web-update", true, write.Token);
        var snapshot = await _lifecycle.ReviewPlaceStructureAsync(write.TripId, write.TargetId, write.Subject, request,
            IconNames(), MarkerColors(), write.Token);
        var place = snapshot.Place ?? throw new InvalidOperationException("Protected Place save requires a Place.");
        var location = request.Changes.TryGetProperty("location", out _) ? request.Location : place.Location;
        var address = request.Changes.TryGetProperty("address", out var desired) ? desired.GetString() : place.Address;
        return FreezeProvider(write, place.Id, address, PlaceStructuralRequest.Pair(location), checkAuthority);
    }

    /// <summary>Routes prepared Web order and combined Save through the existing validation catalog.</summary>
    internal Task<TripStructureResult> StructureWithReceiptAsync(TripStructureWrite write, string operation) =>
        _lifecycle.PlaceStructureWithReceiptAsync(write, operation, IconNames(), MarkerColors());

    /// <summary>Checks the real Web create grammar and preserves the full original destination.</summary>
    private async Task<(CreationConditionalRequest Request, EditorPlaceCreateRequest Value)> ReadProtectedCreationAsync(TripStructureWrite write)
    {
        using var body = TripStructureScalars.Body(write.Payload);
        var request = await CreationConditionalRequest.ReadAsync(body, true, write.Token);
        if (request.RegionId != write.TargetId) throw CreationConditionalRequest.Invalid("The original destination must match the path.");
        CreationConditionalRequest.ValidateValue(request.Value, true, true);
        if (request.Value.EnumerateObject().Count() != 7
            || !EditorPlaceRequestParser.TryParseCreate(request.Value, IconNames(), MarkerColors(), out var value, out _))
            throw TripStructureBodies.Invalid();
        return (request, value);
    }

    /// <summary>Returns deferred work for one bounded location. The caller starts it only after claim commit.</summary>
    private Func<Task<TripPlaceProviderResult>> FreezeProvider(TripStructureWrite write, Guid placeId, string? address,
        EditorCoordinateDto? location, Func<CancellationToken, Task> checkAuthority)
    {
        if (location is not { Latitude: >= -90 and <= 90, Longitude: >= -180 and <= 180 })
            throw TripStructureBodies.Invalid();
        return async () =>
        {
            var result = await ResolveAddressAsync(write.Subject, placeId, address, location, true, write.Token, checkAuthority);
            var directive = result.Metadata.HasValue ? new EditorPlaceMetadataDirective(EditorPlaceMetadataPolicy.Replace, result.Metadata.Value)
                : new(EditorPlaceMetadataPolicy.RetainCompatible);
            return new TripPlaceProviderResult(placeId, new(result.Value, directive, result.Warnings));
        };
    }
}
