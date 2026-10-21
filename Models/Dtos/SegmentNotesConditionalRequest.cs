using System.Text.Json;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Models.Dtos;

/// <summary>Strict Notes-only intent with independently present nullable published originals.</summary>
internal sealed record SegmentNotesConditionalRequest(string? Expected, string Desired)
{
    /// <summary>Rejects ambiguous envelopes and non-Notes fields; null/omitted desired Notes never produces a fake save.</summary>
    internal static async Task<SegmentNotesConditionalRequest> ReadAsync(Stream body, bool web, CancellationToken token)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: token);
            var root = Fields(document.RootElement, ["expected", "changes"]);
            if (!root.TryGetValue("expected", out var expected) || !root.TryGetValue("changes", out var changes))
                throw Invalid("request", "Both expected and changes objects are required.");
            var key = web ? "notesHtml" : "notes";
            var originals = Fields(expected, [key]);
            var desired = Fields(changes, [key]);
            if (!originals.TryGetValue(key, out var original)) throw Invalid(key, "The original published Notes value is required, including null.");
            var before = Text(original, key);
            if (!desired.TryGetValue(key, out var next) || Text(next, key) is not { } text)
                throw Invalid("changes", "At least one effective Notes change is required. Use an empty string to clear Notes.");
            if (RichNotes.ContainsDataImageSource(text)) throw Invalid(key, "Notes images must use external image URLs.");
            // Originals are already published canonical evidence: never sanitize a different submitted base into a match.
            return new(before, RichNotes.Normalize(text)!);
        }
        catch (JsonException) { throw Invalid("request", "The request must be valid JSON."); }
    }

    /// <summary>Requires exact spelling and unique keys at each accepted object boundary.</summary>
    private static Dictionary<string, JsonElement> Fields(JsonElement value, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("request", "An object is required.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!allowed.Contains(field.Name) || !fields.TryAdd(field.Name, field.Value))
                throw Invalid("request", "Unknown or duplicate fields are not supported.");
        return fields;
    }

    /// <summary>Preserves null and empty separately and rejects coercion of non-text values.</summary>
    private static string? Text(JsonElement value, string key) => value.ValueKind is JsonValueKind.String or JsonValueKind.Null
        ? value.GetString() : throw Invalid(key, "Notes must be a string or null.");

    /// <summary>Gives field guidance without reflecting private submitted content.</summary>
    private static TripMetadataMutationRejected Invalid(string key, string message) =>
        new(400, "mutation-request-invalid", "Check the requested Segment Notes.", new() { [key] = [message] });
}
