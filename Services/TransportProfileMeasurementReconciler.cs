using System.Data;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Atomically changes planning speed and reconciles every referenced Segment measurement.</summary>
public static class TransportProfileMeasurementReconciler
{
    /// <summary>Owns the serializable profile-wide transaction and approved global lock ordering.</summary>
    public static async Task<TransportProfileMeasurementResult> ReconcileAsync(
        ApplicationDbContext dbContext,
        Guid profileId,
        double? proposedSpeedKmh,
        string actorUserId,
        CancellationToken cancellationToken) =>
        await ReconcileAsync(dbContext, profileId, proposedSpeedKmh, actorUserId, null, cancellationToken);

    /// <summary>Atomically applies all allowlisted profile fields with referenced measurement reconciliation.</summary>
    public static async Task<TransportProfileMeasurementResult> ReconcileUpdateAsync(
        ApplicationDbContext dbContext,
        Guid profileId,
        TransportProfileUpdateProposal update,
        string actorUserId,
        CancellationToken cancellationToken) =>
        await ReconcileAsync(dbContext, profileId, update.PlanningSpeedKmh, actorUserId, update, cancellationToken);

    private static async Task<TransportProfileMeasurementResult> ReconcileAsync(
        ApplicationDbContext dbContext,
        Guid profileId,
        double? proposedSpeedKmh,
        string actorUserId,
        TransportProfileUpdateProposal? update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        if (proposedSpeedKmh.HasValue && (!double.IsFinite(proposedSpeedKmh.Value) || proposedSpeedKmh.Value <= 0))
            return TransportProfileMeasurementResult.Failure("Planning speed must be a finite positive number or null.");
        EnsureCleanContext(dbContext);

        if (!dbContext.Database.IsRelational())
            return await ReconcileLockedAsync(dbContext, profileId, proposedSpeedKmh, actorUserId, update, cancellationToken);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        Guid[] segmentIds = [];
        var cleanupAttempted = false;
        try
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM public.\"TransportProfiles\" WHERE \"Id\" = {profileId} FOR UPDATE", cancellationToken);
            var profile = await dbContext.Set<TransportProfile>().AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == profileId, cancellationToken);
            if (profile == null) return TransportProfileMeasurementResult.Failure("Transport profile was not found.");
            segmentIds = await ReferenceIdsAsync(dbContext, profile, cancellationToken);
            foreach (var segmentId in segmentIds.Order())
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM public.\"Segments\" WHERE \"Id\" = {segmentId} FOR UPDATE", cancellationToken);

            var result = await ReconcileLockedAsync(dbContext, profileId, proposedSpeedKmh, actorUserId, update, cancellationToken);
            if (!result.Succeeded)
            {
                cleanupAttempted = true;
                await CleanupAsync(dbContext, transaction, profileId, segmentIds,
                    new InvalidOperationException(string.Join(" ", result.Errors)));
                return result;
            }
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception originalFailure)
        {
            if (cleanupAttempted)
            {
                ExceptionDispatchInfo.Capture(originalFailure).Throw();
                throw;
            }
            await CleanupAsync(dbContext, transaction, profileId, segmentIds, originalFailure);
            ExceptionDispatchInfo.Capture(originalFailure).Throw();
            throw;
        }
    }

    /// <summary>Preserves the operation failure and invalidates the context when mandatory cleanup is incomplete.</summary>
    private static async Task CleanupAsync(
        ApplicationDbContext dbContext,
        IDbContextTransaction transaction,
        Guid profileId,
        IReadOnlyList<Guid> segmentIds,
        Exception originalFailure)
    {
        var cleanupFailures = new List<Exception>();
        try { await transaction.RollbackAsync(CancellationToken.None); }
        catch (Exception rollbackFailure) { cleanupFailures.Add(rollbackFailure); }
        try { await RecoverTrackedScopeAsync(dbContext, profileId, segmentIds); }
        catch (Exception recoveryFailure) { cleanupFailures.Add(recoveryFailure); }
        if (cleanupFailures.Count == 0) return;

        try { await dbContext.DisposeAsync(); }
        catch (Exception disposalFailure) { cleanupFailures.Add(disposalFailure); }
        throw new AggregateException(
            "Transport-profile reconciliation failed and mandatory cleanup could not restore a reusable DbContext.",
            [originalFailure, .. cleanupFailures]);
    }

    /// <summary>Recovers only the profile-batch aggregate scope and preserves unrelated unchanged tracking.</summary>
    private static async Task RecoverTrackedScopeAsync(
        ApplicationDbContext dbContext,
        Guid profileId,
        IReadOnlyList<Guid> segmentIds)
    {
        foreach (var audit in dbContext.ChangeTracker.Entries<AuditLog>()
                     .Where(entry => entry.State == EntityState.Added).ToArray())
            audit.State = EntityState.Detached;
        foreach (var segmentId in segmentIds)
            await SegmentRouteReconciler.RecoverAggregateAsync(dbContext, segmentId, CancellationToken.None);
        var profileEntry = dbContext.ChangeTracker.Entries<TransportProfile>()
            .SingleOrDefault(entry => entry.Entity.Id == profileId);
        if (profileEntry != null) await profileEntry.ReloadAsync(CancellationToken.None);
    }

    private static async Task<TransportProfileMeasurementResult> ReconcileLockedAsync(
        ApplicationDbContext dbContext,
        Guid profileId,
        double? proposedSpeedKmh,
        string actorUserId,
        TransportProfileUpdateProposal? update,
        CancellationToken cancellationToken)
    {
        var profile = await dbContext.Set<TransportProfile>()
            .SingleOrDefaultAsync(item => item.Id == profileId, cancellationToken);
        if (profile == null) return TransportProfileMeasurementResult.Failure("Transport profile was not found.");
        if (update != null && profile.RowVersion != update.RowVersion)
            return TransportProfileMeasurementResult.Failure("The profile changed after the form was loaded.");
        var segmentIds = await ReferenceIdsAsync(dbContext, profile, cancellationToken);
        var segments = await dbContext.Segments.AsNoTracking()
            .Where(segment => segmentIds.Contains(segment.Id))
            .OrderBy(segment => segment.Id)
            .ToArrayAsync(cancellationToken);
        var automatic = segments.Count(segment => segment.EstimatedDurationSource == EstimatedDurationSource.Automatic);
        var manual = segments.Length - automatic;
        var unavailable = 0;
        foreach (var snapshot in segments)
        {
            var canonical = await SegmentRouteReconciler.LoadAggregateAsync(dbContext, snapshot.Id, cancellationToken);
            if (canonical == null) return TransportProfileMeasurementResult.Failure("A referenced Segment changed concurrently.");
            var proposal = new SegmentRouteProposal(
                canonical.Id,
                canonical.FromPlaceId,
                canonical.ToPlaceId,
                canonical.Waypoints.OrderBy(item => item.Position)
                    .Select(item => new SegmentWaypointProposal(item.PlaceId, item.Position, item.RouteVertexIndex)).ToArray(),
                canonical.RouteGeometry,
                new(canonical.Mode, canonical.TransportProfileId, canonical.EstimatedDurationSource,
                    canonical.EstimatedDuration?.TotalMinutes, AllowUnavailableAutomatic: true,
                    UsePlanningSpeedOverride: true, PlanningSpeedKmhOverride: proposedSpeedKmh));
            var result = await SegmentRouteReconciler.ReconcileLockedAsync(
                dbContext, proposal, refreshCanonicalState: false, cancellationToken);
            if (!result.Succeeded) return TransportProfileMeasurementResult.Failure(string.Join(" ", result.Errors));
            if (canonical.EstimatedDurationSource == EstimatedDurationSource.Automatic
                && canonical.EstimatedDuration == null) unavailable++;
        }

        var oldSpeed = profile.PlanningSpeedKmh;
        profile.PlanningSpeedKmh = proposedSpeedKmh;
        if (update != null)
        {
            profile.Label = update.Label;
            profile.Category = update.Category;
            profile.SortOrder = update.SortOrder;
            profile.IsActive = update.IsActive;
            profile.Description = update.Description;
        }
        dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = actorUserId,
            Action = "TransportProfileSpeedReconciliation",
            Timestamp = DateTime.UtcNow,
            Details = $"ProfileId={profile.Id}; Key={profile.Key}; OldSpeed={FormatSpeed(oldSpeed)}; NewSpeed={FormatSpeed(proposedSpeedKmh)}; Total={segments.Length}; Automatic={automatic}; Manual={manual}; Recalculated={automatic - unavailable}; Unavailable={unavailable}; Outcome=Committed"
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(true, [], segments.Length, automatic, manual, automatic - unavailable, unavailable);
    }

    private static Task<Guid[]> ReferenceIdsAsync(
        ApplicationDbContext dbContext,
        TransportProfile profile,
        CancellationToken cancellationToken) =>
        dbContext.Segments.AsNoTracking()
            .Where(segment => segment.TransportProfileId == profile.Id
                || (segment.TransportProfileId == null && segment.Mode.Trim().ToLower() == profile.Key))
            .OrderBy(segment => segment.Id)
            .Select(segment => segment.Id)
            .ToArrayAsync(cancellationToken);

    private static void EnsureCleanContext(ApplicationDbContext dbContext)
    {
        dbContext.ChangeTracker.DetectChanges();
        if (dbContext.ChangeTracker.Entries().Any(entry =>
                entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Profile measurement reconciliation requires a clean DbContext.");
    }

    private static string FormatSpeed(double? speed) =>
        speed?.ToString("R", CultureInfo.InvariantCulture) ?? "null";
}

/// <summary>Allowlisted profile fields committed with one speed-reconciliation transaction.</summary>
public sealed record TransportProfileUpdateProposal(
    string Label,
    string Category,
    double? PlanningSpeedKmh,
    int SortOrder,
    bool IsActive,
    string? Description,
    uint RowVersion);

/// <summary>Bounded outcome and dependency counts for a profile-speed reconciliation.</summary>
public sealed record TransportProfileMeasurementResult(
    bool Succeeded,
    IReadOnlyList<string> Errors,
    int TotalReferences,
    int AutomaticReferences,
    int ManualReferences,
    int RecalculatedReferences,
    int UnavailableReferences)
{
    /// <summary>Creates a bounded failed outcome without exposing private route content.</summary>
    public static TransportProfileMeasurementResult Failure(string error) => new(false, [error], 0, 0, 0, 0, 0);
}
