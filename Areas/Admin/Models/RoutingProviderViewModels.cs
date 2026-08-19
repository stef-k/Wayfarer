using System.ComponentModel.DataAnnotations;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Models;

namespace Wayfarer.Areas.Admin.Models;

/// <summary>Contains the focused provider list and global feature state.</summary>
public sealed record RoutingProviderIndexViewModel(
    bool ExternalRouteGenerationEnabled, uint SettingsRowVersion, Guid? ActiveProviderId,
    IReadOnlyList<RoutingProviderRowViewModel> Providers);

/// <summary>Contains one safe provider list row without endpoint or credential material.</summary>
public sealed record RoutingProviderRowViewModel(
    Guid Id, string DisplayName, RoutingProviderState State, bool Enabled, bool CredentialPresent,
    int ConfigurationVersion, int? VerifiedConfigurationVersion, uint RowVersion);

/// <summary>Contains allowlisted OSRM configuration and mapping edit fields.</summary>
public sealed class RoutingProviderEditViewModel : IValidatableObject
{
    /// <summary>Gets or sets the provider identity for edits.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the administrator-facing name.</summary>
    [Required, StringLength(120)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the OSRM base endpoint.</summary>
    [Required, StringLength(500)]
    public string BaseEndpoint { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional replacement credential; blank preserves the current value.</summary>
    [StringLength(2000)]
    public string? Credential { get; set; }

    /// <summary>Gets or sets whether a credential is required for this instance.</summary>
    public bool CredentialRequired { get; set; }

    /// <summary>Gets or sets the administrator-owned personal template access mode.</summary>
    public PersonalRoutingAccess PersonalRoutingAccess { get; set; }

    /// <summary>Gets whether a stored credential is present.</summary>
    public bool CredentialPresent { get; set; }

    /// <summary>Gets or sets whether this configuration is eligible for verification.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets safe provider attribution.</summary>
    [StringLength(500)]
    public string? Attribution { get; set; }

    /// <summary>Gets or sets coordinate disclosure shown before generation.</summary>
    [Required, StringLength(1000)]
    public string ExternalCoordinateDisclosure { get; set; } = string.Empty;

    /// <summary>Gets or sets administrator verification coordinates.</summary>
    public double? VerificationFromLongitude { get; set; }
    /// <inheritdoc cref="VerificationFromLongitude" />
    public double? VerificationFromLatitude { get; set; }
    /// <inheritdoc cref="VerificationFromLongitude" />
    public double? VerificationToLongitude { get; set; }
    /// <inheritdoc cref="VerificationFromLongitude" />
    public double? VerificationToLatitude { get; set; }

    /// <summary>Gets or sets the bounded generation timeout.</summary>
    [Range(5, 30)]
    public int GenerationTimeoutSeconds { get; set; } = 15;

    /// <summary>Gets or sets bounded response bytes.</summary>
    [Range(262144, 2097152)]
    public int ResponseSizeLimitBytes { get; set; } = 1048576;

    /// <summary>Gets or sets provider requests per minute.</summary>
    [Range(10, 120)]
    public int RequestsPerMinute { get; set; } = 60;

    /// <summary>Gets or sets strict invariant decimal seconds for provider pacing.</summary>
    public string MinimumIntervalSeconds { get; set; } = "1.0";

    /// <summary>Gets or sets provider concurrency.</summary>
    [Range(1, 8)]
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>Gets or sets explicit transport-profile mappings.</summary>
    public List<RoutingProviderMappingViewModel> Mappings { get; set; } = [];

    /// <summary>Gets or sets the expected provider concurrency token.</summary>
    public uint RowVersion { get; set; }

    /// <summary>Gets or sets the expected singleton settings concurrency token for credential clearing.</summary>
    public uint SettingsRowVersion { get; set; }

    /// <summary>Gets the current configuration version.</summary>
    public int ConfigurationVersion { get; set; } = 1;

    /// <summary>Validates coordinates and credential-required completeness.</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (CredentialRequired && !CredentialPresent && string.IsNullOrWhiteSpace(Credential))
            yield return new ValidationResult("A credential is required for this configuration.", [nameof(Credential)]);
        foreach (var (value, name, minimum, maximum) in new[]
        {
            (VerificationFromLongitude, nameof(VerificationFromLongitude), -180d, 180d),
            (VerificationFromLatitude, nameof(VerificationFromLatitude), -90d, 90d),
            (VerificationToLongitude, nameof(VerificationToLongitude), -180d, 180d),
            (VerificationToLatitude, nameof(VerificationToLatitude), -90d, 90d)
        })
            if (value is not double coordinate || !double.IsFinite(coordinate) || coordinate < minimum || coordinate > maximum)
                yield return new ValidationResult("A finite in-range verification coordinate is required.", [name]);
        if (Mappings.Count(item => !string.IsNullOrWhiteSpace(item.OsrmProfile)) is 0 or > 8)
            yield return new ValidationResult("Map between one and eight transport profiles.", [nameof(Mappings)]);
    }
}

/// <summary>Contains one exact transport-profile to OSRM-profile mapping.</summary>
public sealed class RoutingProviderMappingViewModel
{
    /// <summary>Gets or sets the Wayfarer transport-profile identity.</summary>
    public Guid TransportProfileId { get; set; }

    /// <summary>Gets the transport-profile label.</summary>
    public string TransportProfileLabel { get; set; } = string.Empty;

    /// <summary>Gets or sets the exact OSRM profile path value.</summary>
    [StringLength(80), RegularExpression("^[A-Za-z0-9_-]*$", ErrorMessage = "Use only letters, numbers, underscore, or hyphen.")]
    public string? OsrmProfile { get; set; }
}
