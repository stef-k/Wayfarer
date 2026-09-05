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

/// <summary>Exercises the production reconciliation seam across authoritative projection states.</summary>
public sealed class LocationEnrichmentRecoveryMatrixTests
{
    public static TheoryData<string> Cases => new()
    {
        "missing-job", "missing-trigger", "stale-trigger", "duplicate-trigger",
        "paused-user", "paused-authority", "completed", "cancelled", "failed",
        "expired-running", "unexpired-running"
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ProjectionConvergesAndSecondReconciliationIsANoOp(string scenario)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var now = DateTime.UtcNow;
        var workflow = CreateWorkflow(scenario, now);
        await using (var seed = new ApplicationDbContext(options, services))
        {
            seed.Add(workflow);
            await seed.SaveChangesAsync();
        }
        var originalEpoch = workflow.Epoch;
        var originalIntent = workflow.IntentEnabled;
        var scheduler = new ProjectionScheduler(workflow, scenario);
        var reconciler = new LocationEnrichmentReconciler(new TestContextFactory(options, services),
            new LocationEnrichmentScheduler(scheduler.Mock.Object), scheduler.Mock.Object);

        await reconciler.ReconcileAsync();

        await using (var verify = new ApplicationDbContext(options, services))
        {
            var final = await verify.LocationEnrichmentWorkflows.SingleAsync();
            Assert.Equal(originalEpoch, final.Epoch);
            Assert.Equal(originalIntent, final.IntentEnabled);
            Assert.Equal(scenario == "expired-running" ? LocationEnrichmentState.Scheduled : workflow.State,
                final.State);
            Assert.Equal(2, final.AdmittedUsageCount);
            Assert.Equal(1, final.EnrichedCount);
            Assert.Equal(3, final.ProcessedCount);
            Assert.Equal(1, final.RetryableDeferredCount);
            Assert.Equal(1, final.PermanentlyDeferredCount);
            if (scenario == "expired-running")
            {
                Assert.Null(final.ExecutionLeaseId);
                Assert.Null(final.ExecutionLeaseExpiresAtUtc);
            }
            if (scenario == "unexpired-running") Assert.NotNull(final.ExecutionLeaseId);
        }
        Assert.Equal([LocationEnrichmentScheduler.JobKey(workflow.SchedulerId)], scheduler.Jobs);
        var shouldRun = workflow.IntentEnabled && workflow.State is not (LocationEnrichmentState.PausedByUser
            or LocationEnrichmentState.PausedByAuthority or LocationEnrichmentState.Completed
            or LocationEnrichmentState.Cancelled or LocationEnrichmentState.Failed)
            || scenario == "expired-running";
        var expectedTriggers = shouldRun
            ? new[] { LocationEnrichmentScheduler.TriggerKey(workflow.SchedulerId, workflow.Epoch) }
            : [];
        Assert.Equal(expectedTriggers, scheduler.Triggers);

        scheduler.Mutations = 0;
        scheduler.MutationLog.Clear();
        await reconciler.ReconcileAsync();
        Assert.True(scheduler.Mutations == 0,
            $"Unexpected second-run mutations: {string.Join(", ", scheduler.MutationLog)}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InFlightAttemptRecoveryHonorsItsDurableDeadline(bool expired)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var now = DateTime.UtcNow;
        var workflow = LocationEnrichmentWorkflow.Create($"attempt-{expired}", now);
        workflow.Start(now.AddMinutes(-3));
        var operationId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();
        var attempt = new LocationEnrichmentAttempt
        {
            UserId = workflow.UserId,
            LocationId = 7,
            ProviderKey = "geoapify",
            AdmittedAttemptCount = 1,
            Outcome = LocationEnrichmentOutcome.None,
            LastAttemptAtUtc = now.AddMinutes(-2),
            NextAttemptAtUtc = expired ? now.AddMinutes(-1) : now.AddMinutes(5),
            OperationId = operationId,
            OperationLeaseId = leaseId,
            OperationFencingGeneration = 3,
            OperationStartedAtUtc = now.AddMinutes(-2),
            OperationWorkflowEpoch = workflow.Epoch,
            OperationAttemptNumber = 1
        };
        await using (var seed = new ApplicationDbContext(options, services))
        { seed.AddRange(workflow, attempt); await seed.SaveChangesAsync(); }
        var scheduler = new ProjectionScheduler(workflow, "missing-job");
        var reconciler = new LocationEnrichmentReconciler(new TestContextFactory(options, services),
            new LocationEnrichmentScheduler(scheduler.Mock.Object), scheduler.Mock.Object);

        await reconciler.ReconcileAsync();

        await using var verify = new ApplicationDbContext(options, services);
        var final = await verify.LocationEnrichmentAttempts.SingleAsync();
        Assert.Equal(expired ? null : operationId, final.OperationId);
        Assert.Equal(expired ? null : leaseId, final.OperationLeaseId);
        Assert.Equal(expired ? null : 3, final.OperationFencingGeneration);
        Assert.Equal(expired ? LocationEnrichmentOutcome.RetryableFailure : LocationEnrichmentOutcome.None,
            final.Outcome);
        scheduler.Mutations = 0;
        scheduler.MutationLog.Clear();
        await reconciler.ReconcileAsync();
        Assert.Equal(0, scheduler.Mutations);
    }

    [Fact]
    public async Task RelationalRecoveryCommitSurvivesProjectionFailureAndNextRunConverges()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var now = DateTime.UtcNow;
        var workflow = LocationEnrichmentWorkflow.Create("projection-failure", now);
        workflow.Start(now.AddMinutes(-3));
        workflow.TryAcquireExecutionLease(now.AddMinutes(-2), TimeSpan.FromMinutes(1));
        await using (var seed = new ApplicationDbContext(options, services))
        { seed.Add(workflow); await seed.SaveChangesAsync(); }
        var scheduler = new ProjectionScheduler(workflow, "missing-job");
        scheduler.Mock.Setup(item => item.AddJob(It.IsAny<IJobDetail>(), false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("projection failed"));
        var reconciler = new LocationEnrichmentReconciler(new TestContextFactory(options, services),
            new LocationEnrichmentScheduler(scheduler.Mock.Object), scheduler.Mock.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => reconciler.ReconcileAsync());

        await using (var verify = new ApplicationDbContext(options, services))
        {
            var committed = await verify.LocationEnrichmentWorkflows.SingleAsync();
            Assert.Equal(LocationEnrichmentState.Scheduled, committed.State);
            Assert.Null(committed.ExecutionLeaseId);
        }
        scheduler.Mock.Setup(item => item.AddJob(It.IsAny<IJobDetail>(), false, It.IsAny<CancellationToken>()))
            .Callback<IJobDetail, bool, CancellationToken>((detail, _, _) => scheduler.Jobs.Add(detail.Key))
            .Returns(Task.CompletedTask);
        await reconciler.ReconcileAsync();
        Assert.Equal([LocationEnrichmentScheduler.JobKey(workflow.SchedulerId)], scheduler.Jobs);
        Assert.Equal([LocationEnrichmentScheduler.TriggerKey(workflow.SchedulerId, workflow.Epoch)],
            scheduler.Triggers);
    }

    private static LocationEnrichmentWorkflow CreateWorkflow(string scenario, DateTime now)
    {
        var workflow = LocationEnrichmentWorkflow.Create($"matrix-{scenario}", now);
        workflow.Start(now.AddMinutes(-3));
        workflow.RecordBatch(3, 1, 1, 1, 2, now);
        switch (scenario)
        {
            case "paused-user": workflow.Pause(now); break;
            case "paused-authority":
                workflow.PauseForAuthority(LocationEnrichmentOutcome.AuthorityUnavailable, now); break;
            case "completed":
                workflow.TransitionToTerminal(LocationEnrichmentState.Completed,
                    LocationEnrichmentOutcome.NoCandidates, now); break;
            case "cancelled": workflow.Cancel(now); break;
            case "failed":
                workflow.TransitionToTerminal(LocationEnrichmentState.Failed,
                    LocationEnrichmentOutcome.DataFailure, now); break;
            case "expired-running": workflow.TryAcquireExecutionLease(now.AddMinutes(-2), TimeSpan.FromMinutes(1)); break;
            case "unexpired-running": workflow.TryAcquireExecutionLease(now, TimeSpan.FromMinutes(5)); break;
        }
        return workflow;
    }

    private sealed class ProjectionScheduler
    {
        internal Mock<IScheduler> Mock { get; } = new();
        internal HashSet<JobKey> Jobs { get; } = [];
        internal HashSet<TriggerKey> Triggers { get; } = [];
        internal int Mutations { get; set; }
        internal List<string> MutationLog { get; } = [];

        internal ProjectionScheduler(LocationEnrichmentWorkflow workflow, string scenario)
        {
            // Retain trigger times so reconciliation can compare the actual projected wake.
            var details = new Dictionary<TriggerKey, ITrigger>();
            var job = LocationEnrichmentScheduler.JobKey(workflow.SchedulerId);
            var current = LocationEnrichmentScheduler.TriggerKey(workflow.SchedulerId, workflow.Epoch);
            var stale = LocationEnrichmentScheduler.TriggerKey(workflow.SchedulerId, Math.Max(0, workflow.Epoch - 1));
            if (scenario != "missing-job") Jobs.Add(job);
            if (scenario is not ("missing-job" or "missing-trigger")) Triggers.Add(current);
            if (scenario is "stale-trigger") { Triggers.Remove(current); Triggers.Add(stale); }
            if (scenario is "duplicate-trigger") Triggers.Add(stale);
            foreach (var key in Triggers)
                details[key] = TriggerBuilder.Create().WithIdentity(key).StartAt(DateTimeOffset.FromUnixTimeMilliseconds(
                    new DateTimeOffset(workflow.NextEligibleAtUtc ?? DateTime.UtcNow).ToUnixTimeMilliseconds() + 1)).Build();
            Mock.Setup(item => item.GetTrigger(It.IsAny<TriggerKey>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((TriggerKey key, CancellationToken _) => details.GetValueOrDefault(key));
            Mock.Setup(item => item.GetJobKeys(It.IsAny<GroupMatcher<JobKey>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Jobs.ToHashSet());
            Mock.Setup(item => item.GetTriggerKeys(It.IsAny<GroupMatcher<TriggerKey>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Triggers.ToHashSet());
            Mock.Setup(item => item.AddJob(It.IsAny<IJobDetail>(), false, It.IsAny<CancellationToken>()))
                .Callback<IJobDetail, bool, CancellationToken>((detail, _, _) =>
                { Jobs.Add(detail.Key); Mutations++; MutationLog.Add($"add:{detail.Key}"); })
                .Returns(Task.CompletedTask);
            Mock.Setup(item => item.ScheduleJob(It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()))
                .Returns<ITrigger, CancellationToken>((trigger, _) =>
                { Triggers.Add(trigger.Key); details[trigger.Key] = trigger; Mutations++; MutationLog.Add($"schedule:{trigger.Key}");
                    return Task.FromResult(DateTimeOffset.UtcNow); });
            Mock.Setup(item => item.UnscheduleJob(It.IsAny<TriggerKey>(), It.IsAny<CancellationToken>()))
                .Returns<TriggerKey, CancellationToken>((key, _) =>
                { var removed = Triggers.Remove(key); if (removed) { Mutations++; MutationLog.Add($"unschedule:{key}"); }
                    return Task.FromResult(removed); });
            Mock.Setup(item => item.Interrupt(It.IsAny<JobKey>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        }
    }

    private sealed class TestContextFactory(DbContextOptions<ApplicationDbContext> options, IServiceProvider services)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options, services);
    }
}
