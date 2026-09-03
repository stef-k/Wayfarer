using Wayfarer.Models;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Owns the closed provider-native directions modes exposed by Wayfarer.</summary>
public static class ProviderDirectionsCatalog
{
    /// <summary>Advances whenever executable provider-mode authority changes.</summary>
    public const int AuthorityVersion = 1;
    private static readonly ProviderDirectionsMode[] GeoapifyModes =
    [
        new("walk", "Walk"),
        new("bicycle", "Bicycle"),
        new("motorcycle", "Motorcycle"),
        new("drive", "Drive"),
        new("bus", "Bus")
    ];

    /// <summary>Returns the complete implemented catalog for one provider.</summary>
    public static IReadOnlyList<ProviderDirectionsMode> For(string? providerKey) =>
        providerKey == "geoapify" ? GeoapifyModes : [];

    /// <summary>Parses only an exact native mode implemented by the provider.</summary>
    public static bool TryParse(string? providerKey, string? nativeMode, out ProviderDirectionsMode mode)
    {
        mode = For(providerKey).SingleOrDefault(item => item.Key == nativeMode)!;
        return mode != null;
    }
}

/// <summary>Describes one provider-owned directions mode safe for presentation.</summary>
public sealed record ProviderDirectionsMode(string Key, string Label);

/// <summary>Maps only released-Mobile omitted-mode requests from exact built-in stable keys.</summary>
public static class ReleasedMobileDirectionsCompatibility
{
    /// <summary>Returns a Geoapify mode only for the reviewed exact built-in profile keys.</summary>
    public static bool TryMap(TransportProfile profile, out string mode)
    {
        mode = profile.Key switch
        {
            "walk" => "walk",
            "bicycle" or "bike" => "bicycle",
            "car" => "drive",
            "bus" => "bus",
            _ => string.Empty
        };
        return mode.Length > 0;
    }
}
