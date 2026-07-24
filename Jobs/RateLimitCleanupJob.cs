using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Services;

namespace Wayfarer.Jobs;

/// <summary>
/// Quartz job that periodically sweeps expired entries from all in-memory rate limit caches
/// and reconciles the tile cache size counter with the database.
/// Prevents unbounded memory growth from accumulated expired entries that would otherwise
/// only be cleaned when the cache exceeds the 10,000-entry threshold.
/// Runs every 5 minutes. Each cache is cleaned independently.
/// </summary>
public class RateLimitCleanupJob : IJob
{
    private readonly ILogger<RateLimitCleanupJob> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public RateLimitCleanupJob(ILogger<RateLimitCleanupJob> logger, IServiceScopeFactory serviceScopeFactory)
    {
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;
        var jobDataMap = context.JobDetail.JobDataMap;
        jobDataMap["Status"] = "Scheduled";

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            jobDataMap["Status"] = "In Progress";

            var currentTicks = DateTime.UtcNow.Ticks;
            var totalRemoved = 0;

            // Tile anonymous rate limit cache (keyed by IP).
            var before = TilesController.RateLimitCache.Count;
            RateLimitHelper.CleanupExpiredEntries(TilesController.RateLimitCache, currentTicks);
            totalRemoved += before - TilesController.RateLimitCache.Count;

            // Tile authenticated rate limit cache (keyed by user ID).
            before = TilesController.AuthRateLimitCache.Count;
            RateLimitHelper.CleanupExpiredEntries(TilesController.AuthRateLimitCache, currentTicks);
            totalRemoved += before - TilesController.AuthRateLimitCache.Count;

            // Tile per-IP outbound budget cache (keyed by IP).
            before = TilesController.OutboundBudgetCache.Count;
            RateLimitHelper.CleanupExpiredEntries(TilesController.OutboundBudgetCache, currentTicks);
            totalRemoved += before - TilesController.OutboundBudgetCache.Count;

            // Image proxy rate limit cache (keyed by IP).
            before = TripViewerController.RateLimitCache.Count;
            RateLimitHelper.CleanupExpiredEntries(TripViewerController.RateLimitCache, currentTicks);
            totalRemoved += before - TripViewerController.RateLimitCache.Count;

            if (totalRemoved > 0)
            {
                _logger.LogInformation(
                    "RateLimitCleanupJob completed. Removed {RemovedCount} expired entries.", totalRemoved);
            }

            // Reconcile tile cache size counter with database to correct accumulated drift
            // from non-atomic size tracking during concurrent eviction/caching operations.
            try
            {
                await TileCacheService.ReconcileCacheSizeAsync(_serviceScopeFactory);
                using var scope = _serviceScopeFactory.CreateScope();
                var tileCacheService = scope.ServiceProvider.GetRequiredService<TileCacheService>();
                var retiredLegacyEntries =
                    await tileCacheService.RetireLegacyCacheBatchAsync(cancellationToken);
                if (retiredLegacyEntries > 0)
                {
                    _logger.LogInformation(
                        "Retired {RetiredCount} quarantined legacy tile entries.",
                        retiredLegacyEntries);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tile cache size reconciliation failed (non-critical)");
            }

            jobDataMap["Status"] = "Completed";
            jobDataMap["StatusMessage"] = $"Removed {totalRemoved} expired entries";
        }
        catch (OperationCanceledException)
        {
            jobDataMap["Status"] = "Cancelled";
            _logger.LogInformation("RateLimitCleanupJob was cancelled.");
        }
        catch (Exception ex)
        {
            jobDataMap["Status"] = "Failed";
            _logger.LogError(ex, "Error executing RateLimitCleanupJob");
        }
    }
}
