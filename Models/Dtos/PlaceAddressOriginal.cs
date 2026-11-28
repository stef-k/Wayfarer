using System.Text.Json;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;

namespace Wayfarer.Models.Dtos;

/// <summary>Freezes coordinates and the full persisted enrichment tuple for manual bearer Address edits.</summary>
internal sealed record PlaceAddressOriginal(EditorCoordinateDto? Location, string? ResolvedFeatureName,
    string? ResolvedFeatureType, string? AddressEnrichmentProvider, string? AddressEnrichmentStorageMode, DateTimeOffset? AddressEnrichedAt)
{
    /// <summary>Reads the exact nullable tuple plus its companion position original.</summary>
    internal static PlaceAddressOriginal Parse(JsonElement location, JsonElement enrichment)
    {
        if (enrichment.ValueKind != JsonValueKind.Object || enrichment.EnumerateObject().Count() != 5
            || !enrichment.TryGetProperty("addressEnrichedAt", out var timestamp))
            throw Invalid();
        DateTimeOffset? instant = null;
        if (timestamp.ValueKind != JsonValueKind.Null)
            instant = timestamp.ValueKind == JsonValueKind.String && timestamp.TryGetDateTimeOffset(out var value)
                ? value : throw Invalid();
        return new(ReadLocation(location), ReadText(enrichment, "resolvedFeatureName"), ReadText(enrichment, "resolvedFeatureType"),
            ReadText(enrichment, "addressEnrichmentProvider"), ReadText(enrichment, "addressEnrichmentStorageMode"), instant);
    }

    /// <summary>Uses the same comparison values as the published owner snapshot.</summary>
    internal static PlaceAddressOriginal From(Place place) => new(PlaceStructuralRequest.Pair(place.Location),
        place.ResolvedFeatureName, place.ResolvedFeatureType, place.AddressEnrichmentProvider,
        place.AddressEnrichmentStorageMode, place.AddressEnrichedAt);

    /// <summary>Originals can be null or finite legacy pairs.</summary>
    private static EditorCoordinateDto? ReadLocation(JsonElement location)
    {
        if (location.ValueKind == JsonValueKind.Null) return null;
        if (location.ValueKind != JsonValueKind.Object || location.EnumerateObject().Count() != 2
            || !location.TryGetProperty("latitude", out var lat) || !location.TryGetProperty("longitude", out var lon)
            || lat.ValueKind != JsonValueKind.Number || lon.ValueKind != JsonValueKind.Number
            || !lat.TryGetDouble(out var latitude) || !lon.TryGetDouble(out var longitude)
            || !double.IsFinite(latitude) || !double.IsFinite(longitude))
            throw Invalid();
        return new(latitude, longitude);
    }

    /// <summary>Requires each nullable tuple member independently.</summary>
    private static string? ReadText(JsonElement tuple, string name) =>
        tuple.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Null
            ? value.GetString() : throw Invalid();

    /// <summary>Returns bounded validation guidance without private text or coordinates.</summary>
    private static TripMetadataMutationRejected Invalid() =>
        new(400, "mutation-request-invalid", "The exact original Address, location and enrichment are required.");
}
