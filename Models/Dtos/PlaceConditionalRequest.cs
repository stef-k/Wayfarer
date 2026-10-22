using System.Text.Json;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Models.Dtos;

/// <summary>Strict Place scalar intent and original parent evidence, independent of display defaults.</summary>
internal sealed record PlaceConditionalRequest(Guid RegionId, Dictionary<string, string?> Expected, Dictionary<string, string?> Changes)
{
    /// <summary>Rejects ambiguous/structural JSON and validates every selected replacement before entering the transaction.</summary>
    internal static async Task<PlaceConditionalRequest> ReadAsync(Stream body, bool web, IReadOnlySet<string> icons,
        IReadOnlySet<string> colors, CancellationToken token)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: token);
            var root = Fields(document.RootElement, ["expected", "changes"]);
            if (!root.TryGetValue("expected", out var expected) || !root.TryGetValue("changes", out var changes))
                throw Invalid("request", "Both expected and changes objects are required.");
            string[] allowed = ["name", web ? "notesHtml" : "notes", "iconName", "markerColor"];
            var originals = Fields(expected, [.. allowed, "regionId"]);
            var desired = Fields(changes, allowed);
            if (!originals.Remove("regionId", out var parent) || parent.ValueKind != JsonValueKind.String
                || !parent.TryGetGuid(out var regionId) || regionId == Guid.Empty)
                throw Invalid("expected.regionId", "The original Region ID is required.");
            var parsed = new PlaceConditionalRequest(regionId, [], []);
            foreach (var (key, value) in desired)
            {
                var canonical = key == "notesHtml" ? "notes" : key;
                var next = Value(key, value, true, icons, colors);
                if (canonical == "notes" && next == null)
                {
                    if (originals.TryGetValue(key, out var ignored)) Value(key, ignored, false, icons, colors);
                    continue;
                }
                if (!originals.TryGetValue(key, out var original)) throw Invalid(key, "An original value is required for each changed field.");
                parsed.Expected[canonical] = Value(key, original, false, icons, colors);
                parsed.Changes[canonical] = next;
            }
            if (originals.Keys.Except(desired.Keys).Any()) throw Invalid("expected", "Originals must correspond to requested fields.");
            if (parsed.Changes.Count == 0) throw Invalid("changes", "At least one effective field change is required.");
            return parsed;
        }
        catch (JsonException) { throw Invalid("request", "The request must be valid JSON."); }
        catch (InvalidOperationException) { throw Invalid("request", "The request must contain valid Unicode text."); }
    }

    /// <summary>Checks exact spelling and uniqueness at each accepted object boundary.</summary>
    private static Dictionary<string, JsonElement> Fields(JsonElement value, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("request", "An object is required.");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!allowed.Contains(field.Name) || !result.TryAdd(field.Name, field.Value))
                throw Invalid("request", "Unknown or duplicate fields are not supported.");
        return result;
    }

    /// <summary>Canonicalizes Notes equally for both clients; nullable marker originals retain exact storage identity.</summary>
    private static string? Value(string key, JsonElement value, bool desired, IReadOnlySet<string> icons, IReadOnlySet<string> colors)
    {
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw Invalid(key, "The field must be a string or null.");
        var text = value.GetString();
        if (key is "notes" or "notesHtml")
        {
            if (desired && RichNotes.ContainsDataImageSource(text)) throw Invalid(key, "Notes images must use external image URLs.");
            return RichNotes.Normalize(text);
        }
        if (key == "name")
        {
            if (text == null || desired && string.IsNullOrWhiteSpace(text)) throw Invalid(key, "Name is required.");
            // PostgreSQL text cannot store U+0000; reject before any SQL mutation is attempted.
            if (text.Contains('\0')) throw Invalid(key, "Name must not contain null characters.");
            return desired ? text.Trim() : text;
        }
        if (desired && (text == null || !(key == "iconName" ? icons : colors).Contains(text)))
            throw Invalid(key, "The value must be one of the existing editor options.");
        return text;
    }

    /// <summary>Provides actionable validation without reflecting private submitted content.</summary>
    private static TripMetadataMutationRejected Invalid(string key, string message) =>
        new(400, "mutation-request-invalid", "Check the requested Place fields.", new() { [key] = [message] });
}
