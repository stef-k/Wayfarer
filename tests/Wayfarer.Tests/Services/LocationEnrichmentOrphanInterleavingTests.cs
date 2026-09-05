using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Quartz;
using Quartz.Impl.Matchers;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationEnrichment;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves deterministic orphan deletion and concurrent authority creation interleavings.</summary>
public sealed class LocationEnrichmentOrphanInterleavingTests
{
    public static TheoryData<string> Cases => new()
    {
        "exists-before-check", "create-before-delete", "create-and-project-before-delete",
        "create-after-delete", "authority-changes-during-repair", "remains-absent"
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OrphanRaceConvergesWithoutStrandingCurrentAuthority(string scenario)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var schedulerId = Guid.NewGuid();
        var jobKey = LocationEnrichmentScheduler.JobKey(schedulerId);
        var jobs = new HashSet<JobKey> { jobKey };
        var triggers = new HashSet<TriggerKey>
            { LocationEnrichmentScheduler.TriggerKey(schedulerId, 0) };
        var events = new List<string>();
        var mutations = 0;
        if (scenario == "exists-before-check")
            await CreateWorkflowAsync(options, services, schedulerId, "existing");
        var quartz = new Mock<IScheduler>();
        // Model persisted trigger times as well as identities for the projection comparison.
        var details = new Dictionary<TriggerKey, ITrigger>();
        quartz.Setup(item => item.GetTrigger(It.IsAny<TriggerKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TriggerKey key, CancellationToken _) => details.GetValueOrDefault(key));
        quartz.Setup(item => item.GetJobKeys(It.IsAny<GroupMatcher<JobKey>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => jobs.ToHashSet());
        quartz.Setup(item => item.GetTriggerKeys(It.IsAny<GroupMatcher<TriggerKey>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => triggers.ToHashSet());
        quartz.Setup(item => item.DeleteJob(jobKey, It.IsAny<CancellationToken>()))
            .Returns<JobKey, CancellationToken>(async (_, _) =>
            {
                events.Add("delete-entered");
                if (scenario is "create-before-delete" or "create-and-project-before-delete"
                    or "authority-changes-during-repair")
                {
                    await CreateWorkflowAsync(options, services, schedulerId, scenario);
                    events.Add("workflow-created-before-delete");
                    if (scenario == "create-and-project-before-delete")
                    {
                        jobs.Add(jobKey);
                        triggers.Add(LocationEnrichmentScheduler.TriggerKey(schedulerId, 1));
                        events.Add("valid-projection-created");
                    }
                }
                var removed = jobs.Remove(jobKey);
                triggers.Clear();
                mutations++;
                events.Add("delete-finished");
                if (scenario == "create-after-delete")
                {
                    await CreateWorkflowAsync(options, services, schedulerId, scenario);
                    events.Add("workflow-created-after-delete");
                }
                return removed;
            });
        quartz.Setup(item => item.AddJob(It.IsAny<IJobDetail>(), false, It.IsAny<CancellationToken>()))
            .Callback<IJobDetail, bool, CancellationToken>((job, _, _) =>
            { jobs.Add(job.Key); mutations++; events.Add("repair-job"); })
            .Returns(Task.CompletedTask);
        quartz.Setup(item => item.ScheduleJob(It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()))
            .Returns<ITrigger, CancellationToken>(async (trigger, _) =>
            {
                triggers.Add(trigger.Key);
                details[trigger.Key] = trigger;
                mutations++;
                events.Add("repair-trigger");
                if (scenario == "authority-changes-during-repair")
                {
                    await using var command = new ApplicationDbContext(options, services);
                    (await command.LocationEnrichmentWorkflows.SingleAsync()).Pause(DateTime.UtcNow);
                    await command.SaveChangesAsync();
                    events.Add("authority-paused");
                }
                return DateTimeOffset.UtcNow;
            });
        quartz.Setup(item => item.UnscheduleJob(It.IsAny<TriggerKey>(), It.IsAny<CancellationToken>()))
            .Returns<TriggerKey, CancellationToken>((key, _) =>
            { var removed = triggers.Remove(key); if (removed) mutations++; return Task.FromResult(removed); });
        quartz.Setup(item => item.Interrupt(It.IsAny<JobKey>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var reconciler = new LocationEnrichmentReconciler(new TestContextFactory(options, services),
            new LocationEnrichmentScheduler(quartz.Object), quartz.Object);

        await reconciler.ReconcileAsync();

        await using var verify = new ApplicationDbContext(options, services);
        var authority = await verify.LocationEnrichmentWorkflows.SingleOrDefaultAsync();
        if (scenario == "remains-absent")
        {
            Assert.Null(authority);
            Assert.Empty(jobs);
            Assert.Empty(triggers);
        }
        else
        {
            Assert.NotNull(authority);
            Assert.Equal([jobKey], jobs);
            var expected = authority.State == LocationEnrichmentState.PausedByUser
                ? [] : new[] { LocationEnrichmentScheduler.TriggerKey(schedulerId, authority.Epoch) };
            Assert.Equal(expected, triggers);
        }
        if (scenario == "create-and-project-before-delete")
            Assert.True(events.IndexOf("valid-projection-created") < events.IndexOf("delete-finished"));
        if (scenario == "create-after-delete")
            Assert.True(events.IndexOf("delete-finished") < events.IndexOf("workflow-created-after-delete"));

        mutations = 0;
        await reconciler.ReconcileAsync();
        Assert.Equal(0, mutations);
    }

    private static async Task CreateWorkflowAsync(DbContextOptions<ApplicationDbContext> options,
        IServiceProvider services, Guid schedulerId, string suffix)
    {
        await using var command = new ApplicationDbContext(options, services);
        var workflow = LocationEnrichmentWorkflow.Create($"orphan-{suffix}", DateTime.UtcNow);
        typeof(LocationEnrichmentWorkflow).GetProperty(nameof(LocationEnrichmentWorkflow.SchedulerId))!
            .SetValue(workflow, schedulerId);
        workflow.Start(DateTime.UtcNow);
        command.Add(workflow);
        await command.SaveChangesAsync();
    }

    private sealed class TestContextFactory(DbContextOptions<ApplicationDbContext> options, IServiceProvider services)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options, services);
    }
}
