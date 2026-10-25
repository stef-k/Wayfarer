using System.Text.Json;
using Wayfarer.Services;

namespace Wayfarer.Models.Dtos;

/// <summary>Exact starting-view membership and stored order evidence for one checked append.</summary>
internal sealed record CreationConditionalRequest(JsonElement Value, IReadOnlyList<CreationRegionOriginal>? Regions,
    Guid? RegionId, IReadOnlyList<CreationPlaceOriginal>? Places)
{
    /// <summary>Requires purpose-specific originals; absence, null and an empty collection remain distinct.</summary>
    internal static async Task<CreationConditionalRequest> ReadAsync(Stream body, bool place, CancellationToken token)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: token);
            var root = Fields(document.RootElement, ["expected", "value"]);
            var expected = Fields(Required(root, "expected"), place ? ["regionId", "places", "regions"] : ["regions"]);
            var value = Required(root, "value").Clone();
            Guid? regionId = null;
            IReadOnlyList<CreationPlaceOriginal>? places = null;
            if (place)
            {
                var destination = Required(expected, "regionId");
                regionId = destination.ValueKind == JsonValueKind.Null ? null : Id(destination);
                places = Array(Required(expected, "places")).Select(item =>
                {
                    var fields = Fields(item, ["id", "displayOrder"]);
                    return new CreationPlaceOriginal(Id(Required(fields, "id")), Order(Required(fields, "displayOrder"), true));
                }).ToArray();
                if (places.Select(p => p.Id).Distinct().Count() != places.Count || regionId == null && places.Count != 0)
                    throw Invalid("Place originals must identify every sibling exactly once; an absent destination has no Places.");
            }
            IReadOnlyList<CreationRegionOriginal>? regions = null;
            if (!place || regionId == null)
            {
                regions = Array(Required(expected, "regions")).Select(item =>
                {
                    var fields = Fields(item, ["id", "displayOrder", "isCanonical"]);
                    var canonical = Required(fields, "isCanonical");
                    if (canonical.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid("Canonical identity is required.");
                    return new CreationRegionOriginal(Id(Required(fields, "id")), Order(Required(fields, "displayOrder"), false)!.Value, canonical.GetBoolean());
                }).ToArray();
                if (regions.Select(r => r.Id).Distinct().Count() != regions.Count || place && regions.Any(r => r.IsCanonical))
                    throw Invalid("Region originals must be unique and prove canonical absence when creating a fallback.");
            }
            else if (expected.ContainsKey("regions")) throw Invalid("Existing destinations require only their own Place membership/order evidence.");
            return new(value, regions, regionId, places);
        }
        catch (JsonException) { throw Invalid("The request must be valid JSON."); }
    }

    /// <summary>Rejects unknown and duplicate properties rather than accepting serializer defaults.</summary>
    internal static Dictionary<string, JsonElement> Fields(JsonElement value, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("An object is required.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name) || !fields.TryAdd(property.Name, property.Value)) throw Invalid("Unknown or duplicate field.");
        return fields;
    }

    /// <summary>Checks nested authored objects before the existing Web field validators interpret them.</summary>
    internal static void ValidateValue(JsonElement value, bool place, bool web)
    {
        var fields = Fields(value, place
            ? web ? ["name", "notesHtml", "address", "location", "iconName", "markerColor", "reverseGeocode"]
                : ["regionId", "name", "notes", "location", "iconName", "markerColor"]
            : web ? ["name", "notesHtml", "coverImage", "center"] : ["name", "notes"]);
        foreach (var key in new[] { "location", "center" })
            if (fields.TryGetValue(key, out var coordinate) && coordinate.ValueKind != JsonValueKind.Null)
                Fields(coordinate, ["latitude", "longitude"]);
        if (fields.TryGetValue("coverImage", out var cover) && cover.ValueKind != JsonValueKind.Null) Fields(cover, ["rawUrl"]);
    }

    /// <summary>Never substitutes a missing original with a default value.</summary>
    internal static JsonElement Required(Dictionary<string, JsonElement> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value : throw Invalid($"{key} is required.");

    /// <summary>Accepts only nonempty server identities.</summary>
    internal static Guid Id(JsonElement value) => value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id) && id != Guid.Empty
        ? id : throw Invalid("A nonempty GUID is required.");

    /// <summary>Retains null and sparse/tied stored Place orders exactly.</summary>
    private static int? Order(JsonElement value, bool nullable) => nullable && value.ValueKind == JsonValueKind.Null ? null
        : value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var order) ? order : throw Invalid("A stored integer order is required.");

    /// <summary>Requires a complete membership collection even when it is empty.</summary>
    private static IEnumerable<JsonElement> Array(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray() : throw Invalid("A complete original membership array is required.");

    /// <summary>Shares the published safe request-rejection envelope.</summary>
    internal static TripMetadataMutationRejected Invalid(string message) => new(400, "mutation-request-invalid", message, new() { ["request"] = [message] });
}

/// <summary>Region content is not an append dependency, except its exact canonical identity.</summary>
internal sealed record CreationRegionOriginal(Guid Id, int DisplayOrder, bool IsCanonical);
/// <summary>Complete destination membership and nullable stored order, without unrelated sibling content.</summary>
internal sealed record CreationPlaceOriginal(Guid Id, int? DisplayOrder);
