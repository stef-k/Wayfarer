using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models;

/// <summary>Retains one user's server-default or administrator-approved personal routing selection.</summary>
public sealed class UserRoutingConfiguration
{
    /// <summary>Gets or sets the owning Identity user and primary key.</summary>
    public string UserId { get; set; } = string.Empty;
    /// <summary>Gets or sets the owning user.</summary>
    public ApplicationUser User { get; set; } = null!;
    /// <summary>Gets or sets the selected approved template; null means server default.</summary>
    public Guid? SelectedProviderConfigurationId { get; set; }
    /// <summary>Gets or sets the selected administrator-owned provider.</summary>
    public RoutingProviderConfiguration? SelectedProviderConfiguration { get; set; }
    /// <summary>Gets or sets protected personal credential bytes represented as text.</summary>
    [StringLength(4096)]
    public string? CredentialCiphertext { get; set; }
    /// <summary>Gets or sets whether a protected personal credential is retained.</summary>
    public bool CredentialPresent { get; set; }
    /// <summary>Gets or sets the monotonic selection and credential version.</summary>
    public int ConfigurationVersion { get; set; } = 1;
    /// <summary>Gets or sets the user configuration version proven by personal verification.</summary>
    public int? VerifiedUserConfigurationVersion { get; set; }
    /// <summary>Gets or sets the provider version proven by personal verification.</summary>
    public int? VerifiedProviderConfigurationVersion { get; set; }
    /// <summary>Gets or sets the bounded personal verification status.</summary>
    [StringLength(80)]
    public string? VerificationStatus { get; set; }
    /// <summary>Gets or sets the creation timestamp.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Gets or sets the last mutation timestamp.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Gets the PostgreSQL optimistic concurrency token.</summary>
    public uint RowVersion { get; private set; }

    /// <summary>Creates the retained server-default state for a user.</summary>
    public static UserRoutingConfiguration CreateServerDefault(string userId) => new() { UserId = userId };

    /// <summary>Selects a personal template and clears non-transferable credential state.</summary>
    public void SelectPersonalProvider(Guid providerId)
    {
        if (SelectedProviderConfigurationId == providerId) return;
        SelectedProviderConfigurationId = providerId;
        ClearCredentialAndVerification();
        IncrementVersion();
    }

    /// <summary>Returns explicitly to server-default mode and clears personal state.</summary>
    public void UseServerDefault()
    {
        if (SelectedProviderConfigurationId == null && !CredentialPresent && VerificationStatus == null) return;
        SelectedProviderConfigurationId = null;
        ClearCredentialAndVerification();
        IncrementVersion();
    }

    /// <summary>Invalidates personal verification without changing selection.</summary>
    public void InvalidateVerification()
    {
        VerifiedUserConfigurationVersion = null;
        VerifiedProviderConfigurationVersion = null;
        VerificationStatus = null;
    }

    /// <summary>Removes state prohibited for a credential-free selection and advances authority when needed.</summary>
    public bool NormalizeCredentialFree()
    {
        if (CredentialCiphertext == null && !CredentialPresent && VerifiedUserConfigurationVersion == null
            && VerifiedProviderConfigurationVersion == null && VerificationStatus == null)
            return false;
        ClearCredentialAndVerification();
        IncrementVersion();
        return true;
    }

    /// <summary>Increments the sole user routing generation.</summary>
    public void IncrementVersion()
    {
        ConfigurationVersion = checked(ConfigurationVersion + 1);
        UpdatedAt = DateTime.UtcNow;
    }

    private void ClearCredentialAndVerification()
    {
        CredentialCiphertext = null;
        CredentialPresent = false;
        InvalidateVerification();
    }
}
