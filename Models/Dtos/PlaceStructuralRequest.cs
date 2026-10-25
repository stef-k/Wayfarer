using System.Security.Cryptography;
using System.Text.Json;
using NetTopologySuite.Geometries;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Models.Dtos;

/// <summary>Operation-specific originals and intent; prepared evidence never replaces these client originals.</summary>
internal sealed record PlaceStructuralRequest(string Operation, bool Web, JsonElement Expected,
    JsonElement Changes, Guid? Destination, Dictionary<Guid, Guid[]> Orders, Point? Location,
    string? ConditionToken, bool Reviewed)
{
    /// <summary>Uses the published camel-case wire shape for private hashes and public original comparisons.</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Parses the bounded coordinate, move, combined Web, or complete-vector contract.</summary>
    internal static async Task<PlaceStructuralRequest> ReadAsync(Stream body, string operation, bool web, CancellationToken token)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: token);
            RejectDuplicateMembers(document.RootElement);
            var root = Fields(document.RootElement, "expected", "changes", "location", "destinationRegionId", "placeIdsByRegion", "placeIds", "conditionToken", "reviewed");
            if (!root.TryGetValue("expected", out var expected)) throw Invalid();
            var changes = root.GetValueOrDefault("changes");
            Guid? destination = null;
            Point? location = null;
            Dictionary<Guid, Guid[]> orders = [];
            if (operation == "coordinates")
            {
                RequireOnly(root, "expected", "location", "conditionToken", "reviewed");
                Fields(expected, "regionId", "location");
                if (!root.TryGetValue("location", out var pair)) throw Invalid();
                location = Coordinate(pair, false);
            }
            else if (operation == "move")
            {
                RequireOnly(root, "expected", "destinationRegionId", "placeIdsByRegion", "conditionToken", "reviewed");
                destination = Id(root.GetValueOrDefault("destinationRegionId"));
                orders = OrderVectors(root.GetValueOrDefault("placeIdsByRegion"));
            }
            else if (operation == "order")
            {
                RequireOnly(root, "expected", "placeIds", "conditionToken", "reviewed");
                orders[Guid.Empty] = Ids(root.GetValueOrDefault("placeIds"));
                Fields(expected, "places");
            }
            else if (operation == "web-update" && web)
            {
                RequireOnly(root, "expected", "changes", "placeIdsByRegion", "conditionToken", "reviewed");
                var values = Fields(changes, "regionId", "location", "name", "notesHtml", "iconName", "markerColor", "address", "reverseGeocode");
                if (values.Count == 0) throw Invalid();
                if (values.TryGetValue("regionId", out var parent)) destination = Id(parent);
                if (values.TryGetValue("location", out var pair)) location = Coordinate(pair, true);
                if (values.TryGetValue("address", out var address)
                    && (address.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) || address.GetString()?.Contains('\0') == true)) throw Invalid();
                if (values.TryGetValue("reverseGeocode", out var reverse) && reverse.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
                if (root.TryGetValue("placeIdsByRegion", out var vectors))
                {
                    if (!destination.HasValue) throw Invalid();
                    orders = OrderVectors(vectors);
                }
            }
            else throw Invalid();
            if (operation == "move" || destination.HasValue) Fields(expected, "place", "placesByRegion");
            if (root.TryGetValue("conditionToken", out var condition) && condition.ValueKind != JsonValueKind.String) throw Invalid();
            if (root.TryGetValue("reviewed", out var reviewed) && reviewed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
            // Clone every retained element before disposing the request document.
            return new(operation, web, expected.Clone(), changes.ValueKind == JsonValueKind.Undefined ? default : changes.Clone(),
                destination, orders, location, condition.ValueKind == JsonValueKind.String ? condition.GetString() : null,
                reviewed.ValueKind == JsonValueKind.True);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        { throw Invalid(); }
    }

    /// <summary>Desired actions are bound independently of JSON property order and acknowledgement flags.</summary>
    internal string IntentFingerprint() => Hash(new { Operation, Web, Destination,
        Location = Pair(Location), Expected = Canonical(Expected), Changes = Canonical(Changes), Orders = Orders.OrderBy(p => p.Key).Select(p => new { p.Key, p.Value }) });

    /// <summary>Exact public material content used by move/reorder originals, including nullable stored order.</summary>
    internal static object Original(Place place) => new { place.Id, place.RegionId, place.Name, Notes = RichNotes.Normalize(place.Notes),
        place.Address, Location = Pair(place.Location), place.IconName, place.MarkerColor, place.DisplayOrder,
        place.ResolvedFeatureName, place.ResolvedFeatureType };

    /// <summary>Compares values semantically while preserving missing versus explicit null.</summary>
    internal static bool Equal(JsonElement original, object? current) => JsonElement.DeepEquals(original, JsonSerializer.SerializeToElement(current, Json));

    /// <summary>Projects historical non-finite geography as null; private fingerprints retain the raw stored geometry.</summary>
    internal static EditorCoordinateDto? Pair(Point? point) => point is not null && double.IsFinite(point.X) && double.IsFinite(point.Y)
        ? new(point.Y, point.X) : null;

    /// <summary>Computes a deterministic private fingerprint without exposing private snapshot fields.</summary>
    internal static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));

    /// <summary>Rejects duplicate, unknown, or non-object members at every parsed contract boundary.</summary>
    internal static Dictionary<string, JsonElement> Fields(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid();
        Dictionary<string, JsonElement> fields = new(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name) || !fields.TryAdd(property.Name, property.Value)) throw Invalid();
        return fields;
    }

    /// <summary>Rejects ambiguous originals and vectors too, including objects nested inside arrays.</summary>
    private static void RejectDuplicateMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid();
                RejectDuplicateMembers(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) RejectDuplicateMembers(child);
    }

    /// <summary>Rejects fields belonging to another operation before any preparation or provider contact.</summary>
    private static void RequireOnly(Dictionary<string, JsonElement> fields, params string[] allowed)
    {
        if (fields.Keys.Except(allowed).Any()) throw Invalid();
    }

    /// <summary>Requires a nonempty typed target/member identity.</summary>
    private static Guid Id(JsonElement value) => value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id) && id != Guid.Empty ? id : throw Invalid();

    /// <summary>Parses a desired vector without silently dropping duplicate members.</summary>
    private static Guid[] Ids(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) throw Invalid();
        var ids = value.EnumerateArray().Select(Id).ToArray();
        return ids.Distinct().Count() == ids.Length ? ids : throw Invalid();
    }

    /// <summary>Keeps both exact desired Region scopes, rejecting alias/duplicate keys.</summary>
    private static Dictionary<Guid, Guid[]> OrderVectors(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        Dictionary<Guid, Guid[]> result = [];
        foreach (var field in value.EnumerateObject())
            if (!Guid.TryParse(field.Name, out var id) || id == Guid.Empty || !result.TryAdd(id, Ids(field.Value))) throw Invalid();
        return result;
    }

    /// <summary>Validates the entire WGS84 pair, retaining Web-only nullable coordinate intent.</summary>
    private static Point? Coordinate(JsonElement value, bool nullable)
    {
        if (nullable && value.ValueKind == JsonValueKind.Null) return null;
        var fields = Fields(value, "latitude", "longitude");
        if (fields.Count != 2 || !fields["latitude"].TryGetDouble(out var lat) || !fields["longitude"].TryGetDouble(out var lon)
            || !double.IsFinite(lat) || !double.IsFinite(lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180) throw Invalid();
        return new(lon, lat) { SRID = 4326 };
    }

    /// <summary>Canonicalizes object keys and original identity sets without reordering desired vectors.</summary>
    private static object? Canonical(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Undefined => null,
        JsonValueKind.Object => element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToDictionary(p => p.Name, p => Canonical(p.Value)),
        JsonValueKind.Array => (element.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out _))
            ? element.EnumerateArray().OrderBy(item => item.GetProperty("id").GetString(), StringComparer.Ordinal)
            : element.EnumerateArray().AsEnumerable()).Select(Canonical).ToArray(),
        _ => element
    };

    /// <summary>Reports a known-unapplied request error without reflecting submitted or private content.</summary>
    internal static TripMetadataMutationRejected Invalid() => new(400, "mutation-request-invalid", "Provide complete Place originals and a valid operation vector.", new() { ["request"] = ["Complete original evidence and valid operation fields are required."] });
}
