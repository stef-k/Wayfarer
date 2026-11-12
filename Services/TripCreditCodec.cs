using System.Text.Json;
using System.Text.Json.Serialization;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>One bounded portable credit representation for storage, native KML and generic Google attribution.</summary>
public static class TripCreditCodec
{
    /// <summary>The only author-selectable contribution policies. Null is legacy/unlicensed, never a grant.</summary>
    public static readonly string[] Policies = ["CC-BY-4.0", "CC0-1.0", "view-only"];
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    /// <summary>Versioned attribution envelope; policy describes the named source owner's contributions only.</summary>
    public sealed record Attribution(int Version, string SourceTitle, string? ContributionPolicy, IReadOnlyList<TripCredit> Credits);

    /// <summary>Null is accepted only for stored/imported absence, never as an implicit licence choice.</summary>
    public static string? Policy(string? value) => value == null || Policies.Contains(value, StringComparer.Ordinal)
        ? value : throw new FormatException("Unsupported contribution policy.");

    /// <summary>Rejects oversize, ambiguous or unknown JSON rather than stripping required terms.</summary>
    public static IReadOnlyList<TripCredit> Read(string json)
    {
        CheckJson(json);
        var credits = JsonSerializer.Deserialize<TripCredit[]>(json, Options) ?? throw new FormatException("Credits must be a list.");
        Validate(credits);
        return credits;
    }

    /// <summary>Validates complete readable credits without converting unfamiliar inherited licence text.</summary>
    public static void Validate(IReadOnlyList<TripCredit> credits)
    {
        if (credits.Count > 32) throw new FormatException("At most 32 credits are supported.");
        foreach (var credit in credits)
        {
            if (credit == null || string.IsNullOrWhiteSpace(credit.Contributor) && string.IsNullOrWhiteSpace(credit.SourceTitle))
                throw new FormatException("A credit needs a readable contributor or source description.");
            Text(credit.Contributor, 200); Text(credit.SourceTitle, 200); Text(credit.Licence, 4000);
            Text(credit.Notices, 4000); Text(credit.Modifications, 2000);
            Url(credit.LicenceUrl); Url(credit.SourceUrl);
        }
    }

    /// <summary>Produces the canonical JSON used for original comparisons as well as persistence.</summary>
    public static string Write(IReadOnlyList<TripCredit> credits)
    {
        Validate(credits);
        var json = JsonSerializer.Serialize(credits, Options);
        if (json.Length > 262144) throw new FormatException("Credits exceed the supported payload limit.");
        return json;
    }

    /// <summary>Encodes the same policy/credit projection for native and Google files.</summary>
    public static string WriteAttribution(Trip trip) => JsonSerializer.Serialize(
        new Attribution(1, trip.Name, Policy(trip.ContributionPolicy), Read(Write(trip.Credits))), Options);

    /// <summary>Reads a strict typed projection with no rights inference from links or source identities.</summary>
    public static Attribution ReadAttribution(string json)
    {
        CheckJson(json);
        var value = JsonSerializer.Deserialize<Attribution>(json, Options) ?? throw new FormatException("Attribution is required.");
        if (value.Version != 1 || value.Credits == null || string.IsNullOrWhiteSpace(value.SourceTitle))
            throw new FormatException("Unsupported attribution structure.");
        Text(value.SourceTitle, 200); Policy(value.ContributionPolicy); Validate(value.Credits);
        return value;
    }

    /// <summary>Readable plain text, escaped by each output's normal text renderer.</summary>
    public static string Readable(Attribution value) =>
        $"Owner's contributions ({value.SourceTitle}): {value.ContributionPolicy ?? "unlicensed / view-only"}." +
        string.Concat(value.Credits.Select(c => $"\n{c.Contributor} · {c.SourceTitle} · {c.Licence}" +
            $"\n{c.LicenceUrl}\n{c.Notices}\n{c.Modifications}\n{c.SourceUrl}"));

    /// <summary>Independent copies inherit source grants descriptively, never as the new owner's publication choice.</summary>
    public static IReadOnlyList<TripCredit> Inherited(Trip source) => Inherited(
        new Attribution(1, source.Name, source.ContributionPolicy, source.Credits));

    /// <summary>Preserves inherited terms and a source contribution grant without account IDs or private origins.</summary>
    public static IReadOnlyList<TripCredit> Inherited(Attribution source)
    {
        var result = source.Credits.ToList();
        if (source.ContributionPolicy is "CC-BY-4.0" or "CC0-1.0")
            result.Add(new("", source.SourceTitle, source.ContributionPolicy));
        Validate(result);
        return result.Distinct().ToArray();
    }

    /// <summary>Nullable title is independent of transport mode; an empty authored title explicitly clears it.</summary>
    public static string? Title(string? value)
    {
        Text(value, 200);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static void Text(string? value, int limit)
    {
        if (value?.Length > limit || value?.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t') == true)
            throw new FormatException("Portable text exceeds its supported limits.");
    }

    private static void Url(string? value)
    {
        Text(value, 2000);
        if (value != null && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0))
            throw new FormatException("Credit links must be absolute HTTP(S) URLs without credentials.");
    }

    private static void CheckJson(string json)
    {
        if (json.Length > 262144) throw new FormatException("Attribution exceeds the supported payload limit.");
        try
        {
            using var syntax = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            Unique(syntax.RootElement);
        }
        catch (JsonException error) { throw new FormatException("Invalid attribution JSON.", error); }
    }

    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Unique(item);
        if (value.ValueKind != JsonValueKind.Object) return;
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (var field in value.EnumerateObject())
        {
            if (!names.Add(field.Name)) throw new FormatException("Duplicate attribution field.");
            Unique(field.Value);
        }
    }
}
