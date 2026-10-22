using System.Data;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>One current-account write boundary for both cookie sharing toggles.</summary>
internal sealed class TripShareProgressMutationService(ApplicationDbContext db)
{
    /// <summary>Locks Trip then active account, matching protected metadata; writes only sharing and current-safe freshness.</summary>
    internal async Task<(TripShareProgressStatus Status, Trip? Trip)> ApplyAsync(
        Guid tripId, string userId, bool enabled, CancellationToken token)
    {
        var relational = db.Database.IsRelational();
        await using var transaction = relational
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token)
            : null;
        try
        {
            // Never adopt a preflight/tracked Trip after the lock wait or reverse the Trip -> account lock order.
            var trip = relational
                ? await db.Trips.FromSqlInterpolated($"SELECT * FROM public.\"Trips\" WHERE \"Id\" = {tripId} AND \"UserId\" = {userId} FOR UPDATE")
                    .AsNoTracking().SingleOrDefaultAsync(token)
                : await db.Trips.SingleOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, token);
            if (trip == null) return (TripShareProgressStatus.NotFound, null);

            // The shared account lock also waits for pending deactivation and protects activity through commit.
            var active = relational
                ? (await db.Users.FromSqlInterpolated($"SELECT * FROM public.\"AspNetUsers\" WHERE \"Id\" = {userId} AND \"IsActive\" FOR SHARE")
                    .AsNoTracking().ToListAsync(token)).Count != 0
                : await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsActive, token);
            if (!active) return (TripShareProgressStatus.Inactive, null);
            if (enabled && !trip.IsPublic) return (TripShareProgressStatus.NotPublic, null);

            var now = DateTime.UtcNow;
            trip.ShareProgressEnabled = enabled;
            trip.UpdatedAt = trip.UpdatedAt > now ? trip.UpdatedAt : now;
            if (relational)
            {
                // Avoid SaveChanges: unrelated tracked fields must never enter this sharing write.
                await db.Trips.Where(t => t.Id == tripId && t.UserId == userId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.ShareProgressEnabled, enabled)
                        .SetProperty(t => t.UpdatedAt, trip.UpdatedAt), token);
            }
            else
            {
                // Retain the existing InMemory controller-test seam; PostgreSQL owns concurrency evidence.
                await db.SaveChangesAsync(token);
            }
            if (transaction != null) await transaction.CommitAsync(token);
            return (TripShareProgressStatus.Succeeded, trip);
        }
        catch (Exception original)
        {
            if (transaction == null) throw;
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException("Trip sharing failed and rollback also failed.", original, rollbackFailure);
            }
            // Commit/transport failures remain uncertain; never turn them into a definitive denial or retry.
            throw;
        }
    }
}

/// <summary>Path-neutral outcomes mapped to each existing route's response contract.</summary>
internal enum TripShareProgressStatus
{
    /// <summary>The sharing change committed.</summary>
    Succeeded,
    /// <summary>The current Trip does not exist in the owner's scope.</summary>
    NotFound,
    /// <summary>The current owning account is missing or inactive.</summary>
    Inactive,
    /// <summary>Enabling requires a currently public Trip.</summary>
    NotPublic
}
