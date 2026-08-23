using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models.LocationProviders;

/// <summary>Stores independent nullable active provider selections without owning credentials.</summary>
public sealed class PersonalLocationProviderSelection
{
    /// <summary>Gets or sets the owning user and primary key.</summary>
    [StringLength(450)] public string UserId { get; set; } = string.Empty;
    /// <summary>Gets or sets the active geocoding provider key; null means no provider.</summary>
    [StringLength(24)] public string? GeocodingProviderKey { get; set; }
    /// <summary>Gets or sets the active routing provider key; null means no provider.</summary>
    [StringLength(24)] public string? RoutingProviderKey { get; set; }
    /// <summary>Gets or sets the geocoding selection generation.</summary>
    public int GeocodingSelectionGeneration { get; set; } = 1;
    /// <summary>Gets or sets the routing selection generation.</summary>
    public int RoutingSelectionGeneration { get; set; } = 1;
    /// <summary>Gets the PostgreSQL concurrency token.</summary>
    public uint RowVersion { get; private set; }

    /// <summary>Creates the no-provider state.</summary>
    public static PersonalLocationProviderSelection Create(string userId) => new() { UserId = userId };

    /// <summary>Changes one selection only and advances its stale-work generation.</summary>
    public void Select(PersonalProviderCapability capability, PersonalLocationProvider? provider)
    {
        var key = provider is null ? null : PersonalProviderKeys.Key(provider.Value);
        if (capability == PersonalProviderCapability.Geocoding && GeocodingProviderKey != key)
        { GeocodingProviderKey = key; GeocodingSelectionGeneration++; }
        else if (capability == PersonalProviderCapability.Routing && RoutingProviderKey != key)
        { RoutingProviderKey = key; RoutingSelectionGeneration++; }
    }
}
