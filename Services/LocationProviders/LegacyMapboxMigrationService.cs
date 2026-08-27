using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;

namespace Wayfarer.Services.LocationProviders;

/// <summary>Migrates only the authenticated user's exact legacy Mapbox rows without risking the last readable copy.</summary>
public sealed class LegacyMapboxMigrationService(
    ApplicationDbContext dbContext, PersonalProviderCredentialService credentials)
{
    /// <summary>Converges the current user's legacy state under one transaction and bounded locks.</summary>
    public async Task<LegacyMapboxMigrationResult> MigrateAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;

        var selection = await LockSelectionAsync(userId, cancellationToken);
        var profile = await LockProfileAsync(userId, cancellationToken);
        var legacyRows = await LockLegacyRowsAsync(userId, cancellationToken);
        var values = legacyRows.Select(item => item.Token!.Trim()).Distinct(StringComparer.Ordinal).ToArray();

        if (values.Length == 0)
        {
            if (profile?.RevokedAt != null) profile.LegacyMigrationState = LegacyMapboxMigrationState.Revoked;
            else if (!string.IsNullOrEmpty(profile?.ProtectedCredential) && !credentials.Read(profile).Succeeded)
                profile.LegacyMigrationState = LegacyMapboxMigrationState.ProtectedCredentialUnavailable;
            else if (profile?.LegacyMigrationState == LegacyMapboxMigrationState.Migrated
                     && !profile.HasCurrentPermanentGeocodingConsent()
                     && profile.GeocodingVerification == PersonalProviderVerification.Unverified)
                await ClearEarlierMigrationSelectionAsync(selection);
            if (profile != null) await dbContext.SaveChangesAsync(cancellationToken);
            return await CompleteAsync(new(profile?.LegacyMigrationState ?? LegacyMapboxMigrationState.None, 0,
                profile != null && credentials.Read(profile).Succeeded), transaction, cancellationToken);
        }
        if (profile?.RevokedAt != null)
        {
            profile.LegacyMigrationState = LegacyMapboxMigrationState.Revoked;
            await dbContext.SaveChangesAsync(cancellationToken);
            return await CompleteAsync(new(profile.LegacyMigrationState, 0, false), transaction, cancellationToken);
        }
        if (values.Length > 1)
            return await PreserveConflictAsync(profile, userId, transaction, cancellationToken);

        profile ??= PersonalLocationProviderProfile.Create(userId, PersonalLocationProvider.Mapbox);
        if (dbContext.Entry(profile).State == EntityState.Detached)
            dbContext.Set<PersonalLocationProviderProfile>().Add(profile);

        if (!string.IsNullOrEmpty(profile.ProtectedCredential))
        {
            var protectedRead = credentials.Read(profile);
            if (!protectedRead.Succeeded)
            {
                profile.LegacyMigrationState = LegacyMapboxMigrationState.ProtectedCredentialUnavailable;
                await dbContext.SaveChangesAsync(cancellationToken);
                return await CompleteAsync(new(profile.LegacyMigrationState, 0, false), transaction, cancellationToken);
            }
            if (!string.Equals(protectedRead.Credential, values[0], StringComparison.Ordinal))
                return await PreserveConflictAsync(profile, userId, transaction, cancellationToken);
            await EnsureGeocodingAuthorityAsync(profile, selection);
        }
        else
        {
            credentials.Replace(profile, values[0]);
            profile.SetAuthorization(PersonalProviderCapability.Routing, false);
            await EnsureGeocodingAuthorityAsync(profile, selection);
            await dbContext.SaveChangesAsync(cancellationToken);
            var protectedRead = credentials.Read(profile);
            if (!protectedRead.Succeeded || !string.Equals(protectedRead.Credential, values[0], StringComparison.Ordinal))
            {
                profile.LegacyMigrationState = LegacyMapboxMigrationState.ProtectedCredentialUnavailable;
                await dbContext.SaveChangesAsync(cancellationToken);
                return await CompleteAsync(new(profile.LegacyMigrationState, 0, false), transaction, cancellationToken);
            }
        }

        profile.LegacyMigrationState = LegacyMapboxMigrationState.Migrated;
        dbContext.ApiTokens.RemoveRange(legacyRows.Where(item => string.Equals(item.Token?.Trim(), values[0], StringComparison.Ordinal)));
        var retired = legacyRows.Count;
        await dbContext.SaveChangesAsync(cancellationToken);
        return await CompleteAsync(new(profile.LegacyMigrationState, retired, true), transaction, cancellationToken);
    }

    private static Task ClearEarlierMigrationSelectionAsync(PersonalLocationProviderSelection? selection)
    {
        if (selection?.GeocodingProviderKey == "mapbox")
            selection.Select(PersonalProviderCapability.Geocoding, null);
        return Task.CompletedTask;
    }

    private Task<PersonalLocationProviderSelection?> LockSelectionAsync(
        string userId, CancellationToken cancellationToken) =>
        dbContext.Database.IsNpgsql()
            ? dbContext.Set<PersonalLocationProviderSelection>().FromSqlInterpolated($$"""
                SELECT *, xmin FROM "PersonalLocationProviderSelections"
                WHERE "UserId" = {{userId}} FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken)
            : dbContext.Set<PersonalLocationProviderSelection>().SingleOrDefaultAsync(
                item => item.UserId == userId, cancellationToken);

    private Task<PersonalLocationProviderProfile?> LockProfileAsync(string userId, CancellationToken cancellationToken) =>
        dbContext.Database.IsNpgsql()
            ? dbContext.Set<PersonalLocationProviderProfile>().FromSqlInterpolated($$"""
                SELECT *, xmin FROM "PersonalLocationProviderProfiles"
                WHERE "UserId" = {{userId}} AND "ProviderKey" = 'mapbox' FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken)
            : dbContext.Set<PersonalLocationProviderProfile>().SingleOrDefaultAsync(
                item => item.UserId == userId && item.ProviderKey == "mapbox", cancellationToken);

    private static Task EnsureGeocodingAuthorityAsync(
        PersonalLocationProviderProfile profile, PersonalLocationProviderSelection? selection)
    {
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
        profile.ClearPermanentGeocodingConsent();
        if (selection?.GeocodingProviderKey == "mapbox")
            selection.Select(PersonalProviderCapability.Geocoding, null);
        return Task.CompletedTask;
    }

    private async Task<List<ApiToken>> LockLegacyRowsAsync(string userId, CancellationToken cancellationToken)
    {
        var rows = dbContext.Database.IsNpgsql()
            ? await dbContext.ApiTokens.FromSqlInterpolated($$"""
                SELECT * FROM "ApiTokens" WHERE "UserId" = {{userId}}
                AND lower(btrim("Name")) = 'mapbox' AND btrim(COALESCE("Token", '')) <> '' FOR UPDATE
                """).IgnoreQueryFilters().ToListAsync(cancellationToken)
            : await dbContext.ApiTokens.IgnoreQueryFilters().Where(item => item.UserId == userId && item.Token != null)
                .ToListAsync(cancellationToken);
        return rows.Where(item => PersonalProviderKeys.IsLegacyMapbox(item.Name)
                                  && !string.IsNullOrWhiteSpace(item.Token)).ToList();
    }

    private async Task<LegacyMapboxMigrationResult> PreserveConflictAsync(
        PersonalLocationProviderProfile? profile, string userId,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        profile ??= PersonalLocationProviderProfile.Create(userId, PersonalLocationProvider.Mapbox);
        if (dbContext.Entry(profile).State == EntityState.Detached) dbContext.Add(profile);
        profile.LegacyMigrationState = LegacyMapboxMigrationState.Conflict;
        await dbContext.SaveChangesAsync(cancellationToken);
        return await CompleteAsync(new(profile.LegacyMigrationState, 0, false), transaction, cancellationToken);
    }

    private static async Task<LegacyMapboxMigrationResult> CompleteAsync(
        LegacyMapboxMigrationResult result, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return result;
    }
}

/// <summary>Reports only bounded migration state and counts.</summary>
public sealed record LegacyMapboxMigrationResult(
    LegacyMapboxMigrationState State, int RetiredLegacyRows, bool ProtectedCredentialReady);
