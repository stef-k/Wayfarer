using System.Text.Json;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Models.Dtos;

/// <summary>Original values and exact scalar intent for paired Region editing; no structural fields are admitted.</summary>
internal sealed record RegionConditionalRequest(Dictionary<string, object?> Expected, Dictionary<string, object?> Changes)
{
    /// <summary>Rejects ambiguous JSON and preserves explicit nullable originals independently of display defaults.</summary>
    internal static async Task<RegionConditionalRequest> ReadAsync(Stream body, bool web, CancellationToken token)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: token);
            var root = Fields(document.RootElement, ["expected", "changes"]);
            if (!root.TryGetValue("expected", out var expected) || !root.TryGetValue("changes", out var changes))
                throw Invalid("request", "Both expected and changes objects are required.");
            string[] allowed = web ? ["name", "notesHtml", "coverImage", "center"] : ["name", "notes"];
            var originals = Fields(expected, allowed);
            var desired = Fields(changes, allowed);
            var parsed = new RegionConditionalRequest([], []);
            foreach (var (key, value) in desired)
            {
                var canonical = key == "notesHtml" ? "notes" : key;
                var next = Value(key, value, true);
                if (canonical == "notes" && next == null)
                {
                    if (originals.TryGetValue(key, out var ignored)) Value(key, ignored, false);
                    continue;
                }
                if (!originals.TryGetValue(key, out var original)) throw Invalid(key, "An original value is required for each changed field.");
                parsed.Expected[canonical] = Value(key, original, false);
                parsed.Changes[canonical] = next;
            }
            if (originals.Keys.Except(desired.Keys).Any()) throw Invalid("expected", "Originals must correspond to requested fields.");
            if (parsed.Changes.Count == 0) throw Invalid("changes", "At least one effective field change is required.");
            return parsed;
        }
        catch (JsonException) { throw Invalid("request", "The request must be valid JSON."); }
    }

    /// <summary>Checks field spelling and uniqueness at every accepted object boundary.</summary>
    private static Dictionary<string, JsonElement> Fields(JsonElement value, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("request", "An object is required.");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!allowed.Contains(field.Name) || !result.TryAdd(field.Name, field.Value))
                throw Invalid("request", "Unknown or duplicate fields are not supported.");
        return result;
    }

    /// <summary>Canonicalizes desired values using existing Region validation; originals keep exact scalar identity.</summary>
    private static object? Value(string key, JsonElement value, bool desired)
    {
        if (key == "center") return Center(value);
        if (key == "coverImage")
        {
            if (value.ValueKind == JsonValueKind.Null) return null;
            var fields = Fields(value, ["rawUrl"]);
            if (!fields.TryGetValue("rawUrl", out value)) throw Invalid("coverImage.rawUrl", "The field is required.");
        }
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw Invalid(key, "The field must be a string or null.");
        var text = value.GetString();
        if (key == "name")
        {
            if (text == null || desired && string.IsNullOrWhiteSpace(text)) throw Invalid(key, "Name is required.");
            if (desired && string.Equals(text.Trim(), "Unassigned Places", StringComparison.OrdinalIgnoreCase))
                throw Invalid(key, "The reserved Region name cannot be used.");
            return desired ? text.Trim() : text;
        }
        if (key is "notes" or "notesHtml")
        {
            if (desired && RichNotes.ContainsDataImageSource(text)) throw Invalid(key, "Notes images must use external image URLs.");
            return RichNotes.Normalize(text);
        }
        if (!desired) return text;
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw Invalid("coverImage.rawUrl", "Cover image URL must be an absolute HTTP or HTTPS URL.");
        return text;
    }

    /// <summary>Requires a finite WGS84 pair or explicit Clear; geometry and order cannot enter this operation.</summary>
    private static RegionConditionalCenter? Center(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        var fields = Fields(value, ["latitude", "longitude"]);
        return new(Coordinate(fields, "latitude", 90), Coordinate(fields, "longitude", 180));
    }

    /// <summary>Rejects partial, nonnumeric, nonfinite and out-of-range center coordinates.</summary>
    private static double Coordinate(Dictionary<string, JsonElement> fields, string key, int limit)
    {
        if (!fields.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number < -limit || number > limit)
            throw Invalid($"center.{key}", $"The coordinate must be finite and between {-limit} and {limit}.");
        return number;
    }

    /// <summary>Provides actionable field errors without reflecting submitted private content.</summary>
    private static TripMetadataMutationRejected Invalid(string key, string message) =>
        new(400, "mutation-request-invalid", "Check the requested Region fields.", new() { [key] = [message] });
}

/// <summary>Value identity of a Region's nullable persisted center.</summary>
internal sealed record RegionConditionalCenter(double Latitude, double Longitude);
