using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Models.LocationProviders;

namespace Wayfarer.Services.LocationProviders;

/// <summary>Source-only offline codec; never registered for runtime provider contact.</summary>
public sealed class LegacyCredentialPreparationCodec(IDataProtectionProvider legacyProvider, StableDataProtectionProvider stableProvider)
{
    /// <summary>Reads the original source identity without guessing another content root.</summary>
    internal PersonalCredentialRead ReadLegacy(PersonalLocationProviderProfile profile)
    {
        if (profile.RevokedAt != null || string.IsNullOrEmpty(profile.ProtectedCredential))
            return PersonalCredentialRead.Unavailable;
        try { return new(true, PersonalProviderCredentialService.Protector(profile, legacyProvider).Unprotect(profile.ProtectedCredential)); }
        catch (CryptographicException) { return PersonalCredentialRead.Unavailable; }
    }

    /// <summary>Reads only the preparation companion; runtime contact must continue to use Read.</summary>
    internal PersonalCredentialRead ReadStable(PersonalLocationProviderProfile profile)
    {
        if (profile.RevokedAt != null || string.IsNullOrEmpty(profile.StableProtectedCredential))
            return PersonalCredentialRead.Unavailable;
        try { return new(true, PersonalProviderCredentialService.Protector(profile, stableProvider.Provider).Unprotect(profile.StableProtectedCredential)); }
        catch (CryptographicException) { return PersonalCredentialRead.Unavailable; }
    }

    /// <summary>Creates and verifies a missing companion without changing any runtime authority.</summary>
    internal void PrepareStable(PersonalLocationProviderProfile profile)
    {
        var legacy = ReadLegacy(profile);
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
            var protector = PersonalProviderCredentialService.Protector(profile, stableProvider.Provider);
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

}

/// <summary>Secondary stable provider used only by explicit source preparation.</summary>
public sealed record StableDataProtectionProvider(IDataProtectionProvider Provider);
