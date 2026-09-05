using Quartz;
using Quartz.Impl.Matchers;
using Wayfarer.Jobs;
using Wayfarer.Models.LocationEnrichment;

namespace Wayfarer.Services.LocationEnrichment;

/// <summary>Projects committed workflow intent into stable, one-shot Quartz metadata.</summary>
public sealed class LocationEnrichmentScheduler(IScheduler scheduler)
{
    public static int MisfireInstruction => Quartz.MisfireInstruction.SimpleTrigger.FireNow;
    public const string Group = "LocationEnrichment";
    public static JobKey JobKey(Guid id) => new($"Workflow_{id:N}", Group);
    public static TriggerKey TriggerKey(Guid id, int epoch) => new($"Workflow_{id:N}_{epoch}", Group);

    /// <summary>Idempotently ensures or removes Quartz state after relational commit.</summary>
    public async Task EnsureScheduledAsync(LocationEnrichmentWorkflow workflow, CancellationToken cancellationToken = default)
        => await EnsureScheduledAsync(workflow, null, cancellationToken);

    /// <summary>Projects one workflow using a caller-supplied group snapshot during bounded reconciliation.</summary>
    public async Task EnsureScheduledAsync(LocationEnrichmentWorkflow workflow,
        ISet<TriggerKey>? knownTriggerKeys, CancellationToken cancellationToken = default)
        => await EnsureScheduledAsync(workflow, null, knownTriggerKeys, cancellationToken);

    /// <summary>Projects one workflow using caller-supplied group snapshots during reconciliation.</summary>
    public async Task EnsureScheduledAsync(LocationEnrichmentWorkflow workflow, ISet<JobKey>? knownJobKeys,
        ISet<TriggerKey>? knownTriggerKeys, CancellationToken cancellationToken = default)
    {
        var jobKey = JobKey(workflow.SchedulerId);
        var triggerKey = TriggerKey(workflow.SchedulerId, workflow.Epoch);
        var prefix = $"Workflow_{workflow.SchedulerId:N}_";
        IEnumerable<TriggerKey> triggerKeys = knownTriggerKeys ?? (IEnumerable<TriggerKey>)await scheduler.GetTriggerKeys(
            GroupMatcher<TriggerKey>.GroupEquals(Group), cancellationToken);
        var jobExists = knownJobKeys?.Contains(jobKey) ?? await scheduler.CheckExists(jobKey, cancellationToken);
        if (!jobExists)
        {
            var data = new JobDataMap { ["workflowId"] = workflow.SchedulerId.ToString("N"), ["schema"] = "1" };
            var durable = JobBuilder.Create<LocationEnrichmentJob>().WithIdentity(jobKey)
                .UsingJobData(data).StoreDurably().Build();
            await scheduler.AddJob(durable, false, cancellationToken);
            knownJobKeys?.Add(jobKey);
            jobExists = true;
        }
        if (!workflow.IntentEnabled || workflow.State is LocationEnrichmentState.PausedByUser
            or LocationEnrichmentState.PausedByAuthority or LocationEnrichmentState.Completed
            or LocationEnrichmentState.Cancelled or LocationEnrichmentState.Failed)
        {
            foreach (var live in triggerKeys.Where(item => item.Name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                await scheduler.UnscheduleJob(live, cancellationToken);
                knownTriggerKeys?.Remove(live);
            }
            if (workflow.State is LocationEnrichmentState.PausedByUser or LocationEnrichmentState.Cancelled)
                await scheduler.Interrupt(jobKey, cancellationToken);
            return;
        }

        foreach (var stale in triggerKeys.Where(item => item.Name.StartsWith(prefix, StringComparison.Ordinal)
            && !item.Equals(triggerKey)))
        {
            await scheduler.UnscheduleJob(stale, cancellationToken);
            knownTriggerKeys?.Remove(stale);
        }
        // Quartz stores milliseconds. Round up so persistence cannot move the wake before its authority deadline.
        var wake = new DateTimeOffset(workflow.NextEligibleAtUtc ?? DateTime.UtcNow);
        var scheduledAt = DateTimeOffset.FromUnixTimeMilliseconds(wake.ToUnixTimeMilliseconds());
        if (scheduledAt < wake) scheduledAt = scheduledAt.AddMilliseconds(1);
        var trigger = TriggerBuilder.Create().WithIdentity(triggerKey).ForJob(jobKey)
            .UsingJobData("epoch", workflow.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .StartAt(scheduledAt)
            .WithSimpleSchedule(schedule =>
            {
                schedule.WithRepeatCount(0);
                schedule.WithMisfireHandlingInstructionFireNow();
            }).Build();
        // A continuation keeps its epoch but replaces the consumed or differently timed one-shot wake.
        if (jobExists && (knownTriggerKeys?.Contains(triggerKey)
            ?? await scheduler.CheckExists(triggerKey, cancellationToken)))
        {
            var existing = await scheduler.GetTrigger(triggerKey, cancellationToken);
            if (existing is not null && (workflow.State == LocationEnrichmentState.Running
                || existing.StartTimeUtc == trigger.StartTimeUtc)) return;
            if (await scheduler.RescheduleJob(triggerKey, trigger, cancellationToken) is not null) return;
        }
        await scheduler.ScheduleJob(trigger, cancellationToken);
        knownTriggerKeys?.Add(triggerKey);
    }
}
