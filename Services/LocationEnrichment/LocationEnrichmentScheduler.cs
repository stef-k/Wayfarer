using Quartz;
using Quartz.Impl.Matchers;
using Wayfarer.Jobs;
using Wayfarer.Models.LocationEnrichment;

namespace Wayfarer.Services.LocationEnrichment;

/// <summary>Projects committed workflow intent into stable, one-shot Quartz metadata.</summary>
public sealed class LocationEnrichmentScheduler(IScheduler scheduler)
{
    public const string Group = "LocationEnrichment";
    public static JobKey JobKey(Guid id) => new($"Workflow_{id:N}", Group);
    public static TriggerKey TriggerKey(Guid id, int epoch) => new($"Workflow_{id:N}_{epoch}", Group);

    /// <summary>Idempotently ensures or removes Quartz state after relational commit.</summary>
    public async Task EnsureScheduledAsync(LocationEnrichmentWorkflow workflow, CancellationToken cancellationToken = default)
    {
        var jobKey = JobKey(workflow.SchedulerId);
        if (!workflow.IntentEnabled || workflow.State is LocationEnrichmentState.PausedByUser
            or LocationEnrichmentState.PausedByAuthority or LocationEnrichmentState.Completed
            or LocationEnrichmentState.Cancelled or LocationEnrichmentState.Failed)
        {
            if (await scheduler.CheckExists(jobKey, cancellationToken))
                await scheduler.DeleteJob(jobKey, cancellationToken);
            return;
        }

        var triggerKey = TriggerKey(workflow.SchedulerId, workflow.Epoch);
        var prefix = $"Workflow_{workflow.SchedulerId:N}_";
        var triggerKeys = await scheduler.GetTriggerKeys(GroupMatcher<TriggerKey>.GroupEquals(Group), cancellationToken);
        foreach (var stale in triggerKeys.Where(item => item.Name.StartsWith(prefix, StringComparison.Ordinal)
            && item != triggerKey))
            await scheduler.UnscheduleJob(stale, cancellationToken);
        var jobExists = await scheduler.CheckExists(jobKey, cancellationToken);
        if (jobExists && await scheduler.CheckExists(triggerKey, cancellationToken)) return;
        var data = new JobDataMap { ["workflowId"] = workflow.SchedulerId.ToString("N"), ["schema"] = "1" };
        var job = JobBuilder.Create<LocationEnrichmentJob>().WithIdentity(jobKey).UsingJobData(data).StoreDurably().Build();
        var trigger = TriggerBuilder.Create().WithIdentity(triggerKey).ForJob(jobKey)
            .UsingJobData("epoch", workflow.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .StartAt(workflow.NextEligibleAtUtc ?? DateTime.UtcNow)
            .WithSimpleSchedule(schedule => schedule.WithRepeatCount(0)
                .WithMisfireHandlingInstructionNextWithRemainingCount()).Build();
        if (!jobExists) await scheduler.ScheduleJob(job, trigger, cancellationToken);
        else await scheduler.ScheduleJob(trigger, cancellationToken);
    }
}
