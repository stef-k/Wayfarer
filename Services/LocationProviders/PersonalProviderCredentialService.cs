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
    private readonly IDataProtectionProvider _stableProvider;

    /// <summary>Creates the credential owner.</summary>
    public PersonalProviderCredentialService(IDataProtectionProvider provider, StableDataProtectionProvider stableProvider)
    {
        _provider = provider;
        _stableProvider = stableProvider.Provider;
    }

    /// <summary>Protects a nonblank replacement, advances generation, and preserves authorizations.</summary>
    public void Replace(PersonalLocationProviderProfile profile, string credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        var normalized = credential.Trim();
        if (normalized.Length > 2048 || normalized.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            throw new ArgumentException("The provider credential contains unsupported characters.", nameof(credential));
        var generation = checked(profile.CredentialGeneration + 1);
        string legacy;
        string stable;
        try
        {
            legacy = Protector(profile).Protect(normalized);
            stable = Protector(profile, _stableProvider).Protect(normalized);
        }
        catch (Exception)
        {
            // Never retain a provider exception that could contain credential material.
            throw new InvalidOperationException("The personal credential could not be protected.");
        }
        profile.ProtectedCredential = legacy;
        profile.StableProtectedCredential = stable;
        profile.CredentialGeneration = generation;
        profile.RevokedAt = null;
        profile.ClearPermanentGeocodingConsent();
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
        profile.StableProtectedCredential = null;
        profile.CredentialGeneration = checked(profile.CredentialGeneration + 1);
        profile.RevokedAt = DateTimeOffset.UtcNow;
        profile.ClearPermanentGeocodingConsent();
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

    /// <summary>Reads only the preparation companion; runtime contact must continue to use Read.</summary>
    internal PersonalCredentialRead ReadStable(PersonalLocationProviderProfile profile)
    {
        if (profile.RevokedAt != null || string.IsNullOrEmpty(profile.StableProtectedCredential))
            return PersonalCredentialRead.Unavailable;
        try { return new(true, Protector(profile, _stableProvider).Unprotect(profile.StableProtectedCredential)); }
        catch (CryptographicException) { return PersonalCredentialRead.Unavailable; }
    }

    /// <summary>Creates and verifies a missing companion without changing any runtime authority.</summary>
    internal void PrepareStable(PersonalLocationProviderProfile profile)
    {
        var legacy = Read(profile);
        if (!legacy.Succeeded) throw new InvalidOperationException("Legacy credential is unavailable.");
        if (profile.StableProtectedCredential != null)
        {
            var existing = ReadStable(profile);
            if (!existing.Succeeded || !string.Equals(existing.Credential, legacy.Credential, StringComparison.Ordinal))
                throw new InvalidOperationException("Stable credential is invalid.");
            return;
        }
        try
        {
            var protector = Protector(profile, _stableProvider);
            var stable = protector.Protect(legacy.Credential!);
            if (!string.Equals(protector.Unprotect(stable), legacy.Credential, StringComparison.Ordinal))
                throw new InvalidOperationException();
            profile.StableProtectedCredential = stable;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Stable credential preparation failed.");
        }
    }

    /// <summary>Keeps the credential purpose chain identical for both application identities.</summary>
    private IDataProtector Protector(PersonalLocationProviderProfile profile, IDataProtectionProvider? provider = null) => (provider ?? _provider)
        .CreateProtector(ProtectionPurpose).CreateProtector("credential")
        .CreateProtector(profile.ProviderKey).CreateProtector(profile.UserId);
}
