using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Reconciles only newly inserted import rows inside their existing PostgreSQL transaction.</summary>
public sealed class ActivityCatalogReconciler(ApplicationDbContext db)
{
    /// <summary>Returns accepted invalid classifications; every valid supplied name acquires its destination FK.</summary>
    public async Task<int> ReconcileAsync(IReadOnlyList<Location> locations, CancellationToken token)
    {
        var invalid = locations.Count(l => l.ImportedActivityInvalid || !ActivityCatalog.CanTransport(l.ImportedActivityName));
        var supplied = locations.Where(l => !l.ImportedActivityInvalid && ActivityCatalog.CanTransport(l.ImportedActivityName)
            && !string.IsNullOrEmpty(l.ImportedActivityName)).ToArray();
        if (supplied.Length == 0) return invalid;
        if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Activity import reconciliation requires a PostgreSQL transaction.");

        var settings = await db.Database.SqlQuery<CatalogTimeouts>($"""
            SELECT current_setting('statement_timeout') AS "StatementTimeout",
                   current_setting('lock_timeout') AS "LockTimeout"
            """).SingleAsync(token);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL statement_timeout = '5s'; SET LOCAL lock_timeout = '2s'", token);
        var names = supplied.Select(l => l.ImportedActivityName!).ToArray();
        // PostgreSQL supplies identity, validation and C ordering; ordinal identifies the accepted file row.
        var normalized = await db.Database.SqlQuery<NormalizedImportActivity>($"""
            SELECT ordinal::integer AS "Ordinal",
                   public.wayfarer_activity_name_v1(raw, false) AS "DisplayName",
                   public.wayfarer_activity_name_v1(raw, true) AS "NormalizedName"
            FROM unnest({names}::text[]) WITH ORDINALITY AS input(raw, ordinal)
            ORDER BY "NormalizedName" COLLATE "C", ordinal
            """).ToListAsync(token);
        invalid += normalized.Count(n => n.DisplayName is null);
        var valid = normalized.Where(n => !string.IsNullOrEmpty(n.NormalizedName)).ToArray();
        foreach (var group in valid.GroupBy(n => n.NormalizedName!, StringComparer.Ordinal))
        {
            var id = await ResolveAsync(group.Key, group.First().DisplayName!, token);
            foreach (var name in group) supplied[name.Ordinal - 1].ActivityTypeId = id;
        }
        if (valid.Any(n => supplied[n.Ordinal - 1].ActivityTypeId is null))
            throw new InvalidOperationException("A valid imported Activity was not reconciled.");

        // On error the caller rolls back the transaction, which also restores these local settings.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT set_config('statement_timeout', {settings.StatementTimeout}, true),
                   set_config('lock_timeout', {settings.LockTimeout}, true)
            """, token);
        return invalid;
    }

    /// <summary>Locks existing names, or inserts conflict-safely and looks up the winner in a fresh statement snapshot.</summary>
    private async Task<int> ResolveAsync(string key, string display, CancellationToken token)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = await FindLockedAsync(key, token);
            if (existing.HasValue) return existing.Value;
            _ = await db.Database.SqlQuery<int>($"""
                INSERT INTO "ActivityTypes" ("Name", "Description") VALUES ({display}, NULL)
                ON CONFLICT ("NormalizedName") DO NOTHING RETURNING "Id" AS "Value"
                """).ToListAsync(token);
            // A concurrent winner may be invisible to the INSERT snapshot. Never combine this with a CTE fallback.
            var winner = await FindLockedAsync(key, token);
            if (winner.HasValue) return winner.Value;
        }
        throw new ActivityCatalogResolutionException();
    }

    /// <summary>FOR SHARE fences both Admin rename and delete through the Location/checkpoint commit.</summary>
    private async Task<int?> FindLockedAsync(string key, CancellationToken token)
    {
        var ids = await db.Database.SqlQuery<int>($"""
            SELECT "Id" AS "Value" FROM "ActivityTypes"
            WHERE "NormalizedName" = {key} COLLATE "C" FOR SHARE
            """).ToListAsync(token);
        return ids.Count switch
        {
            0 => null,
            1 => ids[0],
            _ => throw new InvalidOperationException("Activity catalog identity is not unique.")
        };
    }

    /// <summary>Restores the connection's original transaction-local statement and lock limits.</summary>
    private sealed record CatalogTimeouts(string StatementTimeout, string LockTimeout);

    /// <summary>Database normalization results retain accepted input order independently of key order.</summary>
    private sealed record NormalizedImportActivity(int Ordinal, string? DisplayName, string? NormalizedName);
}

/// <summary>A vanished or renamed conflict winner requires a fresh whole-batch attempt.</summary>
internal sealed class ActivityCatalogResolutionException : Exception;
