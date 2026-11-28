using System.Text;
using System.Text.Json;

namespace Wayfarer.Services.TripMetadataReceipts;

/// <summary>Checks the protected JSON lane without altering the bytes bound by the permit.</summary>
internal static class TripStructureBodies
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Removes the transport marker only after the caller has verified the frozen digest.</summary>
    internal static JsonElement ReadWrite(byte[] body)
    {
        var root = ReadObject(body);
        if (!root.TryGetProperty("receiptContract", out var marker) || marker.ValueKind != JsonValueKind.String
            || marker.GetString() != TripMetadataIdentity.StructureVersion)
            throw Invalid();
        return JsonSerializer.SerializeToElement(root.EnumerateObject()
            .Where(property => property.Name != "receiptContract").ToDictionary(property => property.Name, property => property.Value));
    }

    /// <summary>Validates one UTF-8 object with no BOM, malformed Unicode or duplicate decoded member names.</summary>
    internal static JsonElement ReadObject(byte[] body)
    {
        try
        {
            if (body.AsSpan().StartsWith(Encoding.UTF8.Preamble)) throw Invalid();
            _ = StrictUtf8.GetCharCount(body);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            RequireUnique(root);
            return root.Clone();
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            throw Invalid();
        }
    }

    /// <summary>Checks nested objects, including dynamic membership maps, before the domain binder sees them.</summary>
    private static void RequireUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) _ = value.GetString();
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in value.EnumerateArray()) RequireUnique(entry);
        }
        if (value.ValueKind != JsonValueKind.Object) return;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
        {
            if (!names.Add(field.Name)) throw Invalid();
            if (Guid.TryParse(field.Name, out var id) && (id == Guid.Empty || id.ToString("D") != field.Name)) throw Invalid();
            RequireUnique(field.Value);
        }
    }

    /// <summary>Returns the fixed invalid-transport error without submitted content.</summary>
    internal static TripMetadataMutationRejected Invalid() =>
        new(400, "mutation-request-invalid", "Protected Trip edits require one strict UTF-8 JSON object.");
}
