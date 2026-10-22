using System.Data;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>One current-account write boundary for both cookie sharing toggles.</summary>
internal sealed class TripShareProgressMutationService(ApplicationDbContext db)
{
    /// <summary>Locks the Trip then tries the active account without waiting; writes only sharing and current-safe freshness.</summary>
    internal async Task<(TripShareProgressStatus Status, Trip? Trip)> ApplyAsync(
        Guid tripId, string userId, bool enabled, CancellationToken token)
    {
        var relational = db.Database.IsRelational();
        await using var transaction = relational
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token)
            : null;
        try
        {
            // Retain metadata's Trip-first order and obtain current ownership/publication after any Trip wait.
            var trip = relational
                ? await db.Trips.FromSqlInterpolated($"SELECT * FROM public.\"Trips\" WHERE \"Id\" = {tripId} AND \"UserId\" = {userId} FOR UPDATE")
                    .AsNoTracking().SingleOrDefaultAsync(token)
                : await db.Trips.SingleOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, token);
            if (trip == null) return (TripShareProgressStatus.NotFound, null);

            // Never wait for an account while holding its Trip: Identity deletion locks account then cascades to Trip.
            // SKIP LOCKED denies this attempt if busy; a successful shared lock protects current activity through commit.
            var active = relational
                ? (await db.Users.FromSqlInterpolated($"SELECT * FROM public.\"AspNetUsers\" WHERE \"Id\" = {userId} AND \"IsActive\" FOR SHARE SKIP LOCKED")
                    .AsNoTracking().ToListAsync(token)).Count != 0
                : await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsActive, token);
            if (!active)
            {
                // No sharing write has run. Release the Trip before reporting a known-unapplied generic denial.
                if (transaction != null) await transaction.RollbackAsync(token);
                return (TripShareProgressStatus.AuthorityUnavailable, null);
            }
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
    /// <summary>The current owning account is missing, inactive or locked by another transaction.</summary>
    AuthorityUnavailable,
    /// <summary>Enabling requires a currently public Trip.</summary>
    NotPublic
}
