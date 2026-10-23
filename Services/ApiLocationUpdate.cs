using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Wayfarer.Models;
using Wayfarer.Parsers;

namespace Wayfarer.Services;

/// <summary>Persists owner coordinate refreshes only while their original row and edit authority remain current.</summary>
internal static class ApiLocationUpdate
{
    /// <summary>Keeps pending edits out of provider saves, admits under the account fence and revalidates after unlocked HTTP.</summary>
    internal static async Task<Location> PersistAsync(ApplicationDbContext db, ReverseGeocodingService reverse,
        Location edited, CancellationToken token)
    {
        db.ChangeTracker.DetectChanges();
        var entry = db.Entry(edited);
        var original = entry.OriginalValues.Clone();
        var values = entry.CurrentValues.Clone();
        var modified = entry.Properties.Where(property => property.IsModified).Select(property => property.Metadata.Name).ToList();
        entry.State = EntityState.Detached;

        var result = await reverse.EnrichRetainedAsync(db, edited, token, original);
        await using var transaction = await LocationDeletionFence.BeginAsync(db, edited.UserId, token);
        if (result.Succeeded && result.Authority != null && db.Database.IsNpgsql())
        {
            // Account first, provider selection/profile second, Location last; no lock survives the HTTP gap.
            await db.Database.SqlQuery<int>($$"""
                SELECT 1 AS "Value" FROM "PersonalLocationProviderSelections"
                WHERE "UserId" = {{edited.UserId}} FOR SHARE
                """).ToListAsync(token);
            await db.Database.SqlQuery<int>($$"""
                SELECT 1 AS "Value" FROM "PersonalLocationProviderProfiles"
                WHERE "UserId" = {{edited.UserId}} AND "ProviderKey" = {{result.Authority.ProviderKey}} FOR SHARE
                """).ToListAsync(token);
        }
        var current = await RequireCurrentAsync(db, edited, original, token);
        db.Locations.Attach(current);
        foreach (var name in modified)
        {
            db.Entry(current).Property(name).CurrentValue = values[name];
            // Preserve forced #855 invalidation even when the editor originally read a null admission.
            db.Entry(current).Property(name).IsModified = true;
        }
        current.ReverseGeocodingProvider = null;
        current.ReverseGeocodingStorageMode = null;
        current.ReverseGeocodedAt = null;
        current.ProviderAddressLine1 = null;
        current.ResolvedFeatureName = null;
        current.ResolvedFeatureType = null;
        if (result.Succeeded && result.Authority != null && await reverse.IsAuthorityCurrentAsync(result.Authority, token))
            result.ApplyTo(current, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(token);
        if (transaction != null) await transaction.CommitAsync(token);
        return current;
    }

    /// <summary>Locks and checks the original owner/key and editor snapshot before admission or final persistence.</summary>
    internal static async Task<Location> RequireCurrentAsync(ApplicationDbContext db, Location edited,
        PropertyValues original, CancellationToken token)
    {
        if (await LocationDeletionFence.IsDeletedAsync(db, edited.UserId, (Guid?)original[nameof(Location.IdempotencyKey)], token))
            throw new LocationUpdateMissingException();
        if (db.Database.IsNpgsql())
            await db.Database.SqlQuery<int>($$"""
                SELECT 1 AS "Value" FROM "Locations"
                WHERE "UserId" = {{edited.UserId}} AND "Id" = {{edited.Id}} FOR UPDATE
                """).ToListAsync(token);
        var current = await db.Locations.AsNoTracking().SingleOrDefaultAsync(
            row => row.UserId == edited.UserId && row.Id == edited.Id, token);
        if (current == null) throw new LocationUpdateMissingException();
        var currentValues = db.Entry(current).CurrentValues;
        foreach (var property in original.Properties)
        {
            if (Util.LocationHistoryDerivation.IsDerived(property.Name)) continue;
            // A concurrent presence admission is not an owner edit; retain it or explicitly invalidate it with the patch.
            if (property.Name is nameof(Location.CoordinateOrigin) or nameof(Location.ObservedAtUtc)
                or nameof(Location.ObservedAgeAtEventMs) or nameof(Location.PresenceObservedAtUtc)) continue;
            if (!property.GetValueComparer()!.Equals(original[property], currentValues[property]))
                throw new LocationUpdateConflictException();
        }
        return current;
    }
}

/// <summary>An owner refresh lost its retained row or original deletion decision; PUT keeps its ordinary 404.</summary>
internal sealed class LocationUpdateMissingException : Exception;

/// <summary>A newer owner edit superseded the refresh's original snapshot; no part of the stale patch may persist.</summary>
internal sealed class LocationUpdateConflictException : Exception;
