using Wayfarer.Models;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Identifies the closed initial Geoapify routing-mode catalog.</summary>
public enum GeoapifyRoutingMode { Walk, Bicycle, Motorcycle, Drive, Bus }

/// <summary>Calculates conservative Geoapify credits before provider contact.</summary>
public static class GeoapifyRouteCost
{
    /// <summary>Calculates checked cost for the consecutive pairs in 2–25 waypoints.</summary>
    public static int Calculate(GeoapifyRoutingMode mode, int waypointCount)
    {
        if (waypointCount is < 2 or > 25) throw new ArgumentOutOfRangeException(nameof(waypointCount));
        return CalculatePairs(mode, waypointCount - 1);
    }

    /// <summary>Calculates a checked cost for an already validated positive pair count.</summary>
    public static int CalculatePairs(GeoapifyRoutingMode mode, int pairCount)
    {
        if (pairCount <= 0) throw new ArgumentOutOfRangeException(nameof(pairCount));
        var perPair = mode switch
        {
            GeoapifyRoutingMode.Walk or GeoapifyRoutingMode.Bicycle => 1,
            GeoapifyRoutingMode.Motorcycle or GeoapifyRoutingMode.Drive or GeoapifyRoutingMode.Bus => 21,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        return checked(pairCount * perPair);
    }

    /// <summary>Parses only an exact supported persisted value.</summary>
    public static bool TryParse(string? value, out GeoapifyRoutingMode mode) => Enum.TryParse(value, true, out mode)
        && value == NativeMode(mode);

    /// <summary>Returns the exact provider-native value.</summary>
    public static string NativeMode(GeoapifyRoutingMode mode) => mode switch
    {
        GeoapifyRoutingMode.Walk => "walk",
        GeoapifyRoutingMode.Bicycle => "bicycle",
        GeoapifyRoutingMode.Motorcycle => "motorcycle",
        GeoapifyRoutingMode.Drive => "drive",
        GeoapifyRoutingMode.Bus => "bus",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}

/// <summary>Applies provider-specific anchor limits after authoritative provider resolution.</summary>
public static class RoutingProviderAnchorPolicy
{
    /// <summary>Returns a bounded provider input error without admitting generation or provider budgets.</summary>
    public static string? Validate(ResolvedRoutingProviderExecution execution, IReadOnlyList<RouteCoordinate> anchors) =>
        execution.Provider.AdapterType == RoutingAdapterType.Geoapify && anchors.Count > 25
            ? "routing-cost-invalid" : null;
}

/// <summary>Identifies bounded mapping resolution without inferring from display text.</summary>
public enum ProviderTransportProfileCategory { Supported, Unmapped, Unsupported }

/// <summary>Contains safe stable mapping authority for provider work.</summary>
public sealed record ProviderTransportProfileResolution(
    ProviderTransportProfileCategory Category, string? NativeMode, string? Authority);

/// <summary>Resolves only explicit provider-configuration plus stable-profile mappings.</summary>
public static class ProviderTransportProfileResolver
{
    /// <summary>Resolves one exact mapping and validates it against the selected adapter catalog.</summary>
    public static ProviderTransportProfileResolution Resolve(
        RoutingProviderConfiguration configuration, TransportProfile profile)
    {
        var mapping = configuration.ProfileMappings.SingleOrDefault(item => item.TransportProfileId == profile.Id);
        if (mapping == null)
            return new(ProviderTransportProfileCategory.Unmapped, null, null);
        var nativeMode = mapping.ProviderNativeMode;
        var supported = configuration.AdapterType switch
        {
            RoutingAdapterType.Geoapify => GeoapifyRouteCost.TryParse(nativeMode, out _),
            RoutingAdapterType.MapboxDirections => nativeMode is "mapbox/driving" or "mapbox/driving-traffic"
                or "mapbox/walking" or "mapbox/cycling",
            RoutingAdapterType.OsrmCompatible => !string.IsNullOrWhiteSpace(nativeMode),
            _ => false
        };
        if (!supported)
            return new(ProviderTransportProfileCategory.Unsupported, null, null);
        var authority = $"{configuration.Id:N}:{configuration.ConfigurationVersion}:{profile.Id:N}:{nativeMode}";
        return new(ProviderTransportProfileCategory.Supported, nativeMode, authority);
    }
}
