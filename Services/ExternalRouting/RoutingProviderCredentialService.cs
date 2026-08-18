using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Models;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Owns purpose-specific protection and mutation of routing credentials.</summary>
public sealed class RoutingProviderCredentialService
{
    /// <summary>Identifies the isolated Data Protection purpose.</summary>
    public const string ProtectionPurpose = "Wayfarer.ExternalRouting.Credentials.v1";
    private readonly IDataProtector _protector;

    /// <summary>Initializes credential protection with the purpose-specific protector.</summary>
    public RoutingProviderCredentialService(IDataProtector protector) => _protector = protector;

    /// <summary>Preserves a credential for blank edits and replaces non-blank values.</summary>
    public void ApplyEdit(RoutingProviderConfiguration configuration, string? credential)
    {
        if (!string.IsNullOrWhiteSpace(credential)) Replace(configuration, credential);
    }

    /// <summary>Replaces and protects a credential while invalidating verification.</summary>
    public void Replace(RoutingProviderConfiguration configuration, string credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        configuration.CredentialCiphertext = _protector.Protect(credential);
        configuration.CredentialPresent = true;
        configuration.MarkConfigurationChanged();
    }

    /// <summary>Returns the credential or a bounded unusable result when key material is unavailable.</summary>
    public CredentialReadResult Read(RoutingProviderConfiguration configuration)
    {
        if (!configuration.CredentialPresent || string.IsNullOrEmpty(configuration.CredentialCiphertext))
            return new CredentialReadResult(true, null, null);
        try { return new CredentialReadResult(true, _protector.Unprotect(configuration.CredentialCiphertext), null); }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or FormatException)
        { return new CredentialReadResult(false, null, "routing-credential-unavailable"); }
    }
}

/// <summary>Contains only the internal credential read result.</summary>
public sealed record CredentialReadResult(bool Succeeded, string? Credential, string? ErrorCode);
