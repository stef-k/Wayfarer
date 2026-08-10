using System.Data;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Creation wrapper for the shared locked Segment aggregate core.</summary>
public static partial class SegmentRouteReconciler
{
    /// <summary>Creates and reconciles a Segment atomically without exposing an incomplete persisted row.</summary>
    public static async Task<SegmentRouteReconciliationResult> CreateAsync(
        ApplicationDbContext dbContext,
        SegmentCreation creation,
        SegmentRouteProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(creation);
        if (creation.Id != proposal.SegmentId)
            throw new ArgumentException("Creation and route proposal must identify the same Segment.", nameof(proposal));
        EnsureCleanContext(dbContext);

        if (!dbContext.Database.IsRelational())
        {
            dbContext.Segments.Add(NewSegment(creation));
            await dbContext.SaveChangesAsync(cancellationToken);
            var result = await ReconcileLockedAsync(dbContext, proposal, refreshCanonicalState: false, cancellationToken);
            if (result.Succeeded) await dbContext.SaveChangesAsync(cancellationToken);
            else
            {
                dbContext.ChangeTracker.Clear();
                var created = await dbContext.Segments.SingleAsync(item => item.Id == creation.Id, cancellationToken);
                dbContext.Segments.Remove(created);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            return result;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            dbContext.Segments.Add(NewSegment(creation));
            await dbContext.SaveChangesAsync(cancellationToken);
            await LockProfilesAsync(dbContext,
                proposal.Measurement?.TransportProfileId is Guid profileId ? [profileId] : [], cancellationToken);
            await LockSegmentAsync(dbContext, creation.Id, cancellationToken);
            var result = await ReconcileLockedAsync(dbContext, proposal, refreshCanonicalState: true, cancellationToken);
            if (!result.Succeeded)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                dbContext.ChangeTracker.Clear();
                return result;
            }
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    private static Segment NewSegment(SegmentCreation creation) => new()
    {
        Id = creation.Id,
        UserId = creation.UserId,
        TripId = creation.TripId,
        DisplayOrder = creation.DisplayOrder,
        Mode = string.Empty,
        Notes = string.Empty
    };
}

/// <summary>Server-owned scalar values needed before the aggregate route proposal can be reconciled.</summary>
public sealed record SegmentCreation(Guid Id, string UserId, Guid TripId, int DisplayOrder);
