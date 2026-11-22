using Wayfarer.Models.LocationProviders;

namespace Wayfarer.Localization;

/// <summary>Translates bounded provider status at the settings document's presentation boundary.</summary>
public static class ProviderSettingsText
{
    /// <summary>Formats the redirect status with only a recognized capability label as its argument.</summary>
    public static string RedirectStatus(WebMessages text, string key, string? capability)
    {
        if (!key.StartsWith("Providers.Status.", StringComparison.Ordinal))
            return text["Providers.Status.Unknown"];
        return capability switch
        {
            "Geocoding" => text[key, text["Providers.Geocoding"]],
            "Routing" => text[key, text["Providers.Directions"]],
            _ => text[key]
        };
    }

    /// <summary>Describes the verification state without changing the capability decision.</summary>
    public static string Verification(WebMessages text, PersonalProviderVerification state) => text[state switch
    {
        PersonalProviderVerification.Verified => "Providers.Verified",
        PersonalProviderVerification.Failed => "Providers.VerificationFailed",
        PersonalProviderVerification.Unavailable => "Providers.VerificationUnavailable",
        _ => "Providers.Unverified"
    }];

    /// <summary>Translates the selected provider's effective readiness with a fixed display name.</summary>
    public static string Selection(WebMessages text, string? selected, string status, bool routing = false) => selected switch
    {
        null => text[routing ? "Providers.NotSelectedDirections" : "Providers.NotSelectedGeocoding"],
        "geoapify" when status == "Ready with geoapify." => text["Providers.ReadyWith", "Geoapify"],
        "mapbox" when status == "Ready with mapbox." => text["Providers.ReadyWith", "Mapbox"],
        _ => Eligibility(text, status)
    };

    /// <summary>Maps the service's legacy descriptions without changing other consumers' response contracts.</summary>
    public static string Eligibility(WebMessages text, string status) => text[status switch
    {
        "Blocked. Replace the credential and verify again." => "Providers.BlockedCredential",
        "Blocked. Mapbox Directions is not available." => "Providers.BlockedDirections",
        "Blocked. Enable and verify this capability again." => "Providers.BlockedAuthorization",
        "Blocked. Renew Permanent Geocoding consent and verify again." => "Providers.BlockedConsent",
        "Blocked. Verify this capability again." => "Providers.BlockedVerification",
        "Permanent meter exhausted" => "Providers.PermanentExhausted",
        _ => "Providers.BlockedUnknown"
    }];
}
