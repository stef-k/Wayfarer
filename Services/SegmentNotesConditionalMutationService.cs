using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>Shared Web/bearer authority for atomic Segment Notes comparisons without aggregate replay.</summary>
internal sealed class SegmentNotesConditionalMutationService(ApplicationDbContext db)
{
    /// <summary>Locks Segment before Trip, revalidates live authority after waits and commits only Notes plus current-safe freshness.</summary>
    internal async Task<SegmentNotesConditionalResult> ApplyAsync(Guid tripId, Guid segmentId, string userId, Stream body, bool web,
        Func<CancellationToken, Task<bool>>? revalidateBearer, CancellationToken token)
    {
        if (!await db.Segments.AsNoTracking().AnyAsync(s => s.Id == segmentId && s.TripId == tripId
                && s.UserId == userId && s.Trip.UserId == userId, token)) throw Unavailable();
        var request = await SegmentNotesConditionalRequest.ReadAsync(body, web, token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var commitStarted = false;
        try
        {
            // Subset of profiles -> Segments -> Places/Areas -> Regions -> Trips used by aggregate and #848 deletion owners.
            await SegmentRouteReconciler.LockSegmentAsync(db, segmentId, token);
            var segment = await db.Segments.AsNoTracking().SingleOrDefaultAsync(s => s.Id == segmentId, token);
            if (segment == null || segment.UserId != userId) throw Unavailable();
            if (segment.TripId != tripId)
                throw new TripMetadataMutationRejected(409, "mutation-scope-stale", "The Segment parent changed. Keep your draft and review its current Trip.");
            var trip = await db.Trips.FromSqlInterpolated($"SELECT * FROM public.\"Trips\" WHERE \"Id\" = {tripId} AND \"UserId\" = {userId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(token);
            if (trip == null) throw Unavailable();
            var active = await db.Users.FromSqlInterpolated($"SELECT * FROM public.\"AspNetUsers\" WHERE \"Id\" = {userId} AND \"IsActive\" FOR SHARE")
                .AsNoTracking().ToListAsync(token);
            if (active.Count == 0 || revalidateBearer != null && !await revalidateBearer(token))
                throw new TripMetadataMutationRejected(401, "mutation-authority-unavailable", "Authentication is no longer valid.");
            if (!string.Equals(request.Expected, RichNotes.Normalize(segment.Notes), StringComparison.Ordinal))
                throw new TripMetadataMutationRejected(409, "mutation-base-stale",
                    "Segment Notes changed. Keep your draft, review current Notes and explicitly choose a new base before saving.");
            var applied = !string.Equals(request.Desired, segment.Notes, StringComparison.Ordinal);
            if (applied)
            {
                await db.Segments.Where(s => s.Id == segmentId && s.TripId == tripId && s.UserId == userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(s => s.Notes, request.Desired), token);
                var now = DateTime.UtcNow;
                await db.Trips.Where(t => t.Id == tripId && t.UserId == userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, t => t.UpdatedAt > now ? t.UpdatedAt : now), token);
            }
            // Capture complete published state and its fresh xmin while the Segment lock still excludes other writers.
            var saved = await db.Segments.AsNoTracking().Include(s => s.FromPlace).Include(s => s.ToPlace)
                .Include(s => s.Waypoints.OrderBy(w => w.Position)).ThenInclude(w => w.Place)
                .SingleAsync(s => s.Id == segmentId, token);
            commitStarted = true;
            await transaction.CommitAsync(token);
            return new(saved, applied);
        }
        catch (Exception original)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception rollback) { throw new AggregateException("Segment Notes edit failed and rollback also failed.", original, rollback); }
            if (!commitStarted && original is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure })
                throw new TripMetadataMutationRejected(409, "mutation-contention", "Segment Notes contention prevented this attempt. Review current values before saving.");
            throw;
        }
    }

    /// <summary>Conceals foreign and missing Segment/Trip chains identically.</summary>
    private static TripMetadataMutationRejected Unavailable() => new(404, "mutation-scope-unavailable", "Segment not found.");
}

/// <summary>Returned only after commit; neither HTTP delivery proof nor an idempotency receipt.</summary>
internal sealed record SegmentNotesConditionalResult(Segment Segment, bool Applied);
