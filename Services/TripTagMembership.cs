using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Shared PostgreSQL locking for membership commits and catalogue cleanup.</summary>
internal static class TripTagMembership
{
    /// <summary>Serializes a Trip's join writers before reading its current membership.</summary>
    internal static Task<Trip?> LockTripAsync(ApplicationDbContext db, Guid id, string owner, CancellationToken token) =>
        db.Trips.FromSqlInterpolated($"""SELECT * FROM public."Trips" WHERE "Id" = {id} AND "UserId" = {owner} FOR UPDATE""")
            .AsNoTracking().SingleOrDefaultAsync(token);

    /// <summary>Orders catalogue resolution, reuse and deletion after existing Trip and account locks.</summary>
    internal static Task LockCatalogueAsync(ApplicationDbContext db, CancellationToken token) =>
        db.Database.IsRelational()
            ? db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('Wayfarer.TripTags.Catalogue', 0))", token)
            : Task.CompletedTask;

    /// <summary>Protects each exact reused row through the join commit, or refuses if its snapshot lost it.</summary>
    internal static async Task ProtectAsync(ApplicationDbContext db, IEnumerable<Guid> ids, CancellationToken token)
    {
        var requested = ids.Distinct().ToArray();
        if (requested.Length == 0 || !db.Database.IsRelational()) return;
        var locked = await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM public."Tags" WHERE "Id" = ANY({requested}) ORDER BY "Id" FOR KEY SHARE
            """).ToListAsync(token);
        if (locked.Count != requested.Length) throw TripMetadataMutationService.Contention();
    }

    /// <summary>Creates a row version for Serializable observers without changing tag-only freshness.</summary>
    internal static Task TouchTripAsync(ApplicationDbContext db, Guid tripId, CancellationToken token) =>
        db.Database.IsRelational() ? db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE public."Trips" SET "UpdatedAt" = "UpdatedAt" WHERE "Id" = {tripId}
            """, token) : Task.CompletedTask;

    /// <summary>Deletes only locked catalogue identities whose fresh reference check is empty.</summary>
    internal static async Task<Tag[]> DeleteUnusedAsync(ApplicationDbContext db, Guid[] ids, CancellationToken token)
    {
        if (ids.Length == 0) return [];
        if (!db.Database.IsRelational())
        {
            var unused = await db.Tags.Where(tag => ids.Contains(tag.Id) && !tag.Trips.Any()).ToArrayAsync(token);
            db.Tags.RemoveRange(unused);
            await db.SaveChangesAsync(token);
            return unused;
        }
        if (db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted)
            return [];
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT "Id" FROM public."Tags" WHERE "Id" = ANY({ids}) ORDER BY "Id" FOR UPDATE
            """, token);
        return await db.Tags.FromSqlInterpolated($"""
            DELETE FROM public."Tags" t WHERE t."Id" = ANY({ids})
                AND NOT EXISTS (SELECT 1 FROM public."TripTags" j WHERE j."TagId" = t."Id")
            RETURNING t.*
            """).AsNoTracking().ToArrayAsync(token);
    }
}
