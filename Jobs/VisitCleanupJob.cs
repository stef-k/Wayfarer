using Microsoft.EntityFrameworkCore;
using Quartz;
using Wayfarer.Models;
using Wayfarer.Parsers;

namespace Wayfarer.Jobs;

/// <summary>
/// Scheduled job that performs global cleanup of stale visit data.
/// - Closes open visits that have been inactive beyond the configured threshold.
/// - Deletes stale visit candidates that were never confirmed.
///
/// Uses thresholds from ApplicationSettings:
/// - VisitedEndVisitAfterMinutes: Close visits with no pings for this duration
/// - VisitedCandidateStaleMinutes: Delete candidates older than this
///
/// Note: Per-user cleanup also runs during ping processing, but this job
/// ensures cleanup happens for users who have stopped sending location pings.
/// </summary>
public class VisitCleanupJob : IJob
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IApplicationSettingsService _settingsService;
    private readonly ILogger<VisitCleanupJob> _logger;

    public VisitCleanupJob(
        ApplicationDbContext dbContext,
        IApplicationSettingsService settingsService,
        ILogger<VisitCleanupJob> logger)
    {
        _dbContext = dbContext;
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>
    /// Execute the visit cleanup job.
    /// Supports cancellation via CancellationToken.
    /// </summary>
    public async Task Execute(IJobExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;
        var jobDataMap = context.JobDetail.JobDataMap;
        jobDataMap["Status"] = "Scheduled";

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation("VisitCleanupJob started");
            jobDataMap["Status"] = "In Progress";

            var settings = _settingsService.GetSettings();
            var now = DateTime.UtcNow;

            // 1. Close stale open visits
            var closedVisits = await CloseStaleVisitsAsync(now, settings, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            // 2. Delete stale candidates
            var deletedCandidates = await DeleteStaleCandidatesAsync(now, settings, cancellationToken);

            _logger.LogInformation(
                "VisitCleanupJob completed: closed {ClosedVisits} open visits, deleted {DeletedCandidates} stale candidates",
                closedVisits, deletedCandidates);

            jobDataMap["Status"] = "Completed";
            jobDataMap["StatusMessage"] = $"Closed {closedVisits} visits, deleted {deletedCandidates} candidates";
        }
        catch (OperationCanceledException)
        {
            jobDataMap["Status"] = "Cancelled";
            _logger.LogInformation("VisitCleanupJob was cancelled");
        }
        catch (Exception ex)
        {
            jobDataMap["Status"] = "Failed";
            _logger.LogError(ex, "Error executing VisitCleanupJob");
            throw;
        }
    }

    /// <summary>
    /// Close all open visits that have been inactive beyond the threshold.
    /// Sets EndedAtUtc = LastSeenAtUtc for stale visits.
    /// </summary>
    /// <returns>Number of visits closed.</returns>
    private async Task<int> CloseStaleVisitsAsync(DateTime now, ApplicationSettings settings, CancellationToken cancellationToken)
    {
        var cutoff = now.AddMinutes(-settings.VisitedEndVisitAfterMinutes);

        var staleVisits = await _dbContext.PlaceVisitEvents
            .Where(v => v.EndedAtUtc == null)
            .Where(v => v.LastSeenAtUtc < cutoff)
            .ToListAsync(cancellationToken);

        foreach (var visit in staleVisits)
        {
            cancellationToken.ThrowIfCancellationRequested();

            visit.EndedAtUtc = visit.LastSeenAtUtc;

            _logger.LogDebug(
                "Closed stale visit {VisitId} for place {PlaceName}, ended at {EndedAt}",
                visit.Id, visit.PlaceNameSnapshot, visit.EndedAtUtc);
        }

        if (staleVisits.Count > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return staleVisits.Count;
    }

    /// <summary>
    /// Delete all visit candidates that are older than the stale threshold.
    /// These are candidates that were never confirmed as visits.
    /// </summary>
    /// <returns>Number of candidates deleted.</returns>
    private async Task<int> DeleteStaleCandidatesAsync(DateTime now, ApplicationSettings settings, CancellationToken cancellationToken)
    {
        var cutoff = now.AddMinutes(-settings.VisitedCandidateStaleMinutes);

        var staleCandidates = await _dbContext.PlaceVisitCandidates
            .Where(c => c.LastHitUtc < cutoff)
            .ToListAsync(cancellationToken);

        if (staleCandidates.Count > 0)
        {
            _dbContext.PlaceVisitCandidates.RemoveRange(staleCandidates);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogDebug(
                "Deleted {Count} stale visit candidates",
                staleCandidates.Count);
        }

        return staleCandidates.Count;
    }
}
