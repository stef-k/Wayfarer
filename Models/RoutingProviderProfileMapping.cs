using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models;

/// <summary>Maps one Wayfarer transport profile to an exact OSRM profile.</summary>
public sealed class RoutingProviderProfileMapping
{
    /// <summary>Gets or sets the owning provider configuration.</summary>
    public Guid RoutingProviderConfigurationId { get; set; }

    /// <summary>Gets or sets the mapped Wayfarer transport profile.</summary>
    public Guid TransportProfileId { get; set; }

    /// <summary>Gets or sets the exact OSRM route profile path value.</summary>
    [Required, StringLength(80)]
    public string OsrmProfile { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider configuration navigation.</summary>
    public RoutingProviderConfiguration RoutingProviderConfiguration { get; set; } = null!;

    /// <summary>Gets or sets the Wayfarer transport profile navigation.</summary>
    public TransportProfile TransportProfile { get; set; } = null!;
}
