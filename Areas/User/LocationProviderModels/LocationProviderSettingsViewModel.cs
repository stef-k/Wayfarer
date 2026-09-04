using System.ComponentModel.DataAnnotations;
using Wayfarer.Models.LocationProviders;

namespace Wayfarer.Areas.User.LocationProviderModels;

/// <summary>Contains only masked personal provider settings presentation.</summary>
public sealed class LocationProviderSettingsViewModel
{
    public IReadOnlyList<LocationProviderProfileViewModel> Profiles { get; init; } = [];
    public string? ActiveGeocodingProvider { get; init; }
    public string? ActiveRoutingProvider { get; init; }
    public string GeocodingStatus { get; init; } = "No provider selected. Verify a credential, then choose it.";
    public string RoutingStatus { get; init; } = "No provider selected. Verify Geoapify, then choose it.";
    public LegacyMapboxMigrationState LegacyMigrationState { get; init; }
}

/// <summary>Accepts a credential replacement without changing provider choice implicitly.</summary>
public sealed class LocationProviderCredentialInput
{
    [Required, RegularExpression("^(?:geoapify|mapbox)$")] public string ProviderKey { get; set; } = string.Empty;
    [Required, DataType(DataType.Password), StringLength(2048), RegularExpression(@"^[^\s\p{Cc}]*$",
        ErrorMessage = "Credentials cannot contain whitespace or control characters.")]
    public string ReplacementCredential { get; set; } = string.Empty;
}

/// <summary>Accepts one capability-oriented provider choice.</summary>
public sealed class LocationProviderChoiceInput
{
    [Required, RegularExpression("^(?:Geocoding|Routing)$")] public string Capability { get; set; } = string.Empty;
    [StringLength(32)] public string ProviderKey { get; set; } = string.Empty;
}

/// <summary>Presents bounded profile, capability, and provider-native usage state.</summary>
public sealed record LocationProviderProfileViewModel(
    string ProviderKey, string DisplayName, bool CredentialConfigured, string Mask,
    bool GeocodingAuthorized, PersonalProviderVerification GeocodingVerification,
    bool GeocodingEligible, string GeocodingBlockingStatus,
    bool RoutingAuthorized, PersonalProviderVerification RoutingVerification,
    bool RoutingEligible, string RoutingBlockingStatus,
    bool GuardEnabled, int Limit, int Used, string Unit, string WindowExplanation, bool Exhausted,
    bool? DirectionsGuardEnabled = null, int? DirectionsLimit = null, int? DirectionsUsed = null,
    bool PermanentConsentCurrent = false, int? PermanentConsentVersion = null,
    DateTimeOffset? PermanentConsentedAt = null, string? PausedReason = null, DateOnly? CycleStart = null);

/// <summary>Accepts explicit profile replacement/authorization and independent selection.</summary>
public sealed class LocationProviderProfileInput
{
    [Required, RegularExpression("^(?:geoapify|mapbox)$")] public string ProviderKey { get; set; } = string.Empty;
    [DataType(DataType.Password), StringLength(2048), RegularExpression(@"^[^\s\p{Cc}]*$",
        ErrorMessage = "Credentials cannot contain whitespace or control characters.")]
    public string? ReplacementCredential { get; set; }
    public bool GeocodingAuthorized { get; set; }
    public bool RoutingAuthorized { get; set; }
    public bool ActiveForGeocoding { get; set; }
    public bool ActiveForRouting { get; set; }
}

/// <summary>Accepts one bounded provider-native guard setting.</summary>
public sealed class LocationProviderGuardInput
{
    [Required, RegularExpression("^(?:geoapify|mapbox-permanent|mapbox-directions)$")] public string GuardKey { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    [Range(0, 10_000_000)] public int Limit { get; set; }
}

/// <summary>Accepts all explicit acknowledgements required for Mapbox Permanent Geocoding.</summary>
public sealed class MapboxPermanentConsentInput
{
    [Range(typeof(bool), "true", "true", ErrorMessage = "Confirm that stored Mapbox enrichment was chosen.")] public bool StorageAcknowledged { get; set; }
    [Range(typeof(bool), "true", "true", ErrorMessage = "Confirm that Permanent Geocoding may incur charges.")] public bool BillingAcknowledged { get; set; }
    [Range(typeof(bool), "true", "true", ErrorMessage = "Confirm eligible Mapbox billing or enterprise terms.")] public bool BillingEligibilityAcknowledged { get; set; }
    [Range(typeof(bool), "true", "true", ErrorMessage = "Confirm that Wayfarer meters only its own contacts.")] public bool WayfarerMeterAcknowledged { get; set; }
    [Range(typeof(bool), "true", "true", ErrorMessage = "Confirm external applications may consume the allowance.")] public bool ExternalUsageAcknowledged { get; set; }
}
