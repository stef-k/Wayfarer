using System.Data;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;

namespace Wayfarer.Services;

/// <summary>Uses the database's Activity identity and permanent numeric compatibility map.</summary>
public sealed class ActivityCatalog(ApplicationDbContext db)
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <summary>Rejects only text that cannot be transported unchanged to PostgreSQL.</summary>
    public static bool CanTransport(string? raw)
    {
        if (raw is null) return true;
        if (raw.Contains('\0')) return false;
        try { StrictUtf8.GetByteCount(raw); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    /// <summary>Returns both v1 outputs; null outputs mean invalid, empty outputs mean missing.</summary>
    public async Task<ActivityName> NormalizeAsync(string? raw, CancellationToken cancellationToken = default)
    {
        if (!CanTransport(raw)) return new ActivityName(null, null);
        return await db.Database.SqlQuery<ActivityName>($"""
            SELECT public.wayfarer_activity_name_v1({raw}, false) AS "DisplayName",
                   public.wayfarer_activity_name_v1({raw}, true) AS "NormalizedName"
            """).SingleAsync(cancellationToken);
    }

    /// <summary>Canonicalizes and saves initial seed data using the same database authority as Admin writes.</summary>
    public async Task SaveInitialAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in db.ChangeTracker.Entries<ActivityType>().Where(e => e.State == EntityState.Added))
        {
            var name = await NormalizeAsync(entry.Entity.Name, cancellationToken);
            if (string.IsNullOrEmpty(name.DisplayName))
                throw new InvalidOperationException("Invalid initial Activity name.");
            entry.Entity.Name = name.DisplayName;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Resolves a live positive ID first, otherwise its direct retained redirect.</summary>
    public async Task<int?> ResolveIdAsync(int? id, CancellationToken cancellationToken = default)
    {
        if (id is null or <= 0) return null;
        var live = await db.ActivityTypes.Where(a => a.Id == id).Select(a => (int?)a.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return live ?? await db.Set<ActivityTypeIdRedirect>().Where(r => r.LegacyId == id)
            .Select(r => (int?)r.ActivityTypeId).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Resolves a bounded read selection in two queries using the same live-first retained redirect authority.</summary>
    internal async Task<int[]?> ResolveIdsAsync(int[] ids, CancellationToken token)
    {
        var live = await db.ActivityTypes.Where(row => ids.Contains(row.Id)).Select(row => row.Id).ToArrayAsync(token);
        var missing = ids.Except(live).ToArray();
        var redirects = await db.Set<ActivityTypeIdRedirect>().Where(row => missing.Contains(row.LegacyId))
            .Select(row => new { row.LegacyId, row.ActivityTypeId }).ToArrayAsync(token);
        if (live.Length + redirects.Length != ids.Length) return null;
        return live.Concat(redirects.Select(row => row.ActivityTypeId)).Distinct().Order().ToArray();
    }

    /// <summary>Retains the existing FK failure behavior of create writers for unknown/offline IDs.</summary>
    public async Task<int?> ResolveWriteIdAsync(int? id, CancellationToken cancellationToken = default) =>
        await ResolveIdAsync(id, cancellationToken) ?? id;

    /// <summary>Finds an existing entry by v1 identity without gaining catalog creation authority.</summary>
    public async Task<int?> FindByNameAsync(string? raw, CancellationToken cancellationToken = default)
    {
        var name = await NormalizeAsync(raw, cancellationToken);
        if (string.IsNullOrEmpty(name.NormalizedName)) return null;
        return await db.ActivityTypes.Where(a => EF.Property<string>(a, "NormalizedName") == name.NormalizedName)
            .Select(a => (int?)a.Id).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Reads only catalog fields, row versions and aggregate Location counts for the two choices.</summary>
    public Task<List<ActivityConsolidationEntry>> GetConsolidationEntriesAsync(int sourceId, int destinationId,
        CancellationToken token = default) => db.Database.SqlQuery<ActivityConsolidationEntry>($"""
            SELECT a."Id", a."Name", a."Description", a.xmin::text AS "Version",
                   (SELECT count(*) FROM "Locations" l WHERE l."ActivityTypeId" = a."Id") AS "LocationCount"
            FROM "ActivityTypes" a WHERE a."Id" = {sourceId} OR a."Id" = {destinationId}
            """).ToListAsync(token);

    /// <summary>
    /// Moves only Activity references and preserves prose before permanently retiring the source.
    /// Repeatable read prevents a concurrent catalog edit from being silently overwritten or deleted;
    /// existing restrictive FKs reject racing references. Conflicts roll back for a manual retry.
    /// </summary>
    public async Task<bool> ConsolidateAsync(int sourceId, int destinationId, string? sourceVersion,
        string? destinationVersion, CancellationToken token = default)
    {
        if (sourceId <= 0 || destinationId <= 0 || sourceId == destinationId
            || string.IsNullOrEmpty(sourceVersion) || string.IsNullOrEmpty(destinationVersion)) return false;

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var entries = await GetConsolidationEntriesAsync(sourceId, destinationId, token);
        var source = entries.SingleOrDefault(a => a.Id == sourceId);
        var destination = entries.SingleOrDefault(a => a.Id == destinationId);
        if (source is null || destination is null
            || source.Version != sourceVersion || destination.Version != destinationVersion) return false;

        var description = PreserveDescription(source, destination);
        if (!string.Equals(description, destination.Description, StringComparison.Ordinal))
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "ActivityTypes" SET "Description" = {description} WHERE "Id" = {destinationId}
                """, token);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Locations" SET "ActivityTypeId" = {destinationId} WHERE "ActivityTypeId" = {sourceId}
            """, token);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ActivityTypeIdRedirects" SET "ActivityTypeId" = {destinationId} WHERE "ActivityTypeId" = {sourceId}
            """, token);
        // The live-ID overlap guard requires removal before inserting the retired ID, in this same transaction.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "ActivityTypes" WHERE "Id" = {sourceId}
            """, token);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ActivityTypeIdRedirects" ("LegacyId", "ActivityTypeId") VALUES ({sourceId}, {destinationId})
            """, token);
        await transaction.CommitAsync(token);
        return true;
    }

    /// <summary>Matches the migration's exact-text equality, Unicode 16 blank test and original source heading.</summary>
    private static string? PreserveDescription(ActivityConsolidationEntry source, ActivityConsolidationEntry destination)
    {
        const string whitespace = "\u0009\u000A\u000B\u000C\u000D\u0020\u0085\u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000";
        if (string.IsNullOrEmpty(source.Description?.Trim(whitespace.ToCharArray()))
            || string.Equals(source.Description, destination.Description, StringComparison.Ordinal))
            return destination.Description;
        if (string.IsNullOrEmpty(destination.Description?.Trim(whitespace.ToCharArray()))) return source.Description;
        return $"{destination.Description}\n\nPreserved description from Activity {source.Id} ({source.Name}):\n{source.Description}";
    }
}

/// <summary>Database normalization outputs, with no second client-side name matcher.</summary>
public sealed record ActivityName(string? DisplayName, string? NormalizedName);
