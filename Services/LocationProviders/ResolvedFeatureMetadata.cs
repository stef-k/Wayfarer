namespace Wayfarer.Services.LocationProviders;

/// <summary>Normalizes optional provider-returned named-feature metadata at persistence boundaries.</summary>
public static class ResolvedFeatureMetadata
{
    private static readonly HashSet<string> GeoapifyTypes = new(StringComparer.Ordinal)
    {
        "amenity", "building", "street", "suburb", "district", "postcode", "city", "county", "state", "country"
    };

    /// <summary>Returns a valid trimmed feature name, or null without truncating invalid input.</summary>
    public static string? NormalizeName(string? value) => Normalize(value, 500);

    /// <summary>Returns a documented lower-case Geoapify result type, or null.</summary>
    public static string? NormalizeGeoapifyType(string? value)
    {
        var normalized = Normalize(value, 32)?.ToLowerInvariant();
        return normalized != null && GeoapifyTypes.Contains(normalized) ? normalized : null;
    }

    /// <summary>Authenticates and normalizes one imported enrichment tuple.</summary>
    public static ResolvedFeatureTuple NormalizeImported(
        string? name, string? type, string? provider, string? storageMode, string? timestamp)
    {
        var normalizedProvider = provider?.Trim().ToLowerInvariant();
        var normalizedMode = storageMode?.Trim().ToLowerInvariant();
        var hasTimestamp = DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsedAt);
        var validProvenance = hasTimestamp && (normalizedProvider == "geoapify" && normalizedMode == "persistent"
                || normalizedProvider == "mapbox" && normalizedMode == "permanent");
        if (!validProvenance) return default;
        var normalizedName = NormalizeName(name);
        var normalizedType = NormalizeGeoapifyType(type);
        if (normalizedName == null && normalizedType == null) return default;
        return new(normalizedName, normalizedType, normalizedProvider, normalizedMode, parsedAt.ToUniversalTime());
    }

    private static string? Normalize(string? value, int maximumLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > maximumLength || trimmed.Any(char.IsControl)
            ? null : trimmed;
    }
}

/// <summary>Validated optional feature metadata with its complete enrichment provenance.</summary>
public readonly record struct ResolvedFeatureTuple(
    string? Name, string? Type, string? Provider, string? StorageMode, DateTimeOffset? EnrichedAt);
