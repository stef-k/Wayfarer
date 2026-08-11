using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Owns the property-only relational mutation for legacy Segment notes compatibility.</summary>
internal static class SegmentNotesMutation
{
    /// <summary>Atomically updates notes and the owning Trip timestamp without attaching stale aggregate state.</summary>
    internal static async Task<bool> UpdateRelationalAsync(
        ApplicationDbContext context,
        Guid tripId,
        Guid segmentId,
        string userId,
        string notes,
        CancellationToken cancellationToken)
    {
        var updatedAt = DateTime.UtcNow;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var tripCount = await context.Trips
                .Where(item => item.Id == tripId && item.UserId == userId)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    item => item.UpdatedAt,
                    item => item.UpdatedAt > updatedAt ? item.UpdatedAt : updatedAt), cancellationToken);
            var segmentCount = await context.Segments
                .Where(item => item.Id == segmentId && item.TripId == tripId && item.UserId == userId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Notes, notes), cancellationToken);
            if (tripCount != 1 || segmentCount != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return false;
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (Exception original)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException(
                    "Segment notes update failed and its transaction could not be rolled back.",
                    original, rollbackFailure);
            }
            ExceptionDispatchInfo.Capture(original).Throw();
            throw;
        }
    }
}
