using System.ComponentModel.DataAnnotations;
using Wayfarer.Models.LocationProviders;

namespace Wayfarer.Areas.User.LocationProviderModels;

/// <summary>Contains only masked personal provider settings presentation.</summary>
public sealed class LocationProviderSettingsViewModel
{
    public IReadOnlyList<LocationProviderProfileViewModel> Profiles { get; init; } = [];
    public string? ActiveGeocodingProvider { get; init; }
    public string? ActiveRoutingProvider { get; init; }
    public LegacyMapboxMigrationState LegacyMigrationState { get; init; }
}

/// <summary>Presents bounded profile, capability, and provider-native usage state.</summary>
public sealed record LocationProviderProfileViewModel(
    string ProviderKey, string DisplayName, bool CredentialConfigured, string Mask,
    bool GeocodingAuthorized, PersonalProviderVerification GeocodingVerification,
    bool RoutingAuthorized, PersonalProviderVerification RoutingVerification,
    bool GuardEnabled, int Limit, int Used, string Unit, string WindowExplanation, bool Exhausted,
    bool? DirectionsGuardEnabled = null, int? DirectionsLimit = null, int? DirectionsUsed = null);

/// <summary>Accepts explicit profile replacement/authorization and independent selection.</summary>
public sealed class LocationProviderProfileInput
{
    [Required, RegularExpression("geoapify|mapbox")] public string ProviderKey { get; set; } = string.Empty;
    [DataType(DataType.Password), StringLength(2048)] public string? ReplacementCredential { get; set; }
    public bool GeocodingAuthorized { get; set; }
    public bool RoutingAuthorized { get; set; }
    public bool ActiveForGeocoding { get; set; }
    public bool ActiveForRouting { get; set; }
}

/// <summary>Accepts one bounded provider-native guard setting.</summary>
public sealed class LocationProviderGuardInput
{
    [Required, RegularExpression("geoapify|mapbox-permanent|mapbox-directions")] public string GuardKey { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    [Range(0, 10_000_000)] public int Limit { get; set; }
}
