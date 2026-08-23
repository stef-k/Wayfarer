using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models.LocationProviders;

/// <summary>Identifies a supported personal location provider.</summary>
public enum PersonalLocationProvider { Geoapify = 1, Mapbox = 2 }

/// <summary>Identifies an independently authorized provider capability.</summary>
public enum PersonalProviderCapability { Geocoding = 1, Routing = 2 }

/// <summary>Identifies bounded provider-native products used by usage diagnostics.</summary>
public enum PersonalProviderProduct { Geocoding = 1, Routing = 2, PermanentGeocoding = 3, Directions = 4 }

/// <summary>Contains bounded verification state and never provider response content.</summary>
public enum PersonalProviderVerification { Unverified = 0, Verified = 1, Failed = 2, Unavailable = 3 }

/// <summary>Owns one protected credential and independent capability authority for one user/provider.</summary>
public sealed class PersonalLocationProviderProfile
{
    /// <summary>Gets or sets the stable profile identifier.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Gets or sets the owning Identity user.</summary>
    [StringLength(450)] public string UserId { get; set; } = string.Empty;
    /// <summary>Gets or sets the normalized stable provider key.</summary>
    [StringLength(24)] public string ProviderKey { get; set; } = string.Empty;
    /// <summary>Gets or sets protected credential material.</summary>
    [StringLength(4096)] public string? ProtectedCredential { get; set; }
    /// <summary>Gets or sets the monotonic credential authority generation.</summary>
    public int CredentialGeneration { get; set; } = 1;
    /// <summary>Gets or sets when the credential was explicitly revoked.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
    /// <summary>Gets or sets independent geocoding authorization.</summary>
    public bool GeocodingAuthorized { get; set; }
    /// <summary>Gets or sets independent routing authorization.</summary>
    public bool RoutingAuthorized { get; set; }
    /// <summary>Gets or sets the geocoding configuration generation.</summary>
    public int GeocodingGeneration { get; set; } = 1;
    /// <summary>Gets or sets the routing configuration generation.</summary>
    public int RoutingGeneration { get; set; } = 1;
    /// <summary>Gets or sets bounded geocoding verification.</summary>
    public PersonalProviderVerification GeocodingVerification { get; set; }
    /// <summary>Gets or sets bounded routing verification.</summary>
    public PersonalProviderVerification RoutingVerification { get; set; }
    /// <summary>Gets or sets the credential generation verified for geocoding.</summary>
    public int? GeocodingVerifiedCredentialGeneration { get; set; }
    /// <summary>Gets or sets the capability generation verified for geocoding.</summary>
    public int? GeocodingVerifiedConfigurationGeneration { get; set; }
    /// <summary>Gets or sets the credential generation verified for routing.</summary>
    public int? RoutingVerifiedCredentialGeneration { get; set; }
    /// <summary>Gets or sets the capability generation verified for routing.</summary>
    public int? RoutingVerifiedConfigurationGeneration { get; set; }
    /// <summary>Gets or sets bounded legacy migration state.</summary>
    public LegacyMapboxMigrationState LegacyMigrationState { get; set; }
    /// <summary>Gets or sets the last mutation time.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Gets the PostgreSQL concurrency token.</summary>
    public uint RowVersion { get; private set; }

    /// <summary>Creates an empty normalized profile.</summary>
    public static PersonalLocationProviderProfile Create(string userId, PersonalLocationProvider provider) => new()
    { UserId = userId, ProviderKey = PersonalProviderKeys.Key(provider) };

    /// <summary>Returns whether the capability is explicitly authorized.</summary>
    public bool IsAuthorized(PersonalProviderCapability capability) => capability switch
    {
        PersonalProviderCapability.Geocoding => GeocodingAuthorized,
        PersonalProviderCapability.Routing => RoutingAuthorized,
        _ => false
    };

    /// <summary>Changes only the requested capability authority and invalidates its verification.</summary>
    public void SetAuthorization(PersonalProviderCapability capability, bool authorized)
    {
        if (capability == PersonalProviderCapability.Geocoding && GeocodingAuthorized != authorized)
        { GeocodingAuthorized = authorized; GeocodingGeneration++; ClearVerification(capability); }
        else if (capability == PersonalProviderCapability.Routing && RoutingAuthorized != authorized)
        { RoutingAuthorized = authorized; RoutingGeneration++; ClearVerification(capability); }
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Clears bounded verification for one capability.</summary>
    public void ClearVerification(PersonalProviderCapability capability)
    {
        if (capability == PersonalProviderCapability.Geocoding)
        { GeocodingVerification = PersonalProviderVerification.Unverified; GeocodingVerifiedCredentialGeneration = null; GeocodingVerifiedConfigurationGeneration = null; }
        else
        { RoutingVerification = PersonalProviderVerification.Unverified; RoutingVerifiedCredentialGeneration = null; RoutingVerifiedConfigurationGeneration = null; }
    }
}

/// <summary>Normalizes the only supported provider identities.</summary>
public static class PersonalProviderKeys
{
    /// <summary>Gets a stable lower-case storage key.</summary>
    public static string Key(PersonalLocationProvider provider) => provider switch
    { PersonalLocationProvider.Geoapify => "geoapify", PersonalLocationProvider.Mapbox => "mapbox", _ => throw new ArgumentOutOfRangeException(nameof(provider)) };

    /// <summary>Recognizes only the exact trimmed legacy Mapbox identity.</summary>
    public static bool IsLegacyMapbox(string? value) => string.Equals(value?.Trim(), "Mapbox", StringComparison.OrdinalIgnoreCase);
}
