using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Narrow workspace barrier shared by Finish and normal destination deletion.</summary>
internal static class TripCompositionLifecycle
{
    /// <summary>Locks owner-scoped headers before destination rows; source identities never participate in deletion.</summary>
    internal static async Task LockAsync(ApplicationDbContext db, string owner, Guid? tripId, Guid? compositionId, CancellationToken token)
    {
        if (!db.Database.IsNpgsql()) return;
        await db.Set<TripComposition>().FromSqlInterpolated($"""
            SELECT * FROM public."TripCompositions" WHERE "UserId" = {owner}
            AND ("TripId" = {tripId} OR "Id" = {compositionId})
            ORDER BY "Id" FOR UPDATE
            """).AsNoTracking().ToListAsync(token);
    }

    /// <summary>Deletes C with workspace/origins through FK cascades in the same account-fenced transaction.</summary>
    internal static async Task<bool> DeleteAsync(ApplicationDbContext db, Guid id, string owner, CancellationToken token)
    {
        await using var transaction = await LocationDeletionFence.BeginAsync(db, owner, token);
        try
        {
            await LockAsync(db, owner, id, null, token);
            var trip = await db.Trips.SingleOrDefaultAsync(t => t.Id == id && t.UserId == owner, token);
            if (trip == null) return false;
            db.Trips.Remove(trip);
            await db.SaveChangesAsync(token);
            if (transaction != null) await transaction.CommitAsync(token);
            return true;
        }
        catch (Exception primary)
        {
            db.ChangeTracker.Clear();
            if (transaction != null)
            {
                try { await transaction.RollbackAsync(CancellationToken.None); }
                catch (Exception cleanup) { throw new AggregateException("Trip deletion failed and rollback could not be confirmed.", primary, cleanup); }
            }
            throw;
        }
    }
}
