using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Models;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Protects personal routing credentials with immutable user and provider purposes.</summary>
public sealed class UserRoutingCredentialService
{
    /// <summary>Identifies the dedicated personal routing protection root.</summary>
    public const string ProtectionPurpose = "Wayfarer.ExternalRouting.UserCredentials.v1";
    private readonly IDataProtectionProvider _provider;

    /// <summary>Initializes the personal credential protector factory.</summary>
    public UserRoutingCredentialService(IDataProtectionProvider provider) => _provider = provider;

    /// <summary>Protects a routing credential for exactly one user and provider.</summary>
    public string Protect(string userId, Guid providerId, string credential) =>
        Protector(userId, providerId).Protect(credential);

    /// <summary>Reads a credential or returns a bounded unavailable result without mutating ciphertext.</summary>
    public UserRoutingCredentialReadResult Unprotect(string userId, Guid providerId, string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return UserRoutingCredentialReadResult.Unavailable;
        try { return new(true, Protector(userId, providerId).Unprotect(ciphertext)); }
        catch (CryptographicException) { return UserRoutingCredentialReadResult.Unavailable; }
    }

    /// <summary>Applies a nonblank replacement after protection succeeds.</summary>
    public bool Replace(UserRoutingConfiguration configuration, Guid providerId, string? credential)
    {
        if (string.IsNullOrWhiteSpace(credential)) return false;
        var ciphertext = Protect(configuration.UserId, providerId, credential.Trim());
        configuration.CredentialCiphertext = ciphertext;
        configuration.CredentialPresent = true;
        configuration.InvalidateVerification();
        configuration.IncrementVersion();
        return true;
    }

    /// <summary>Clears a credential only after explicit confirmation.</summary>
    public bool Clear(UserRoutingConfiguration configuration, bool confirmed)
    {
        if (!confirmed || !configuration.CredentialPresent) return false;
        configuration.CredentialCiphertext = null;
        configuration.CredentialPresent = false;
        configuration.InvalidateVerification();
        configuration.IncrementVersion();
        return true;
    }

    private IDataProtector Protector(string userId, Guid providerId) => _provider
        .CreateProtector(ProtectionPurpose).CreateProtector("routing-api-credential")
        .CreateProtector(userId).CreateProtector(providerId.ToString("D"));
}

/// <summary>Contains only the bounded server-internal personal credential read result.</summary>
public sealed record UserRoutingCredentialReadResult(bool Succeeded, string? Credential)
{
    /// <summary>Gets the bounded unavailable result.</summary>
    public static UserRoutingCredentialReadResult Unavailable { get; } = new(false, null);
}
