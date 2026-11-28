using System.Text.Json;
using Wayfarer.Services;
using Wayfarer.Services.TripMetadataReceipts;
using static Wayfarer.Models.Dtos.TripMetadataConditionalRequest;

namespace Wayfarer.Models.Dtos;

/// <summary>Bounded JSON grammars for protected tag, sharing and saved-view operations.</summary>
internal static class TripControlRequest
{
    /// <summary>Decodes an immutable body using the route's specific grammar.</summary>
    internal static async Task<object> ReadAsync(Stream body, TripMetadataIdentity identity, CancellationToken token)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: token);
            var names = identity.Kind switch
            {
                "tags-replace" when identity.Adapter == "bearer" => new[] { "expected", "tags" },
                "tags-replace" or "tags-attach" => ["tags"],
                "tags-detach" => ["slug"],
                "editor-sharing" or "viewer-sharing" => ["enabled"],
                "saved-view" => ["expected", "value"],
                _ => throw Invalid("request", "Unsupported Trip operation.")
            };
            var fields = Fields(document.RootElement, ["receiptContract", .. names]);
            if (fields.Count != names.Length + 1 || !fields.TryGetValue("receiptContract", out var marker)
                || marker.ValueKind != JsonValueKind.String || marker.GetString() != identity.Contract)
                throw Invalid("request", "All operation fields and a matching receipt contract are required.");
            return identity.Kind switch
            {
                "tags-replace" or "tags-attach" => ReadTags(fields),
                "tags-detach" => new TripTagChange([], null, RequiredText(fields["slug"], "slug")),
                "saved-view" => ReadSavedView(fields),
                _ when fields["enabled"].ValueKind is JsonValueKind.True or JsonValueKind.False => fields["enabled"].GetBoolean(),
                _ => throw Invalid("enabled", "Enabled must be a boolean.")
            };
        }
        catch (JsonException) { throw Invalid("request", "The request must be valid JSON."); }
    }

    /// <summary>Reads exact published identities and preserves the author's submitted names.</summary>
    private static TripTagChange ReadTags(Dictionary<string, JsonElement> fields)
    {
        var names = Array(fields["tags"], "tags").Select(value => NullableString("tags", value)
            ?? throw Invalid("tags", "Each tag must be a string.")).ToArray();
        if (!fields.TryGetValue("expected", out var original)) return new(names, null, null);
        var expected = new HashSet<TripTagIdentity>();
        var ids = new HashSet<Guid>();
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Array(original, "expected"))
        {
            var pair = Fields(entry, ["id", "slug"]);
            if (pair.Count != 2 || pair["id"].ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(pair["id"].GetString(), "D", out var id) || id == Guid.Empty)
                throw Invalid("expected", "Each original must include a tag ID and slug.");
            var slug = RequiredText(pair["slug"], "slug");
            if (!ids.Add(id) || !slugs.Add(slug)) throw Invalid("expected", "Original tags must be unique.");
            expected.Add(new(id, slug));
        }
        return new(names, expected, null);
    }

    /// <summary>Reset retains all originals and Set requires a complete bounded view.</summary>
    private static TripSavedViewChange ReadSavedView(Dictionary<string, JsonElement> fields)
    {
        var expected = ReadView(Fields(fields["expected"], ["center", "zoom"]), false);
        var value = Fields(fields["value"], ["mode", "center", "zoom"]);
        if (!value.Remove("mode", out var mode)) throw Invalid("mode", "Set or Reset is required.");
        return RequiredText(mode, "mode") switch
        {
            "reset" when value.Count == 0 => new(expected, new(new(null, null), null)),
            "set" => new(expected, ReadView(value, true)),
            _ => throw Invalid("mode", "Use a complete Set or an empty Reset.")
        };
    }

    /// <summary>Both coordinate originals and zoom participate in the same compare.</summary>
    private static TripSavedView ReadView(Dictionary<string, JsonElement> fields, bool desired)
    {
        if (fields.Count != 2) throw Invalid("center", "Center and zoom are required.");
        var center = ReadCenter(fields["center"], desired);
        var zoom = ReadZoom(fields["zoom"], desired);
        if (desired && (center.Latitude == null || zoom == null)) throw Invalid("center", "Set requires a complete view.");
        return new(center, zoom);
    }

    /// <summary>Checks the array type before reading entries.</summary>
    private static JsonElement.ArrayEnumerator Array(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : throw Invalid(field, "An array is required.");

    /// <summary>Retains exact spelling for identity comparison.</summary>
    private static string RequiredText(JsonElement value, string field) => NullableString(field, value) is { Length: > 0 } text
        ? text : throw Invalid(field, "A string is required.");
}

internal sealed record TripTagChange(string[] Names, HashSet<TripTagIdentity>? Expected, string? Slug);
internal sealed record TripSavedViewChange(TripSavedView Expected, TripSavedView Value);
