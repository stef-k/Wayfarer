using Quartz;
using Wayfarer.Models;
using Wayfarer.Services.LocationMutations;

namespace Wayfarer.Jobs;

/// <summary>Existing Quartz maintenance infrastructure owns finite receipt cleanup and one bounded catch-up trigger.</summary>
[DisallowConcurrentExecution, PersistJobDataAfterExecution]
public sealed class LocationMutationCleanupJob(ApplicationDbContext db) : IJob
{
    private static readonly JobKey Key = new("LocationMutationCleanupJob", "Maintenance");
    private static readonly TriggerKey FollowUp = new("LocationMutationCleanupFollowUp", "Maintenance");

    /// <summary>Preserves existing job status/history reporting without promising a physical reclamation deadline.</summary>
    public async Task Execute(IJobExecutionContext context)
    {
        var data = context.JobDetail.JobDataMap;
        data["Status"] = "In Progress";
        try
        {
            LocationMutationCleanupCursor? cursor = null;
            if (DateTime.TryParse(data.GetString("CursorTime"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var time)
                && long.TryParse(data.GetString("CursorId"), out var id)) cursor = new(time, data.GetString("CursorOwner") ?? "", id);
            var result = await new LocationMutationCleanup(db).RunAsync(context.CancellationToken, cursor);
            data["CursorTime"] = result.Cursor?.RetainUntilUtc.ToString("O") ?? "";
            data["CursorOwner"] = result.Cursor?.Owner ?? "";
            data["CursorId"] = result.Cursor?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            if (result.FollowUp && !await context.Scheduler.CheckExists(FollowUp, context.CancellationToken))
                await context.Scheduler.ScheduleJob(TriggerBuilder.Create().WithIdentity(FollowUp).ForJob(Key)
                    .StartAt(DateTimeOffset.UtcNow.AddMinutes(1)).Build(), context.CancellationToken);
            data["Status"] = "Completed";
            data["StatusMessage"] = $"Deleted {result.Deleted} eligible Location receipts";
        }
        catch (OperationCanceledException) { data["Status"] = "Cancelled"; throw; }
        catch { data["Status"] = "Failed"; throw; }
    }

    /// <summary>Registers one nonconcurrent job and its five-minute periodic wake with the existing scheduler.</summary>
    internal static void Configure(IScheduler scheduler)
    {
        if (scheduler.CheckExists(Key).GetAwaiter().GetResult()) return;
        var job = JobBuilder.Create<LocationMutationCleanupJob>().WithIdentity(Key).StoreDurably().Build();
        var trigger = TriggerBuilder.Create().WithIdentity("LocationMutationCleanupTrigger", "Maintenance").ForJob(Key)
            .StartNow().WithSimpleSchedule(schedule => schedule.WithIntervalInMinutes(5).RepeatForever()).Build();
        scheduler.ScheduleJob(job, trigger).GetAwaiter().GetResult();
    }
}
