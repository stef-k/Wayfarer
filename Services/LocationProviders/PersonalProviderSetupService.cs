using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;

namespace Wayfarer.Services.LocationProviders;

/// <summary>Owns atomic capability setup transitions without provider contact.</summary>
public sealed class PersonalProviderSetupService(
    ApplicationDbContext dbContext, PersonalProviderCredentialService credentials)
{
    /// <summary>Replaces one credential and disables both capabilities without contacting a provider.</summary>
    public async Task ReplaceCredentialAsync(string userId, PersonalLocationProvider provider, string credential,
        CancellationToken cancellationToken)
    {
        var key = PersonalProviderKeys.Key(provider);
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        var selection = await SelectionAsync(userId, cancellationToken)
            ?? PersonalLocationProviderSelection.Create(userId);
        if (dbContext.Entry(selection).State == EntityState.Detached) dbContext.Add(selection);
        var profile = await ProfileAsync(userId, key, cancellationToken)
            ?? PersonalLocationProviderProfile.Create(userId, provider);
        if (dbContext.Entry(profile).State == EntityState.Detached) dbContext.Add(profile);
        credentials.Replace(profile, credential);
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, false);
        profile.SetAuthorization(PersonalProviderCapability.Routing, false);
        if (selection.GeocodingProviderKey == key) selection.Select(PersonalProviderCapability.Geocoding, null);
        if (selection.RoutingProviderKey == key) selection.Select(PersonalProviderCapability.Routing, null);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Records explicit setup consent for one capability after local prerequisites validate.</summary>
    public async Task<bool> AuthorizeVerificationAsync(string userId, PersonalLocationProvider provider,
        PersonalProviderCapability capability, CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        var profile = await ProfileAsync(userId, PersonalProviderKeys.Key(provider), cancellationToken);
        if (profile == null || !credentials.Read(profile).Succeeded
            || provider == PersonalLocationProvider.Mapbox && capability == PersonalProviderCapability.Geocoding
            && !profile.HasCurrentPermanentGeocodingConsent()) return false;
        profile.SetAuthorization(capability, true);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>Selects one current verified provider or atomically disables only the requested capability.</summary>
    public async Task<ProviderChoiceResult> ChooseAsync(string userId, PersonalProviderCapability capability,
        PersonalLocationProvider? provider, CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        var selection = await SelectionAsync(userId, cancellationToken)
            ?? PersonalLocationProviderSelection.Create(userId);
        if (provider == null)
        {
            if (dbContext.Entry(selection).State == EntityState.Detached) dbContext.Add(selection);
            var currentKey = capability == PersonalProviderCapability.Geocoding
                ? selection.GeocodingProviderKey : selection.RoutingProviderKey;
            if (currentKey != null)
            {
                var current = await ProfileAsync(userId, currentKey, cancellationToken);
                current?.SetAuthorization(capability, false);
            }
            selection.Select(capability, null);
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
            return ProviderChoiceResult.Success;
        }
        var profile = await ProfileAsync(userId, PersonalProviderKeys.Key(provider.Value), cancellationToken);
        var readable = profile != null && credentials.Read(profile).Succeeded;
        if (!PersonalProviderEligibility.Evaluate(profile, provider.Value, capability, readable).Eligible)
            return ProviderChoiceResult.NotVerified;
        if (dbContext.Entry(selection).State == EntityState.Detached) dbContext.Add(selection);
        profile!.SetAuthorization(capability, true);
        selection.Select(capability, provider);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return ProviderChoiceResult.Success;
    }

    private Task<PersonalLocationProviderProfile?> ProfileAsync(string userId, string providerKey,
        CancellationToken cancellationToken) => dbContext.Database.IsNpgsql()
        ? dbContext.Set<PersonalLocationProviderProfile>().FromSqlInterpolated($$"""
            SELECT *, xmin FROM "PersonalLocationProviderProfiles"
            WHERE "UserId" = {{userId}} AND "ProviderKey" = {{providerKey}} FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken)
        : dbContext.Set<PersonalLocationProviderProfile>()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.ProviderKey == providerKey, cancellationToken);

    private Task<PersonalLocationProviderSelection?> SelectionAsync(string userId,
        CancellationToken cancellationToken) => dbContext.Database.IsNpgsql()
        ? dbContext.Set<PersonalLocationProviderSelection>().FromSqlInterpolated($$"""
            SELECT *, xmin FROM "PersonalLocationProviderSelections"
            WHERE "UserId" = {{userId}} FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken)
        : dbContext.Set<PersonalLocationProviderSelection>()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
}

/// <summary>Identifies a bounded provider-choice transition outcome.</summary>
public enum ProviderChoiceResult { Success, NotVerified }

/// <summary>Projects executable personal-provider authority for setup and presentation.</summary>
public static class PersonalProviderEligibility
{
    private const string ReplaceCredential = "Blocked. Replace the credential and verify again.";

    /// <summary>Evaluates one capability without contacting a provider or changing stored state.</summary>
    public static PersonalProviderEligibilityResult Evaluate(PersonalLocationProviderProfile? profile,
        PersonalLocationProvider provider, PersonalProviderCapability capability, bool credentialReadable)
    {
        if (profile == null || profile.RevokedAt != null || !credentialReadable)
            return new(false, ReplaceCredential);
        if (provider == PersonalLocationProvider.Mapbox && capability == PersonalProviderCapability.Routing)
            return new(false, "Blocked. Mapbox Directions is not available.");
        if (!profile.IsAuthorized(capability))
            return new(false, "Blocked. Enable and verify this capability again.");
        if (provider == PersonalLocationProvider.Mapbox && capability == PersonalProviderCapability.Geocoding
            && !profile.HasCurrentPermanentGeocodingConsent())
            return new(false, "Blocked. Renew Permanent Geocoding consent and verify again.");
        var verified = capability == PersonalProviderCapability.Geocoding
            ? profile.GeocodingVerification == PersonalProviderVerification.Verified
              && profile.GeocodingVerifiedCredentialGeneration == profile.CredentialGeneration
              && profile.GeocodingVerifiedConfigurationGeneration == profile.GeocodingGeneration
            : profile.RoutingVerification == PersonalProviderVerification.Verified
              && profile.RoutingVerifiedCredentialGeneration == profile.CredentialGeneration
              && profile.RoutingVerifiedConfigurationGeneration == profile.RoutingGeneration;
        return verified ? new(true, "Ready.") : new(false, "Blocked. Verify this capability again.");
    }
}

/// <summary>Contains eligibility and a bounded actionable presentation status.</summary>
public sealed record PersonalProviderEligibilityResult(bool Eligible, string Status);
