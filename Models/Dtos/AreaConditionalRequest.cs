using System.Text.Json;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Models.Dtos;

/// <summary>Strict Area field intent plus original parent binding; bearer selects Notes only.</summary>
internal sealed record AreaConditionalRequest(Guid RegionId, Dictionary<string, object?> Expected, Dictionary<string, object?> Changes)
{
    /// <summary>Preserves nullable originals and rejects duplicates, unknown fields and empty effective changes.</summary>
    internal static async Task<AreaConditionalRequest> ReadAsync(Stream body, bool web, CancellationToken token)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: token);
            var root = Fields(document.RootElement, ["expected", "changes"]);
            if (!root.TryGetValue("expected", out var expected) || !root.TryGetValue("changes", out var changes))
                throw Invalid("request", "Both expected and changes objects are required.");
            string[] allowed = web ? ["name", "notesHtml", "fillHex", "geometry"] : ["notes"];
            var originals = Fields(expected, [.. allowed, "regionId"]);
            var desired = Fields(changes, allowed);
            if (!originals.Remove("regionId", out var parent) || parent.ValueKind != JsonValueKind.String
                || !parent.TryGetGuid(out var regionId) || regionId == Guid.Empty)
                throw Invalid("expected.regionId", "The original Region ID is required.");
            var parsed = new AreaConditionalRequest(regionId, [], []);
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

    /// <summary>Checks exact field spelling and uniqueness at each accepted object boundary.</summary>
    private static Dictionary<string, JsonElement> Fields(JsonElement value, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("request", "An object is required.");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!allowed.Contains(field.Name) || !result.TryAdd(field.Name, field.Value))
                throw Invalid("request", "Unknown or duplicate fields are not supported.");
        return result;
    }

    /// <summary>Validates desired replacements, including the persisted trimmed Name cap; originals retain nullable identity.</summary>
    private static object? Value(string key, JsonElement value, bool desired)
    {
        if (key == "geometry" && desired)
        {
            Fields(value, ["type", "coordinates"]);
            var wrapper = JsonSerializer.SerializeToElement(new { geometry = value });
            if (!EditorAreaRequestParser.TryParseGeometryUpdate(wrapper, out var replacement, out var errors))
                throw new TripMetadataMutationRejected(400, "mutation-request-invalid", "Check the requested Polygon.", errors);
            return replacement.Geometry;
        }
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw Invalid(key, "The field must be a string or null.");
        var text = value.GetString();
        if (key is "notes" or "notesHtml")
        {
            if (desired && RichNotes.ContainsDataImageSource(text)) throw Invalid(key, "Notes images must use external image URLs.");
            return RichNotes.Normalize(text);
        }
        if (key == "geometry")
        {
            if (text == null || !System.Text.RegularExpressions.Regex.IsMatch(text, "^area-polygon-1:[0-9A-F]{64}$"))
                throw Invalid(key, "Original server Polygon evidence is required.");
            return text;
        }
        if (!desired)
        {
            if (key == "name" && text == null) throw Invalid(key, "Original Name must be a string.");
            return text;
        }
        var validation = new Dictionary<string, string[]>();
        if (key == "name")
        {
            EditorAreaRequestParser.ValidateRequiredName(text, validation);
            if (text?.Trim().Length > 200) validation["name"] = ["Area name must be 200 characters or fewer."];
        }
        else EditorAreaRequestParser.ValidateFillHex(text, validation);
        if (validation.Count != 0) throw new TripMetadataMutationRejected(400, "mutation-request-invalid", "Check the requested Area fields.", validation);
        return key == "name" ? text!.Trim() : text!.Trim().ToLowerInvariant();
    }

    /// <summary>Returns field guidance without reflecting submitted private content.</summary>
    private static TripMetadataMutationRejected Invalid(string key, string message) =>
        new(400, "mutation-request-invalid", "Check the requested Area fields.", new() { [key] = [message] });
}
