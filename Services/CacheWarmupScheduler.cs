using Quartz;
using Wayfarer.Jobs;

namespace Wayfarer.Services;

/// <summary>
/// Contract for scheduling debounced cache warm-up jobs for trips.
/// </summary>
public interface ICacheWarmupScheduler
{
    /// <summary>
    /// Schedules (or reschedules) a cache warm-up job for the given trip.
    /// The job fires 5 minutes after the last call, implementing debounce
    /// so rapid edits only trigger a single warm-up pass.
    /// </summary>
    /// <param name="tripId">The trip whose images should be pre-cached.</param>
    Task ScheduleWarmupAsync(Guid tripId);
}

/// <summary>
/// Schedules debounced <see cref="CacheWarmupJob"/> instances via Quartz.
/// Uses a per-trip trigger identity so repeated calls within 5 minutes
/// reschedule instead of creating duplicate jobs.
/// </summary>
public class CacheWarmupScheduler : ICacheWarmupScheduler
{
    /// <summary>
    /// Delay before the warm-up job fires, providing debounce for rapid edits.
    /// </summary>
    private static readonly TimeSpan WarmupDelay = TimeSpan.FromMinutes(5);

    private readonly IScheduler _scheduler;
    private readonly ILogger<CacheWarmupScheduler> _logger;

    public CacheWarmupScheduler(IScheduler scheduler, ILogger<CacheWarmupScheduler> logger)
    {
        _scheduler = scheduler;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ScheduleWarmupAsync(Guid tripId)
    {
        var triggerKey = new TriggerKey($"CacheWarmup-{tripId}", "CacheWarmup");
        var jobKey = new JobKey($"CacheWarmupJob-{tripId}", "CacheWarmup");

        try
        {
            var exists = await _scheduler.CheckExists(triggerKey);

            if (exists)
            {
                // Reschedule existing trigger (debounce)
                var newTrigger = TriggerBuilder.Create()
                    .WithIdentity(triggerKey)
                    .ForJob(jobKey)
                    .StartAt(DateTimeOffset.UtcNow.Add(WarmupDelay))
                    .Build();

                await _scheduler.RescheduleJob(triggerKey, newTrigger);
                _logger.LogDebug("Rescheduled cache warm-up for trip {TripId} (debounce).", tripId);
            }
            else
            {
                // Create new job + trigger
                var job = JobBuilder.Create<CacheWarmupJob>()
                    .WithIdentity(jobKey)
                    .UsingJobData("tripId", tripId.ToString())
                    .Build();

                var trigger = TriggerBuilder.Create()
                    .WithIdentity(triggerKey)
                    .ForJob(job)
                    .StartAt(DateTimeOffset.UtcNow.Add(WarmupDelay))
                    .Build();

                try
                {
                    await _scheduler.ScheduleJob(job, trigger);
                    _logger.LogInformation("Scheduled cache warm-up for trip {TripId} in {Delay} minutes.", tripId, WarmupDelay.TotalMinutes);
                }
                catch (ObjectAlreadyExistsException)
                {
                    // TOCTOU race: another request created the trigger between CheckExists and ScheduleJob.
                    // Fall back to reschedule (debounce).
                    var rescheduleTrigger = TriggerBuilder.Create()
                        .WithIdentity(triggerKey)
                        .ForJob(jobKey)
                        .StartAt(DateTimeOffset.UtcNow.Add(WarmupDelay))
                        .Build();
                    await _scheduler.RescheduleJob(triggerKey, rescheduleTrigger);
                    _logger.LogDebug("Rescheduled cache warm-up for trip {TripId} (concurrent race resolved).", tripId);
                }
            }
        }
        catch (Exception ex)
        {
            // Non-fatal — warm-up is best-effort; images will still be cached on first access
            _logger.LogWarning(ex, "Failed to schedule cache warm-up for trip {TripId}.", tripId);
        }
    }
}
