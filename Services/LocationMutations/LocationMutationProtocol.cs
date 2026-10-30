using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wayfarer.Util;
using Wayfarer.Parsers;

namespace Wayfarer.Services.LocationMutations;

/// <summary>Strict immutable wire parsing and location-values-1 comparison; never reserializes a request for hashing.</summary>
internal sealed record LocationMutationCommand(JsonElement Body, string Adapter, string Kind, string Handle,
    string Normalization, JsonElement Expected, JsonElement Changes)
{
    internal static readonly string[] AddressFields = ["address", "fullAddress", "addressNumber", "streetName", "postCode", "place", "region", "country"];
    internal static readonly string[] AddressMembers = [.. AddressFields, "providerAddressLine1", "resolvedFeatureName", "resolvedFeatureType", "reverseGeocodingProvider", "reverseGeocodingStorageMode", "reverseGeocodedAt"];

    /// <summary>Rejects malformed UTF-8, duplicate/unknown names and invalid tagged shapes before admitting work.</summary>
    internal static LocationMutationCommand Parse(byte[] bytes)
    {
        try
        {
            var raw = new UTF8Encoding(false, true).GetString(bytes);
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement.Clone();
            ValidateScalars(body);
            var kind = Text(body.GetProperty("kind"));
            Members(body, kind == "update"
                ? ["contract", "normalization", "adapter", "baseHandle", "kind", "expected", "changes"]
                : ["contract", "normalization", "adapter", "baseHandle", "kind", "expected"]);
            Require(Text(body.GetProperty("contract")) == "location-mutation-1");
            var adapter = Text(body.GetProperty("adapter"));
            Require(adapter is "owner" or "web-edit");
            Require(kind is "update" or "delete");
            var expected = body.GetProperty("expected");
            var changes = kind == "update" ? body.GetProperty("changes") : default;
            var groups = new HashSet<string> { "uuid" };
            if (kind == "delete") groups.Add("content");
            else ValidateChanges(changes, adapter, groups);
            Members(expected, groups.ToArray());
            foreach (var field in expected.EnumerateObject()) ValidateSlot(field.Name, field.Value);
            return new(body, adapter, kind, Text(body.GetProperty("baseHandle")), Text(body.GetProperty("normalization")), expected, changes);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or DecoderFallbackException or ArgumentException)
        { throw new MutationProtocolException("mutationInvalid"); }
    }

    /// <summary>Defines the exact required original union and permitted values for each selected change.</summary>
    private static void ValidateChanges(JsonElement changes, string adapter, HashSet<string> groups)
    {
        Require(changes.ValueKind == JsonValueKind.Object && changes.EnumerateObject().Any());
        foreach (var field in changes.EnumerateObject())
        {
            var value = field.Value;
            var op = Text(value.GetProperty("op"));
            Require(op is "set" or "clear");
            switch (field.Name)
            {
                case "notes":
                    groups.Add("notesHtml"); Members(value, op == "set" ? ["op", "html"] : ["op"]);
                    if (op == "set") Text(value.GetProperty("html"));
                    break;
                case "activity":
                    groups.Add("activityTypeId"); Members(value, op == "set" ? ["op", "id"] : ["op"]);
                    if (op == "set") Require(value.GetProperty("id").TryGetInt32(out var id) && id > 0);
                    break;
                case "position":
                    groups.UnionWith(["position", "address"]); Require(op == "set");
                    Members(value, "op", "latitude", "longitude"); Position(value); break;
                case "eventUtc":
                    groups.Add("eventUtc"); Require(op == "set"); Members(value, "op", "value");
                    Instant(Text(value.GetProperty("value")), false); break;
                case "altitude":
                    Require(adapter == "web-edit"); groups.Add("altitude");
                    Members(value, op == "set" ? ["op", "value"] : ["op"]);
                    if (op == "set") Number(value.GetProperty("value")); break;
                case "manualAddress":
                    Require(adapter == "web-edit" && op == "set"); groups.UnionWith(["position", "address"]);
                    Members(value, "op", "fields"); var fields = value.GetProperty("fields");
                    Require(fields.ValueKind == JsonValueKind.Object && fields.EnumerateObject().Any());
                    foreach (var component in fields.EnumerateObject())
                    { Require(AddressFields.Contains(component.Name)); NullableText(component.Value); }
                    break;
                default: throw new MutationProtocolException("mutationInvalid");
            }
        }
    }

    /// <summary>Unavailable originals remain distinct from known nulls and cannot compare successfully.</summary>
    private static void ValidateSlot(string name, JsonElement slot)
    {
        var state = Text(slot.GetProperty("state"));
        if (state == "unavailable")
        {
            Members(slot, "state", "reason");
            Require(Text(slot.GetProperty("reason")) is "unrepresentableStoredValue" or "notRetainedOriginal"); return;
        }
        Require(state == "known"); Members(slot, "state", "value"); var value = slot.GetProperty("value");
        switch (name)
        {
            case "uuid": if (value.ValueKind != JsonValueKind.Null) Uuid(Text(value)); break;
            case "notesHtml": NullableText(value); break;
            case "altitude": if (value.ValueKind != JsonValueKind.Null) Number(value); break;
            case "activityTypeId": Require(value.ValueKind == JsonValueKind.Null || value.TryGetInt32(out _)); break;
            case "eventUtc": Instant(Text(value), true); break;
            case "position": Members(value, "latitude", "longitude"); Position(value); break;
            case "address":
                Members(value, AddressMembers);
                foreach (var item in value.EnumerateObject())
                    if (item.Name == "reverseGeocodedAt" && item.Value.ValueKind != JsonValueKind.Null) Instant(Text(item.Value), false);
                    else NullableText(item.Value);
                break;
            case "content":
                Members(value, "format", "fingerprint"); Require(Text(value.GetProperty("format")) == "location-delete-storage-1");
                Require(Regex.IsMatch(Text(value.GetProperty("fingerprint")), "^sha256:[0-9a-f]{64}$")); break;
            default: throw new MutationProtocolException("mutationInvalid");
        }
    }

    /// <summary>Checks every object for duplicate names and every decoded string for scalar Unicode and transportability.</summary>
    private static void ValidateScalars(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            { Require(seen.Add(field.Name) && ActivityCatalog.CanTransport(field.Name)); ValidateScalars(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateScalars(item);
        else if (value.ValueKind == JsonValueKind.String) Text(value);
    }

    /// <summary>Requires an exact, case-sensitive member set.</summary>
    internal static void Members(JsonElement value, params string[] names)
    {
        Require(value.ValueKind == JsonValueKind.Object);
        Require(value.EnumerateObject().Select(field => field.Name).Order().SequenceEqual(names.Order()));
    }

    internal static void Require(bool valid) { if (!valid) throw new MutationProtocolException("mutationInvalid"); }
    internal static string Text(JsonElement value)
    { Require(value.ValueKind == JsonValueKind.String); var text = value.GetString()!; Require(ActivityCatalog.CanTransport(text)); return text; }
    private static void NullableText(JsonElement value) { if (value.ValueKind != JsonValueKind.Null) Text(value); }
    internal static Guid Uuid(string value) { Require(Guid.TryParseExact(value, "D", out var id) && id.ToString("D") == value); return id; }
    internal static double Number(JsonElement value)
    { Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)); return value.GetDouble(); }
    private static void Position(JsonElement value) => Require(ReverseGeocodingService.HasValidCoordinates(Number(value.GetProperty("latitude")), Number(value.GetProperty("longitude"))));

    /// <summary>Matches Npgsql's toward-zero microsecond conversion, including instants before the PostgreSQL epoch.</summary>
    internal static DateTime Instant(string text, bool original)
    {
        Require(Regex.IsMatch(text, original ? @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d{1,9})?Z$" : @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d{1,6})?Z$"));
        var dot = text.IndexOf('.');
        var parsed = dot < 0 ? text : text[..dot] + "." + text[(dot + 1)..^1].PadRight(7, '0')[..7] + "Z";
        Require(DateTime.TryParseExact(parsed, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant));
        Require(instant != DateTime.MinValue && instant != DateTime.MaxValue);
        const long epoch = 630822816000000000;
        return new DateTime(epoch + (instant.Ticks - epoch) / 10 * 10, DateTimeKind.Utc);
    }

    /// <summary>Frames exact bytes with network-order lengths for request and storage fingerprints.</summary>
    internal static void Frame(IncrementalHash hash, byte[] bytes)
    {
        Span<byte> count = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(count, checked((uint)bytes.Length));
        hash.AppendData(count); hash.AppendData(bytes);
    }

    internal static string Fingerprint(string owner, int id, Guid baseId, Guid actionId, string adapter, byte[] body)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var text in new[] { "location-mutation-request-1", owner, id.ToString(CultureInfo.InvariantCulture), baseId.ToString("D"), actionId.ToString("D"), adapter })
            Frame(hash, Encoding.UTF8.GetBytes(text));
        Frame(hash, body); return "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Compares typed values, with only the approved Notes/time normalizations and signed-zero equality.</summary>
    internal static bool Equal(string group, JsonElement expected, JsonElement current)
    {
        if (group == "notesHtml") return RichNotes.Normalize(expected.ValueKind == JsonValueKind.Null ? null : expected.GetString())
            == RichNotes.Normalize(current.ValueKind == JsonValueKind.Null ? null : current.GetString());
        if (group is "eventUtc" or "reverseGeocodedAt")
            return expected.ValueKind == JsonValueKind.Null || current.ValueKind == JsonValueKind.Null
                ? expected.ValueKind == current.ValueKind : Instant(expected.GetString()!, true) == Instant(current.GetString()!, true);
        if (expected.ValueKind != current.ValueKind) return false;
        return expected.ValueKind switch
        {
            JsonValueKind.Object => expected.EnumerateObject().All(field => current.TryGetProperty(field.Name, out var other) && Equal(field.Name, field.Value, other)),
            JsonValueKind.Number => expected.GetDouble() == current.GetDouble(),
            JsonValueKind.String => expected.GetString() == current.GetString(),
            JsonValueKind.Null => true,
            _ => expected.GetRawText() == current.GetRawText()
        };
    }
}

/// <summary>A non-sensitive stable protocol failure, without request bodies or private winner values.</summary>
internal sealed class MutationProtocolException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}
