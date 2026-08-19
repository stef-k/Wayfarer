namespace Wayfarer.Services.ExternalRouting;

/// <summary>Owns explicit server-default and personal resolution outcomes.</summary>
public sealed class AuthoritativeRoutingProviderResolver
{
    /// <summary>Creates a terminal personal-unavailable result.</summary>
    public static RoutingProviderResolutionResult UnavailablePersonal(string errorCode) =>
        new(RoutingProviderResolutionOutcome.UnavailablePersonal, errorCode, false);
}

/// <summary>Identifies the authoritative provider-selection outcome.</summary>
public enum RoutingProviderResolutionOutcome
{
    /// <summary>The explicitly selected global configuration is authoritative.</summary>
    ServerDefault,
    /// <summary>The explicitly selected personal template is authoritative.</summary>
    ResolvedPersonal,
    /// <summary>Personal mode is selected but unavailable; fallback is forbidden.</summary>
    UnavailablePersonal,
    /// <summary>The global feature is disabled.</summary>
    ExternalRoutingDisabled
}

/// <summary>Contains a bounded provider-resolution outcome.</summary>
public sealed record RoutingProviderResolutionResult(
    RoutingProviderResolutionOutcome Outcome, string? ErrorCode, bool MayResolveServerDefault);

/// <summary>Identifies the provider selection bound into protected proposals.</summary>
public enum RoutingProviderSelectionMode
{
    /// <summary>The global administrator selection was used.</summary>
    ServerDefault,
    /// <summary>The user's approved personal selection was used.</summary>
    Personal
}
