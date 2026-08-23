using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models;

/// <summary>Maps one stable Wayfarer transport profile to one provider-native routing mode.</summary>
public sealed class RoutingProviderProfileMapping
{
    /// <summary>Gets or sets the owning provider configuration.</summary>
    public Guid RoutingProviderConfigurationId { get; set; }

    /// <summary>Gets or sets the mapped Wayfarer transport profile.</summary>
    public Guid TransportProfileId { get; set; }

    /// <summary>Gets or sets the exact provider-native mode validated for the owning adapter.</summary>
    [Required, StringLength(80)]
    public string OsrmProfile { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider-neutral name for the existing provider-scoped storage column.</summary>
    public string ProviderNativeMode
    {
        get => OsrmProfile;
        set => OsrmProfile = value;
    }

    /// <summary>Changes the provider-native mode without consulting the display profile name.</summary>
    public void SetNativeMode(string nativeMode) => OsrmProfile = nativeMode;

    /// <summary>Gets or sets the provider configuration navigation.</summary>
    public RoutingProviderConfiguration RoutingProviderConfiguration { get; set; } = null!;

    /// <summary>Gets or sets the Wayfarer transport profile navigation.</summary>
    public TransportProfile TransportProfile { get; set; } = null!;
}
