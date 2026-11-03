using Quartz;
using Wayfarer.Services;

namespace Wayfarer.Jobs;

/// <summary>Startup/five-minute durable orphan recovery through the existing scheduler.</summary>
[DisallowConcurrentExecution]
public sealed class ProfilePhotoCleanupJob(ProfilePhotoService photos) : IJob
{
    public Task Execute(IJobExecutionContext context) => photos.SweepAsync(context.CancellationToken);

    /// <summary>Idempotently register the existing maintenance pattern; no separate cleanup queue.</summary>
    internal static void Configure(IScheduler scheduler)
    {
        var key = new JobKey("ProfilePhotoCleanupJob", "Maintenance");
        if (scheduler.CheckExists(key).GetAwaiter().GetResult())
        {
            scheduler.TriggerJob(key).GetAwaiter().GetResult();
            return;
        }
        var job = JobBuilder.Create<ProfilePhotoCleanupJob>().WithIdentity(key).StoreDurably().Build();
        var trigger = TriggerBuilder.Create().WithIdentity("ProfilePhotoCleanupTrigger", "Maintenance").ForJob(key)
            .StartNow().WithSimpleSchedule(s => s.WithIntervalInMinutes(5).RepeatForever()).Build();
        scheduler.ScheduleJob(job, trigger).GetAwaiter().GetResult();
    }
}
