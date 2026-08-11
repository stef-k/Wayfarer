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
            var result = await ReconcileNewTrackedAsync(dbContext, creation, proposal, cancellationToken);
            if (result.Succeeded) await dbContext.SaveChangesAsync(cancellationToken);
            else dbContext.ChangeTracker.Clear();
            return result;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await LockProfilesAsync(dbContext,
                proposal.Measurement?.TransportProfileId is Guid profileId ? [profileId] : [], cancellationToken);
            var result = await ReconcileNewTrackedAsync(dbContext, creation, proposal, cancellationToken);
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

    /// <summary>Validates and composes a new application-identified aggregate before its only insert boundary.</summary>
    private static async Task<SegmentRouteReconciliationResult> ReconcileNewTrackedAsync(
        ApplicationDbContext dbContext,
        SegmentCreation creation,
        SegmentRouteProposal proposal,
        CancellationToken cancellationToken)
    {
        var segment = NewSegment(creation);
        var placesById = await LoadProposalPlacesAsync(dbContext, proposal, cancellationToken);
        var geometry = CopyGeometry(proposal);
        var errors = Validate(creation.TripId, proposal, placesById, geometry);
        var anchors = BuildAnchorChain(proposal, placesById);
        var measurement = await CalculateMeasurementsAsync(
            dbContext, segment, proposal, geometry, anchors, errors, cancellationToken);
        if (errors.Count > 0) return new(false, errors, anchors);

        dbContext.Segments.Add(segment);
        ApplyTrackedState(dbContext, segment, proposal, placesById, geometry);
        ApplyMeasurements(segment, measurement!);
        if (proposal.ApplyNotes) segment.Notes = proposal.NotesHtml ?? string.Empty;
        return new(true, [], anchors);
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
