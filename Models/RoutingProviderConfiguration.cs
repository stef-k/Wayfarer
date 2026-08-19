using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models;

/// <summary>Stores one administrator-managed OSRM-compatible routing configuration.</summary>
public sealed class RoutingProviderConfiguration
{
    /// <summary>Gets or sets the stable identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the administrator-facing name.</summary>
    [Required, StringLength(120)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the supported adapter discriminator.</summary>
    public RoutingAdapterType AdapterType { get; set; } = RoutingAdapterType.OsrmCompatible;

    /// <summary>Gets or sets the normalized endpoint without credentials.</summary>
    [StringLength(500)]
    public string? BaseEndpoint { get; set; }

    /// <summary>Gets or sets the protected credential. This value must never leave the server.</summary>
    [StringLength(4096)]
    public string? CredentialCiphertext { get; set; }

    /// <summary>Gets or sets whether a protected credential exists.</summary>
    public bool CredentialPresent { get; set; }

    /// <summary>Gets or sets whether this configured OSRM instance requires its server-side credential.</summary>
    public bool CredentialRequired { get; set; }

    /// <summary>Gets or sets whether administrators permit use of this configuration.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the safe attribution shown to users.</summary>
    [StringLength(500)]
    public string? Attribution { get; set; }

    /// <summary>Gets or sets the coordinate-disclosure text shown before generation.</summary>
    [StringLength(1000)]
    public string? ExternalCoordinateDisclosure { get; set; }

    /// <summary>Gets or sets the administrator-supplied verification origin longitude.</summary>
    public double? VerificationFromLongitude { get; set; }

    /// <summary>Gets or sets the administrator-supplied verification origin latitude.</summary>
    public double? VerificationFromLatitude { get; set; }

    /// <summary>Gets or sets the administrator-supplied verification destination longitude.</summary>
    public double? VerificationToLongitude { get; set; }

    /// <summary>Gets or sets the administrator-supplied verification destination latitude.</summary>
    public double? VerificationToLatitude { get; set; }

    /// <summary>Gets or sets the current configuration version.</summary>
    public int ConfigurationVersion { get; set; } = 1;

    /// <summary>Gets or sets the version proven by the last successful verification.</summary>
    public int? VerifiedConfigurationVersion { get; set; }

    /// <summary>Gets or sets the bounded last verification category.</summary>
    [StringLength(80)]
    public string? VerificationStatus { get; set; }

    /// <summary>Gets or sets the bounded safe last verification result.</summary>
    [StringLength(500)]
    public string? VerificationResult { get; set; }

    /// <summary>Gets or sets total generation timeout seconds.</summary>
    [Range(5, 30)]
    public int GenerationTimeoutSeconds { get; set; } = 15;

    /// <summary>Gets or sets the maximum accepted response bytes.</summary>
    [Range(262144, 2097152)]
    public int ResponseSizeLimitBytes { get; set; } = 1048576;

    /// <summary>Gets or sets the provider request budget per minute.</summary>
    [Range(10, 120)]
    public int RequestsPerMinute { get; set; } = 60;

    /// <summary>Gets or sets the provider concurrency limit.</summary>
    [Range(1, 8)]
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>Gets the PostgreSQL optimistic concurrency token.</summary>
    public uint RowVersion { get; private set; }

    /// <summary>Gets the explicit transport-profile mappings.</summary>
    public ICollection<RoutingProviderProfileMapping> ProfileMappings { get; } = [];

    /// <summary>Invalidates verification after any relevant configuration change.</summary>
    public void MarkConfigurationChanged()
    {
        ConfigurationVersion = checked(ConfigurationVersion + 1);
        VerifiedConfigurationVersion = null;
        VerificationStatus = null;
        VerificationResult = null;
    }
}

/// <summary>Identifies the deliberately bounded routing adapter catalog.</summary>
public enum RoutingAdapterType
{
    /// <summary>The explicit OSRM route API contract.</summary>
    OsrmCompatible = 1
}
