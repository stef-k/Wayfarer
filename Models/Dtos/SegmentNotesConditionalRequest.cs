using System.Text.Json;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Models.Dtos;

/// <summary>Strict independent Segment title/Notes intent; omission keeps each unselected field.</summary>
internal sealed record SegmentNotesConditionalRequest(string? Expected, string? Desired,
    bool HasNotes = true, bool HasTitle = false, string? ExpectedTitle = null, string? Title = null)
{
    /// <summary>Each authored group requires its exact nullable original; explicit null title clears it.</summary>
    internal static async Task<SegmentNotesConditionalRequest> ReadAsync(Stream body, bool web, CancellationToken token)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: token);
            var root = Fields(document.RootElement, ["expected", "changes"]);
            if (!root.TryGetValue("expected", out var expected) || !root.TryGetValue("changes", out var changes))
                throw Invalid("Both expected and changes objects are required.");
            var key = web ? "notesHtml" : "notes";
            var originals = Fields(expected, [key, "title"]);
            var desired = Fields(changes, [key, "title"]);
            if (originals.Keys.Except(desired.Keys).Any() || desired.Keys.Except(originals.Keys).Any())
                throw Invalid("Each selected field requires its original, including null.");
            var notes = desired.TryGetValue(key, out var n) ? Text(n) : null;
            var hasNotes = notes != null;
            var hasTitle = desired.ContainsKey("title");
            if (!hasNotes && !hasTitle) throw Invalid("Select at least one field. Clear Notes with an empty string.");
            if (RichNotes.ContainsDataImageSource(notes)) throw Invalid("Notes images must use external image URLs.");
            return new(hasNotes ? Text(originals[key]) : null, RichNotes.Normalize(notes), hasNotes, hasTitle,
                hasTitle ? Text(originals["title"]) : null, hasTitle ? TripCreditCodec.Title(Text(desired["title"])) : null);
        }
        catch (Exception error) when (error is JsonException or FormatException)
        { throw Invalid("The request contains invalid title or Notes data."); }
    }

    private static Dictionary<string, JsonElement> Fields(JsonElement value, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("An object is required.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!allowed.Contains(field.Name) || !fields.TryAdd(field.Name, field.Value))
                throw Invalid("Unknown or duplicate fields are not supported.");
        return fields;
    }

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        _ => throw Invalid("A string or null is required.")
    };

    private static TripMetadataMutationRejected Invalid(string detail) => new(400, "mutation-request-invalid", detail);
}
