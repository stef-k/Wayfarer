using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Models.LocationProviders;

namespace Wayfarer.Services.LocationProviders;

/// <summary>Exclusively protects and reads personal provider credentials bound to provider and user.</summary>
public sealed class PersonalProviderCredentialService
{
    /// <summary>Gets the immutable root protection purpose.</summary>
    public const string ProtectionPurpose = "Wayfarer.LocationProviders.PersonalCredentials.v1";
    private readonly IDataProtectionProvider _provider;

    /// <summary>Creates the credential owner.</summary>
    public PersonalProviderCredentialService(IDataProtectionProvider provider) => _provider = provider;

    /// <summary>Protects a nonblank replacement, advances generation, and preserves authorizations.</summary>
    public void Replace(PersonalLocationProviderProfile profile, string credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        profile.ProtectedCredential = Protector(profile).Protect(credential.Trim());
        profile.CredentialGeneration = checked(profile.CredentialGeneration + 1);
        profile.RevokedAt = null;
        profile.ClearVerification(PersonalProviderCapability.Geocoding);
        profile.ClearVerification(PersonalProviderCapability.Routing);
        profile.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Reads a credential as a bounded unavailable result without mutating ciphertext.</summary>
    public PersonalCredentialRead Read(PersonalLocationProviderProfile profile)
    {
        if (profile.RevokedAt != null || string.IsNullOrEmpty(profile.ProtectedCredential))
            return PersonalCredentialRead.Unavailable;
        try { return new(true, Protector(profile).Unprotect(profile.ProtectedCredential)); }
        catch (CryptographicException) { return PersonalCredentialRead.Unavailable; }
    }

    /// <summary>Explicitly revokes contact authority while preserving profile and usage history.</summary>
    public void Revoke(PersonalLocationProviderProfile profile)
    {
        profile.ProtectedCredential = null;
        profile.CredentialGeneration = checked(profile.CredentialGeneration + 1);
        profile.RevokedAt = DateTimeOffset.UtcNow;
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, false);
        profile.SetAuthorization(PersonalProviderCapability.Routing, false);
        profile.ClearVerification(PersonalProviderCapability.Geocoding);
        profile.ClearVerification(PersonalProviderCapability.Routing);
        profile.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Records only bounded verification and binds it to current credential/capability generations.</summary>
    public void RecordVerification(PersonalLocationProviderProfile profile, PersonalProviderCapability capability,
        PersonalProviderVerification verification)
    {
        if (verification is < PersonalProviderVerification.Unverified or > PersonalProviderVerification.Unavailable)
            throw new ArgumentOutOfRangeException(nameof(verification));
        if (capability == PersonalProviderCapability.Geocoding)
        {
            profile.GeocodingVerification = verification;
            profile.GeocodingVerifiedCredentialGeneration = verification == PersonalProviderVerification.Verified ? profile.CredentialGeneration : null;
            profile.GeocodingVerifiedConfigurationGeneration = verification == PersonalProviderVerification.Verified ? profile.GeocodingGeneration : null;
        }
        else
        {
            profile.RoutingVerification = verification;
            profile.RoutingVerifiedCredentialGeneration = verification == PersonalProviderVerification.Verified ? profile.CredentialGeneration : null;
            profile.RoutingVerifiedConfigurationGeneration = verification == PersonalProviderVerification.Verified ? profile.RoutingGeneration : null;
        }
        profile.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private IDataProtector Protector(PersonalLocationProviderProfile profile) => _provider
        .CreateProtector(ProtectionPurpose).CreateProtector("credential")
        .CreateProtector(profile.ProviderKey).CreateProtector(profile.UserId);
}
