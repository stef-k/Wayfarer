using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Services;

/// <summary>Persists API create winners before optional keyed enrichment, retaining legacy keyless ordering.</summary>
internal static class ApiLocationCreation
{
    /// <summary>Only a newly committed keyed row may contact; returns durable state for response/cache/publication.</summary>
    internal static async Task<(Location Location, bool Created)> PersistAsync(ApplicationDbContext db, ReverseGeocodingService reverse,
        Location location, CancellationToken cancellationToken)
    {
        if (!location.IdempotencyKey.HasValue)
        {
            var legacyResult = await reverse.EnrichAsync(location.UserId, location.Coordinates.Y, location.Coordinates.X,
                ReverseGeocodingIntent.LocationCreate, cancellationToken);
            legacyResult.ApplyTo(location, DateTimeOffset.UtcNow);
            db.Locations.Add(location);
            await db.SaveChangesAsync();
            return (location, true);
        }

        // Serialize the final classification and insert. HTTP and provider preparation never hold this transaction.
        await using (var creation = await LocationDeletionFence.BeginAsync(db, location.UserId, cancellationToken))
        {
            await LocationDeletionFence.ThrowIfDeletedAsync(db, location, cancellationToken);
            var winner = await db.Locations.AsNoTracking().FirstOrDefaultAsync(
                row => row.UserId == location.UserId && row.IdempotencyKey == location.IdempotencyKey, cancellationToken);
            if (winner != null) return (winner, false);
            location.OriginalCreateAddressToken = Guid.NewGuid();
            db.Locations.Add(location);
            await db.SaveChangesAsync(cancellationToken);
            if (creation != null) await creation.CommitAsync(cancellationToken);
        }
        var result = await reverse.EnrichRetainedAsync(db, location, cancellationToken);
        return (await ApplyToWinnerAsync(db, location, result, cancellationToken), true);
    }

    /// <summary>Locks only after HTTP, then validates current row and provider authority before applying the tuple.</summary>
    private static async Task<Location> ApplyToWinnerAsync(ApplicationDbContext db, Location inserted,
        ReverseGeocodingResult result, CancellationToken cancellationToken)
    {
        await using var transaction = await LocationDeletionFence.BeginAsync(db, inserted.UserId, cancellationToken);
        await LocationDeletionFence.ThrowIfDeletedAsync(db, inserted, cancellationToken);
        var authority = result.Authority;
        var canApply = result.Succeeded && authority != null && authority.UserId == inserted.UserId
            && authority.Capability == PersonalProviderCapability.Geocoding;
        if (db.Database.IsNpgsql())
        {
            // The outer account fence precedes provider selection/profile and then the Location row lock.
            if (canApply)
            {
                await db.Database.SqlQuery<int>($$"""
                    SELECT 1 AS "Value" FROM "PersonalLocationProviderSelections"
                    WHERE "UserId" = {{inserted.UserId}} FOR SHARE
                    """).ToListAsync(cancellationToken);
                await db.Database.SqlQuery<int>($$"""
                    SELECT 1 AS "Value" FROM "PersonalLocationProviderProfiles"
                    WHERE "UserId" = {{inserted.UserId}} AND "ProviderKey" = {{authority!.ProviderKey}} FOR SHARE
                    """).ToListAsync(cancellationToken);
            }
            await db.Database.SqlQuery<int>($$"""
                SELECT 1 AS "Value" FROM "Locations"
                WHERE "UserId" = {{inserted.UserId}} AND "Id" = {{inserted.Id}} FOR UPDATE
                """).ToListAsync(cancellationToken);
        }

        var current = await db.Locations.AsNoTracking().SingleAsync(
            row => row.UserId == inserted.UserId && row.Id == inserted.Id, cancellationToken);
        var ownsPublication = inserted.OriginalCreateAddressToken != null
            && current.OriginalCreateAddressToken == inserted.OriginalCreateAddressToken;
        if (ownsPublication && canApply && current.Coordinates.EqualsExact(inserted.Coordinates) && HasUntouchedAddress(current)
            && await HasCurrentAuthorityAsync(db, authority!, cancellationToken))
        {
            // Replace only the original tracked snapshot; owner edits on the fresh row remain untouched.
            db.Entry(inserted).State = EntityState.Detached;
            db.Locations.Attach(current);
            result.ApplyTo(current, DateTimeOffset.UtcNow);
            current.OriginalCreateAddressToken = null;
            await db.SaveChangesAsync(cancellationToken);
        }
        if (ownsPublication && current.OriginalCreateAddressToken != null)
        {
            db.Entry(inserted).State = EntityState.Detached;
            if (db.Entry(current).State == EntityState.Detached) db.Attach(current);
            current.OriginalCreateAddressToken = null;
            await db.SaveChangesAsync(cancellationToken);
        }
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return current;
    }

    /// <summary>Any retained address, feature or provenance field supersedes this new row's optional result.</summary>
    private static bool HasUntouchedAddress(Location row) => row.Address == null && row.FullAddress == null
        && row.ProviderAddressLine1 == null && row.AddressNumber == null && row.StreetName == null
        && row.PostCode == null && row.Place == null && row.Region == null && row.Country == null
        && row.ResolvedFeatureName == null && row.ResolvedFeatureType == null
        && row.ReverseGeocodingProvider == null && row.ReverseGeocodingStorageMode == null && row.ReverseGeocodedAt == null;

    /// <summary>Rechecks the contact snapshot's exact selection, profile, verification, credential and consent bindings.</summary>
    private static Task<bool> HasCurrentAuthorityAsync(ApplicationDbContext db, PersonalProviderAuthoritySnapshot authority,
        CancellationToken cancellationToken) => db.PersonalLocationProviderSelections.AnyAsync(selection =>
        selection.UserId == authority.UserId && selection.GeocodingProviderKey == authority.ProviderKey
        && selection.GeocodingSelectionGeneration == authority.SelectionGeneration
        && db.PersonalLocationProviderProfiles.Any(profile => profile.UserId == authority.UserId
            && profile.Id == authority.ProfileId && profile.ProviderKey == authority.ProviderKey
            && profile.RevokedAt == null && profile.GeocodingAuthorized
            && profile.CredentialGeneration == authority.CredentialGeneration
            && profile.GeocodingGeneration == authority.CapabilityGeneration
            && profile.GeocodingVerification == authority.Verification
            && profile.GeocodingVerifiedCredentialGeneration == authority.VerifiedCredentialGeneration
            && profile.GeocodingVerifiedConfigurationGeneration == authority.VerifiedCapabilityGeneration
            && profile.PermanentGeocodingConsentVersion == authority.ConsentVersion
            && profile.PermanentGeocodingConsentedAt == authority.ConsentedAt
            && profile.PermanentGeocodingConsentCredentialGeneration == authority.ConsentCredentialGeneration),
        cancellationToken);
}
