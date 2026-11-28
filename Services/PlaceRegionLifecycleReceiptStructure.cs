using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services.TripMetadataReceipts;

namespace Wayfarer.Services;

public sealed partial class PlaceRegionLifecycleService
{
    /// <summary>Captures the saved effect of one reviewed Place structure write inside its receipt transaction.</summary>
    internal async Task<TripStructureResult> PlaceStructureWithReceiptAsync(TripStructureWrite write, string operation,
        IReadOnlySet<string> icons, IReadOnlySet<string> colors)
    {
        using var body = TripStructureScalars.Body(write.Payload);
        var request = await PlaceStructuralRequest.ReadAsync(body, operation, write.Web, write.Token);
        TripStructureResult? captured = null;
        await ApplyPlaceStructureAsync(write.TripId, write.TargetId, write.Subject, request, icons, colors, write.Readmit,
            write.Provider is { } provider ? _ => Task.FromResult(provider.Address) : null, write.Token,
            async (snapshot, result, applied) => captured = new(applied, await CapturePlaceStructureAsync(write, request, snapshot, result)));
        return captured ?? throw new InvalidOperationException("Missing Place structure receipt effects.");
    }

    /// <summary>Projects original and new parents, exact saved fields, and the actual complete ordering and route effects.</summary>
    private async Task<object> CapturePlaceStructureAsync(TripStructureWrite write, PlaceStructuralRequest request,
        PlaceStructuralSnapshot snapshot, EditorMutationResult<object> result)
    {
        if (request.Operation == "order")
            return new { placeOrder = await TripStructureCollections.PlaceOrderAsync(_dbContext, write.TargetId, write.Token) };
        var original = snapshot.Place ?? throw new InvalidOperationException("Expected a locked Place original.");
        var saved = await _dbContext.Places.AsNoTracking().SingleAsync(row => row.Id == original.Id, write.Token);
        var fields = SavedPlaceFields(request, write.Provider != null);
        var vectors = new List<object>();
        if (original.RegionId != saved.RegionId)
        {
            foreach (var region in new[] { original.RegionId, saved.RegionId }.Order())
                vectors.Add(await TripStructureCollections.PlaceOrderAsync(_dbContext, region, write.Token));
        }

        return new
        {
            saved.Id, fromRegionId = original.RegionId, saved.RegionId,
            fieldDigests = fields.ToDictionary(field => field, field => TripStructureDigests.Place(saved, field)),
            placeOrders = vectors,
            segmentEffects = await TripStructureSegmentEffects.ReadAsync(_dbContext, write.TripId, snapshot.Segments, [], [],
                result.Affected.Segments.Select(segment => segment.Id).ToArray(), write.Token),
            addressEffect = PlaceUpdateAddressEffect(saved, original, fields, write.Provider)
        };
    }

    /// <summary>Lists canonical effective selections, including both values bound by geocode-only intent.</summary>
    private static HashSet<string> SavedPlaceFields(PlaceStructuralRequest request, bool geocode)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        if (request.Operation == "coordinates") fields.Add("location");
        if (request.Changes.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in request.Changes.EnumerateObject())
            {
                if (field.Name is "reverseGeocode" or "regionId"
                    || field.Name == "notesHtml" && field.Value.ValueKind == JsonValueKind.Null) continue;
                fields.Add(field.Name == "notesHtml" ? "notes" : field.Name);
            }
        }
        if (geocode)
        {
            fields.Add("location");
            fields.Add("address");
        }
        return fields;
    }

    /// <summary>Uses the same compatibility rule as the locked write and the frozen provider directive.</summary>
    private static object? PlaceUpdateAddressEffect(Place saved, Place original, HashSet<string> fields, TripPlaceProviderResult? provider)
    {
        if (provider == null && !fields.Contains("address") && !fields.Contains("location")) return null;
        var replaced = provider?.Address.Metadata.Policy == EditorPlaceMetadataPolicy.Replace;
        var geocode = provider == null ? "notRequested" : replaced ? "resolved" : "fallback";
        var compatible = CompatibleAddress(saved.Address, original.Address) && CoordinatesEqual(saved.Location, original.Location);
        return TripStructureDigests.AddressEffect(saved, geocode, replaced ? "replaced" : compatible ? "retained" : "cleared");
    }
}
