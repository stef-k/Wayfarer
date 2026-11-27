using Quartz;
using Wayfarer.Models;
using Wayfarer.Services.TripMetadataReceipts;

namespace Wayfarer.Jobs;

/// <summary>The existing Quartz maintenance scheduler advances Trip retirement even when no history exists.</summary>
[DisallowConcurrentExecution]
public sealed class TripMetadataReceiptMaintenanceJob(ApplicationDbContext db) : IJob
{
    /// <summary>Commits cutoff first so blocked cleanup cannot delay the current retirement boundary.</summary>
    public async Task Execute(IJobExecutionContext context)
    {
        var store = new TripMetadataReceiptStore(db);
        await store.AdvanceCutoffAsync(context.CancellationToken);
        await store.CleanupAsync(context.CancellationToken);
    }

    /// <summary>Installs the one-minute periodic wake using the existing maintenance owner.</summary>
    internal static void Configure(IScheduler scheduler)
    {
        var key = new JobKey(nameof(TripMetadataReceiptMaintenanceJob), "Maintenance");
        if (scheduler.CheckExists(key).GetAwaiter().GetResult()) return;
        var job = JobBuilder.Create<TripMetadataReceiptMaintenanceJob>().WithIdentity(key).StoreDurably().Build();
        var trigger = TriggerBuilder.Create().WithIdentity("TripMetadataReceiptMaintenanceTrigger", "Maintenance").ForJob(key)
            .StartNow().WithSimpleSchedule(schedule => schedule.WithIntervalInMinutes(1).RepeatForever()).Build();
        scheduler.ScheduleJob(job, trigger).GetAwaiter().GetResult();
    }
}
