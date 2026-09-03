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
        if (dbContext.Entry(selection).State == EntityState.Detached) dbContext.Add(selection);
        if (provider == null)
        {
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
        if (!Eligible(profile, provider.Value, capability)) return ProviderChoiceResult.NotVerified;
        profile!.SetAuthorization(capability, true);
        selection.Select(capability, provider);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return ProviderChoiceResult.Success;
    }

    private static bool Eligible(PersonalLocationProviderProfile? profile, PersonalLocationProvider provider,
        PersonalProviderCapability capability) => !(provider == PersonalLocationProvider.Mapbox
            && capability == PersonalProviderCapability.Routing)
        && profile != null && profile.RevokedAt == null
        && profile.IsAuthorized(capability)
        && (provider != PersonalLocationProvider.Mapbox || capability != PersonalProviderCapability.Geocoding
            || profile.HasCurrentPermanentGeocodingConsent())
        && (capability == PersonalProviderCapability.Geocoding
            ? profile.GeocodingVerification == PersonalProviderVerification.Verified
              && profile.GeocodingVerifiedCredentialGeneration == profile.CredentialGeneration
              && profile.GeocodingVerifiedConfigurationGeneration == profile.GeocodingGeneration
            : profile.RoutingVerification == PersonalProviderVerification.Verified
              && profile.RoutingVerifiedCredentialGeneration == profile.CredentialGeneration
              && profile.RoutingVerifiedConfigurationGeneration == profile.RoutingGeneration);

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
