using System.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Services;

/// <summary>Extends existing leaf deletion consent to private planning under the same material row barrier.</summary>
internal sealed class PlanningDeletionGuard(ApplicationDbContext db)
{
    /// <summary>Holds the object through deletion; omission can delete only an unplanned target.</summary>
    internal async Task<IActionResult> DeleteAsync(Guid tripId, Guid id, string owner, string kind, Stream body,
        Func<Task<IActionResult>> delete, CancellationToken token)
    {
        if (!db.Database.IsRelational()) return await delete();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        try
        {
            var sql = kind == "area" ? "SELECT * FROM public.\"Areas\" WHERE \"Id\" = {0} FOR UPDATE"
                : "SELECT *, xmin FROM public.\"Segments\" WHERE \"Id\" = {0} FOR UPDATE";
            PlanningTargetScope? target = null;
            if (kind == "area")
            {
                var area = await db.Areas.FromSqlRaw(sql, id).AsNoTracking().SingleOrDefaultAsync(token);
                if (area != null)
                {
                    var region = await db.Regions.FromSqlInterpolated($"SELECT * FROM public.\"Regions\" WHERE \"Id\" = {area.RegionId} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(token);
                    if (region?.TripId == tripId && region.UserId == owner) target = new(area.RegionId, area.Planning);
                }
            }
            else
            {
                var segment = await db.Segments.FromSqlRaw(sql, id).AsNoTracking().SingleOrDefaultAsync(token);
                if (segment?.TripId == tripId && segment.UserId == owner) target = new(tripId, segment.Planning);
            }
            var trip = await db.Trips.FromSqlInterpolated($"SELECT * FROM public.\"Trips\" WHERE \"Id\" = {tripId} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(token);
            if (target == null || trip?.UserId != owner) return new NotFoundResult();
            var users = await db.Users.FromSqlInterpolated($"SELECT * FROM public.\"AspNetUsers\" WHERE \"Id\" = {owner} AND \"IsActive\" FOR SHARE").AsNoTracking().ToListAsync(token);
            if (users.Count == 0) return new UnauthorizedResult();
            // Read an optional deletion original without treating an absent body as permission to lose planning.
            using var buffer = new MemoryStream();
            await body.CopyToAsync(buffer, token);
            PlanningMutationRequest? request = null;
            if (buffer.Length > 0) { buffer.Position = 0; request = await PlanningMutationRequest.ReadAsync(buffer, token); }
            if (request?.Value != null || request != null && request.ParentId != target.ParentId
                || !Models.Planning.PlanningCodec.Equal(request?.Original, target.Planning))
                throw new TripMetadataMutationRejected(409, "mutation-base-stale", "Planning changed or deletion lacks its original. Review current planning before confirming deletion.");
            var result = await delete();
            if (result is ObjectResult { StatusCode: >= 400 } || result is StatusCodeResult { StatusCode: >= 400 }) return result;
            await transaction.CommitAsync(token);
            return result;
        }
        catch (Exception primary)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception rollback) { throw new AggregateException("Deletion failed and rollback also failed.", primary, rollback); }
            if (primary is TripMetadataMutationRejected rejection) return rejection.ToActionResult();
            throw;
        }
    }

    /// <summary>Only the direct parent and complete aggregate belong to this narrow additional consent.</summary>
    private sealed record PlanningTargetScope(Guid ParentId, Models.Planning.PlanningAggregate? Planning);
}
