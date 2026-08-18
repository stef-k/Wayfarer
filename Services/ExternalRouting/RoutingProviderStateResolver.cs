using Wayfarer.Models;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Derives the deterministic administrator-visible configuration state.</summary>
public static class RoutingProviderStateResolver
{
    /// <summary>Resolves state without persisting a redundant active flag.</summary>
    public static RoutingProviderState Resolve(RoutingProviderConfiguration configuration, bool isActive)
    {
        if (!IsComplete(configuration)) return RoutingProviderState.Incomplete;
        if (!IsValid(configuration)) return RoutingProviderState.Invalid;
        if (isActive && configuration.VerifiedConfigurationVersion == configuration.ConfigurationVersion)
            return RoutingProviderState.Active;
        return configuration.VerifiedConfigurationVersion == configuration.ConfigurationVersion
            ? RoutingProviderState.Verified
            : RoutingProviderState.Ready;
    }

    private static bool IsComplete(RoutingProviderConfiguration value) =>
        value.Enabled && !string.IsNullOrWhiteSpace(value.DisplayName) && !string.IsNullOrWhiteSpace(value.BaseEndpoint)
        && value.VerificationFromLongitude.HasValue && value.VerificationFromLatitude.HasValue
        && value.VerificationToLongitude.HasValue && value.VerificationToLatitude.HasValue
        && value.ProfileMappings.Count > 0;

    private static bool IsValid(RoutingProviderConfiguration value) =>
        Uri.TryCreate(value.BaseEndpoint, UriKind.Absolute, out var endpoint)
        && (endpoint.Scheme == Uri.UriSchemeHttps || endpoint.Scheme == Uri.UriSchemeHttp)
        && CoordinatesValid(value.VerificationFromLongitude!.Value, value.VerificationFromLatitude!.Value)
        && CoordinatesValid(value.VerificationToLongitude!.Value, value.VerificationToLatitude!.Value)
        && value.ProfileMappings.All(mapping => !string.IsNullOrWhiteSpace(mapping.OsrmProfile));

    private static bool CoordinatesValid(double longitude, double latitude) =>
        double.IsFinite(longitude) && double.IsFinite(latitude) && longitude is >= -180 and <= 180 && latitude is >= -90 and <= 90;
}

/// <summary>Describes the derived lifecycle of a provider configuration.</summary>
public enum RoutingProviderState { Incomplete, Invalid, Ready, Verified, Active }
