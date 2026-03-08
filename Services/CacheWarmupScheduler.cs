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
    /// When <paramref name="immediate"/> is <c>false</c> (default), the job fires
    /// 1 minute after the last call, implementing debounce so rapid edits only
    /// trigger a single warm-up pass.
    /// When <paramref name="immediate"/> is <c>true</c> and no trigger exists yet,
    /// the job fires after a short 5-second delay for near-instant caching of
    /// newly introduced images. If a trigger already exists (active editing session),
    /// the 1-minute debounce is always used regardless of this flag.
    /// </summary>
    /// <param name="tripId">The trip whose images should be pre-cached.</param>
    /// <param name="immediate">
    /// When <c>true</c> and no existing trigger, uses a 5-second delay instead of 1 minute.
    /// </param>
    Task ScheduleWarmupAsync(Guid tripId, bool immediate = false);
}

/// <summary>
/// Schedules debounced <see cref="CacheWarmupJob"/> instances via Quartz.
/// Uses a per-trip trigger identity so repeated calls within 5 minutes
/// reschedule instead of creating duplicate jobs.
/// </summary>
public class CacheWarmupScheduler : ICacheWarmupScheduler
{
    /// <summary>
    /// Default delay before the warm-up job fires, providing debounce for rapid edits.
    /// </summary>
    private static readonly TimeSpan WarmupDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Short delay used when images are newly introduced and no trigger exists yet,
    /// providing near-instant caching while still batching near-simultaneous saves.
    /// </summary>
    private static readonly TimeSpan ImmediateDelay = TimeSpan.FromSeconds(5);

    private readonly IScheduler _scheduler;
    private readonly ILogger<CacheWarmupScheduler> _logger;

    public CacheWarmupScheduler(IScheduler scheduler, ILogger<CacheWarmupScheduler> logger)
    {
        _scheduler = scheduler;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ScheduleWarmupAsync(Guid tripId, bool immediate = false)
    {
        var triggerKey = new TriggerKey($"CacheWarmup-{tripId}", "CacheWarmup");
        var jobKey = new JobKey($"CacheWarmupJob-{tripId}", "CacheWarmup");

        try
        {
            var exists = await _scheduler.CheckExists(triggerKey);

            if (exists)
            {
                // Reschedule existing trigger (debounce) — always use WarmupDelay
                // regardless of immediate flag to avoid disrupting active editing sessions.
                var newTrigger = TriggerBuilder.Create()
                    .WithIdentity(triggerKey)
                    .ForJob(jobKey)
                    .StartAt(DateTimeOffset.UtcNow.Add(WarmupDelay))
                    .Build();

                await _scheduler.RescheduleJob(triggerKey, newTrigger);
                _logger.LogDebug("Rescheduled cache warm-up for trip {TripId} (debounce, {Delay}s).",
                    tripId, WarmupDelay.TotalSeconds);
            }
            else
            {
                // No existing trigger — use short delay when images are newly introduced,
                // otherwise standard debounce delay.
                var delay = immediate ? ImmediateDelay : WarmupDelay;

                var job = JobBuilder.Create<CacheWarmupJob>()
                    .WithIdentity(jobKey)
                    .UsingJobData("tripId", tripId.ToString())
                    .Build();

                var trigger = TriggerBuilder.Create()
                    .WithIdentity(triggerKey)
                    .ForJob(job)
                    .StartAt(DateTimeOffset.UtcNow.Add(delay))
                    .Build();

                try
                {
                    await _scheduler.ScheduleJob(job, trigger);
                    _logger.LogInformation(
                        "Scheduled cache warm-up for trip {TripId} in {Delay}s (immediate={Immediate}).",
                        tripId, delay.TotalSeconds, immediate);
                }
                catch (ObjectAlreadyExistsException)
                {
                    // TOCTOU race: another request created the trigger between CheckExists and ScheduleJob.
                    // Fall back to reschedule (debounce) with standard delay.
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
