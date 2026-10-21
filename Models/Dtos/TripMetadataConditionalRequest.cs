using System.Text.Json;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Models.Dtos;

/// <summary>Strict per-field originals and dirty intent for the paired Trip metadata operation.</summary>
internal sealed record TripMetadataConditionalRequest(Dictionary<string, object?> Expected, Dictionary<string, object?> Changes)
{
    /// <summary>Parses only the selected fields, preserving explicit null originals and rejecting ambiguous input.</summary>
    internal static async Task<TripMetadataConditionalRequest> ReadAsync(Stream body, bool web, CancellationToken token)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: token);
            var root = Fields(document.RootElement, ["expected", "changes"]);
            if (!root.TryGetValue("expected", out var expected) || !root.TryGetValue("changes", out var changes))
                throw Invalid("request", "Both expected and changes objects are required.");
            string[] allowed = web ? ["name", "notesHtml", "isPublic", "coverImage", "center", "zoom"] : ["name", "notes"];
            var originals = Fields(expected, allowed);
            var desired = Fields(changes, allowed);
            var parsed = new TripMetadataConditionalRequest([], []);
            foreach (var (key, value) in desired)
            {
                if (!originals.TryGetValue(key, out var original)) throw Invalid(key, "An original value is required for each changed field.");
                var canonicalKey = key == "notesHtml" ? "notes" : key;
                parsed.Expected[canonicalKey] = ReadValue(key, original, false);
                var next = ReadValue(key, value, true);
                // Bearer null Notes means Unchanged. Web Clear must also be an explicit empty string.
                if (canonicalKey == "notes" && next == null) continue;
                parsed.Changes[canonicalKey] = next;
            }
            if (originals.Keys.Except(desired.Keys).Any()) throw Invalid("expected", "Originals must correspond to requested fields.");
            if (parsed.Changes.Count == 0) throw Invalid("changes", "At least one effective field change is required.");
            // Ignored null Notes must not accidentally add a compare to an unrelated effective mutation.
            foreach (var key in parsed.Expected.Keys.Except(parsed.Changes.Keys).ToArray()) parsed.Expected.Remove(key);
            return parsed;
        }
        catch (JsonException) { throw Invalid("request", "The request must be valid JSON."); }
    }

    /// <summary>Checks each object once so duplicate, unknown, and wrongly cased properties fail closed.</summary>
    private static Dictionary<string, JsonElement> Fields(JsonElement value, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("request", "An object is required.");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
        {
            if (!allowed.Contains(field.Name) || !result.TryAdd(field.Name, field.Value))
                throw Invalid("request", "Unknown or duplicate fields are not supported.");
        }
        return result;
    }

    /// <summary>Canonicalizes authored Notes only; expected values retain null and original scalar identity.</summary>
    private static object? ReadValue(string key, JsonElement value, bool desired) => key switch
    {
        "name" => ReadName(value, desired),
        "notes" or "notesHtml" => ReadNotes(key, value, desired),
        "isPublic" when value.ValueKind is JsonValueKind.True or JsonValueKind.False => value.GetBoolean(),
        "coverImage" => ReadCover(value, desired),
        "center" => ReadCenter(value, desired),
        "zoom" => ReadZoom(value, desired),
        _ => throw Invalid(key, "The field has an invalid value.")
    };

    private static string ReadName(JsonElement value, bool desired)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid("name", "Name must be a string.");
        var name = value.GetString()!;
        if (!desired) return name;
        name = name.Trim();
        if (name.Length is < 1 or > 200) throw Invalid("name", "Name must contain 1 through 200 characters.");
        return name;
    }

    private static string? ReadNotes(string key, JsonElement value, bool desired)
    {
        var notes = NullableString(key, value);
        if (desired && RichNotes.ContainsDataImageSource(notes))
            throw Invalid(key, "Notes images must use external image URLs, not data:image sources.");
        return RichNotes.Normalize(notes);
    }

    private static string? ReadCover(JsonElement value, bool desired)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        var fields = Fields(value, ["rawUrl"]);
        if (!fields.TryGetValue("rawUrl", out var raw)) throw Invalid("coverImage.rawUrl", "The field is required.");
        var url = NullableString("coverImage.rawUrl", raw);
        if (!desired) return url;
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw Invalid("coverImage.rawUrl", "Cover image URL must be an absolute HTTP or HTTPS URL.");
        return url;
    }

    private static TripMetadataCenter ReadCenter(JsonElement value, bool desired)
    {
        if (value.ValueKind == JsonValueKind.Null) return new(null, null);
        var fields = Fields(value, ["latitude", "longitude"]);
        if (!fields.TryGetValue("latitude", out var lat) || !fields.TryGetValue("longitude", out var lon))
            throw Invalid("center", "Both coordinates are required.");
        return new(Coordinate(lat, "center.latitude", 90, desired), Coordinate(lon, "center.longitude", 180, desired));
    }

    private static double? Coordinate(JsonElement value, string key, int limit, bool desired)
    {
        if (!desired && value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)
            || (desired && (number < -limit || number > limit)))
            throw Invalid(key, $"The coordinate must be finite and between {-limit} and {limit}.");
        return number;
    }

    private static int? ReadZoom(JsonElement value, bool desired)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var zoom) || (desired && (zoom < 0 || zoom > 19)))
            throw Invalid("zoom", "Zoom must be an integer from 0 through 19.");
        return zoom;
    }

    private static string? NullableString(string key, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        _ => throw Invalid(key, "The field must be a string or null.")
    };

    private static TripMetadataMutationRejected Invalid(string field, string message) =>
        new(400, "mutation-request-invalid", "Check the requested Trip fields.", new() { [field] = [message] });
}

/// <summary>Preserves both original nullable coordinates, including partially populated legacy values.</summary>
internal sealed record TripMetadataCenter(double? Latitude, double? Longitude);
