using System.Text;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

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

    /// <summary>Canonicalizes initial seed data using the same database authority as Admin writes.</summary>
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
}

/// <summary>Database normalization outputs, with no second client-side name matcher.</summary>
public sealed record ActivityName(string? DisplayName, string? NormalizedName);
