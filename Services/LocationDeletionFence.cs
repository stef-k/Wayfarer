using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Serializes Location deletion intent, keyed ingestion and effects for one account across processes.</summary>
internal static class LocationDeletionFence
{
    /// <summary>Acquires the account lock before any subordinate row/provider/presence lock or decision snapshot.</summary>
    internal static async Task<IDbContextTransaction?> BeginAsync(ApplicationDbContext db, string userId,
        CancellationToken token)
    {
        if (!db.Database.IsRelational()) return null;
        var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        try
        {
            await AcquireAsync(db, userId, token);
            return transaction;
        }
        catch (Exception failure)
        {
            try { await transaction.DisposeAsync(); }
            catch (Exception cleanup) { failure.Data["DeletionFenceCleanupFailure"] = cleanup.GetType().Name; }
            throw;
        }
    }

    /// <summary>Joins a caller-owned batch transaction; the following command gets a fresh READ COMMITTED snapshot.</summary>
    internal static async Task AcquireAsync(ApplicationDbContext db, string userId, CancellationToken token)
    {
        if (!db.Database.IsNpgsql()) return;
        if (db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Location deletion fencing requires a transaction.");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"location-deletion:" + userId}, 838))", token);
    }

    /// <summary>Only affirmative owner-scoped evidence is deletion; absence alone never is.</summary>
    internal static Task<bool> IsDeletedAsync(ApplicationDbContext db, string userId, Guid? key,
        CancellationToken token) => key.HasValue
        ? db.Set<LocationDeletionMarker>().AsNoTracking().AnyAsync(
            marker => marker.UserId == userId && marker.IdempotencyKey == key.Value, token)
        : Task.FromResult(false);

    /// <summary>Translates a confirmed marker into a bounded domain outcome without retaining request data.</summary>
    internal static async Task ThrowIfDeletedAsync(ApplicationDbContext db, Location location, CancellationToken token)
    {
        if (await IsDeletedAsync(db, location.UserId, location.IdempotencyKey, token))
            throw new LocationDeletedException();
    }

    /// <summary>Atomically marks and removes existing owned rows; null IDs means all Locations, never all markers.</summary>
    internal static async Task<List<int>> DeleteAsync(ApplicationDbContext db, string userId,
        IReadOnlyCollection<int>? ids, IMemoryCache? cache, CancellationToken token)
    {
        await using var transaction = await BeginAsync(db, userId, token);
        var all = ids == null;
        var selected = ids?.Distinct().ToArray() ?? [];
        List<int> deleted;
        if (db.Database.IsNpgsql())
        {
            // Consistent ID order also serializes existing editors; unknown IDs cannot manufacture markers.
            deleted = await db.Database.SqlQuery<int>($$"""
                SELECT "Id" AS "Value" FROM "Locations"
                WHERE "UserId" = {{userId}} AND ({{all}} OR "Id" = ANY({{selected}}))
                ORDER BY "Id" FOR UPDATE
                """).ToListAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO "LocationDeletionMarkers" ("UserId", "IdempotencyKey")
                SELECT DISTINCT "UserId", "IdempotencyKey" FROM "Locations"
                WHERE "UserId" = {{userId}} AND "Id" = ANY({{deleted.ToArray()}}) AND "IdempotencyKey" IS NOT NULL
                ON CONFLICT ("UserId", "IdempotencyKey") DO NOTHING
                """, token);
            await db.Locations.Where(row => row.UserId == userId && deleted.Contains(row.Id)).ExecuteDeleteAsync(token);
            foreach (var entry in db.ChangeTracker.Entries<Location>()
                .Where(entry => entry.Entity.UserId == userId && deleted.Contains(entry.Entity.Id)).ToList())
                entry.State = EntityState.Detached;
        }
        else
        {
            var rows = await db.Locations.Where(row => row.UserId == userId && (all || selected.Contains(row.Id)))
                .OrderBy(row => row.Id).ToListAsync(token);
            deleted = rows.Select(row => row.Id).ToList();
            foreach (var key in rows.Where(row => row.IdempotencyKey.HasValue)
                .Select(row => row.IdempotencyKey!.Value).Distinct())
                if (!await IsDeletedAsync(db, userId, key, token))
                    db.Set<LocationDeletionMarker>().Add(new() { UserId = userId, IdempotencyKey = key });
            db.Locations.RemoveRange(rows);
            await db.SaveChangesAsync(token);
        }
        // All participating publishers hold the same fence, so an earlier effect cannot refill this after deletion.
        cache?.Remove($"lastLocation_{userId}");
        if (transaction != null) await transaction.CommitAsync(token);
        return deleted;
    }
}

/// <summary>Confirmed server deletion, distinct from missing rows, filtered skips and persistence failures.</summary>
internal sealed class LocationDeletedException : Exception;
