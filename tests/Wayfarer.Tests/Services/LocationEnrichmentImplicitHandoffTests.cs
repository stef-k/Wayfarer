using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationProviders;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves implicit opt-in preserves holds, deadlines and durable continuation dispositions.</summary>
public sealed class LocationEnrichmentImplicitHandoffTests
{
    [Theory]
    [InlineData(LocationEnrichmentState.PausedByUser, false)]
    [InlineData(LocationEnrichmentState.Cancelled, false)]
    [InlineData(LocationEnrichmentState.Failed, false)]
    [InlineData(LocationEnrichmentState.PausedByAuthority, false)]
    [InlineData(LocationEnrichmentState.PausedByUser, true)]
    [InlineData(LocationEnrichmentState.Cancelled, true)]
    public async Task DelayedOptInPreservesExplicitHoldEvenWithoutCredentials(LocationEnrichmentState state, bool available)
    {
        await using var scope = new Scenario();
        var workflow = scope.Workflow(state);
        await scope.Db.SaveChangesAsync();
        var before = (workflow.Epoch, workflow.State, workflow.Outcome, workflow.IntentEnabled, workflow.AdmittedUsageCount);
        scope.Inspect(available: available);

        Assert.Equal(EnrichmentHandoffDisposition.Held, await scope.Handoff.EnsureAsync("user"));
        Assert.Equal(before, (workflow.Epoch, workflow.State, workflow.Outcome, workflow.IntentEnabled, workflow.AdmittedUsageCount));
    }

    [Theory]
    [InlineData(LocationEnrichmentState.PausedByBudget)]
    [InlineData(LocationEnrichmentState.BackingOff)]
    public async Task ExistingDeadlineSurvivesRepeatedOptInAndMissingCredentials(LocationEnrichmentState state)
    {
        await using var scope = new Scenario();
        var workflow = scope.Workflow(state);
        var deadline = workflow.NextEligibleAtUtc;
        await scope.Db.SaveChangesAsync();
        scope.Inspect(available: false);

        Assert.Equal(EnrichmentHandoffDisposition.ContinuationAccepted, await scope.Handoff.EnsureAsync("user"));
        Assert.Equal(EnrichmentHandoffDisposition.ContinuationAccepted, await scope.Handoff.EnsureAsync("user"));
        Assert.Equal(deadline, workflow.NextEligibleAtUtc);
        Assert.Equal(state, workflow.State);
        Assert.Equal(7, workflow.AdmittedUsageCount);
    }

    [Theory]
    [InlineData("geoapify")]
    [InlineData("mapbox")]
    public async Task InitialExhaustionIsBudgetWaitingWithExistingPolicyWake(string provider)
    {
        await using var scope = new Scenario();
        scope.Inspect(provider, exhausted: true);
        scope.Progress.Setup(x => x.ProjectAsync("user", It.IsAny<PersonalProviderAuthorityBinding>(),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(new LocationEnrichmentProgressPresentation(1, 0, 0, 0, null));

        Assert.Equal(EnrichmentHandoffDisposition.ContinuationAccepted, await scope.Handoff.EnsureAsync("user"));
        var workflow = await scope.Db.LocationEnrichmentWorkflows.SingleAsync();
        var wake = provider == "mapbox" ? LocationEnrichmentRetryPolicy.MapboxWake(new(scope.Now)).UtcDateTime
            : scope.Now.AddHours(2).AddSeconds(5);
        Assert.Equal(LocationEnrichmentState.PausedByBudget, workflow.State);
        Assert.Equal(LocationEnrichmentOutcome.BudgetExhausted, workflow.Outcome);
        Assert.True(workflow.IntentEnabled);
        Assert.Equal(wake, workflow.NextEligibleAtUtc);
        await scope.Handoff.EnsureAsync("user");
        Assert.Equal(wake, workflow.NextEligibleAtUtc);
    }

    [Fact]
    public async Task BudgetSnapshotBeforeMonthRolloverDoesNotSkipJustResetCycle()
    {
        await using var scope = new Scenario();
        var cycle = new DateTime(scope.Now.Year, scope.Now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var snapshot = cycle.AddTicks(-1);
        var binding = new PersonalProviderAuthorityBinding("mapbox", Guid.NewGuid(), 1, 1, 1,
            PersonalProviderVerification.Verified, 1, 1, null, null, null);
        scope.Status.Setup(x => x.InspectPersistentGeocodingAsync("user", It.IsAny<CancellationToken>())).ReturnsAsync(
            new PersonalProviderInspection(PersonalProviderAdmissionCategory.Admitted, "mapbox", true, true,
                cycle, null, binding, snapshot));
        scope.Progress.Setup(x => x.ProjectAsync("user", binding, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocationEnrichmentProgressPresentation(1, 0, 0, 0, null));
        await scope.Handoff.EnsureAsync("user");
        var workflow = await scope.Db.LocationEnrichmentWorkflows.SingleAsync();
        Assert.Equal(cycle.AddSeconds(5), workflow.NextEligibleAtUtc);
    }

    [Fact]
    public async Task RunningAndProjectionUncertaintyNeverAcknowledgeCandidate()
    {
        await using var scope = new Scenario();
        var workflow = scope.Workflow(LocationEnrichmentState.Running);
        await scope.Db.SaveChangesAsync();
        Assert.Equal(EnrichmentHandoffDisposition.Pending, await scope.Handoff.EnsureAsync("user"));
        workflow.TryReleaseExecutionLease(workflow.ExecutionLeaseId!.Value, workflow.ExecutionFencingGeneration);
        workflow.ContinueAs(LocationEnrichmentState.Scheduled, LocationEnrichmentOutcome.None, scope.Now, scope.Now);
        await scope.Db.SaveChangesAsync();
        scope.Projection.Setup(x => x.ProjectAsync("user", It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());
        Assert.Equal(EnrichmentHandoffDisposition.Pending, await scope.Handoff.EnsureAsync("user"));
    }

    [Theory]
    [InlineData(0, 0, EnrichmentHandoffDisposition.NoOutstandingWork)]
    [InlineData(1, 0, EnrichmentHandoffDisposition.ContinuationAccepted)]
    [InlineData(0, 1, EnrichmentHandoffDisposition.Pending)]
    public async Task FreshCompletionCheckDistinguishesFutureRetryAndRecovery(int future, int recovery, EnrichmentHandoffDisposition expected)
    {
        await using var scope = new Scenario();
        scope.Workflow(LocationEnrichmentState.Completed);
        await scope.Db.SaveChangesAsync();
        scope.Progress.Setup(x => x.ProjectAsync("user", It.IsAny<PersonalProviderAuthorityBinding>(),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(new LocationEnrichmentProgressPresentation(
                0, future, 0, 0, future > 0 ? scope.Now.AddHours(1) : null, AwaitingRecovery: recovery));
        Assert.Equal(expected, await scope.Handoff.EnsureAsync("user"));
    }

    /// <summary>Small shared-owner fixture with no provider transport or scheduler implementation.</summary>
    private sealed class Scenario : IAsyncDisposable
    {
        internal DateTime Now { get; } = DateTime.UtcNow;
        internal ApplicationDbContext Db { get; }
        internal Mock<IPersonalProviderStatusReader> Status { get; } = new();
        internal Mock<ILocationEnrichmentProgressQuery> Progress { get; } = new();
        internal Mock<IWorkflowScheduleProjection> Projection { get; } = new();
        internal ImportEnrichmentHandoff Handoff => new(Db, Projection.Object, Status.Object, Progress.Object);

        internal Scenario()
        {
            Db = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
                new ServiceCollection().BuildServiceProvider());
            Inspect();
        }

        internal void Inspect(string provider = "geoapify", bool available = true, bool exhausted = false)
        {
            var binding = available ? new PersonalProviderAuthorityBinding(provider, Guid.NewGuid(), 1, 1, 1,
                PersonalProviderVerification.Verified, 1, 1, null, null, null) : null;
            Status.Setup(x => x.InspectPersistentGeocodingAsync("user", It.IsAny<CancellationToken>())).ReturnsAsync(
                new PersonalProviderInspection(available ? PersonalProviderAdmissionCategory.Admitted
                    : PersonalProviderAdmissionCategory.CredentialUnavailable, provider, true, exhausted,
                    exhausted ? Now.AddHours(2).AddSeconds(5) : null, null, binding, Now));
        }

        internal LocationEnrichmentWorkflow Workflow(LocationEnrichmentState state)
        {
            var workflow = LocationEnrichmentWorkflow.Create("user", Now);
            workflow.Start(Now);
            workflow.RecordBatch(8, 1, 0, 0, 7, Now);
            if (state == LocationEnrichmentState.PausedByUser) workflow.Pause(Now);
            else if (state == LocationEnrichmentState.Cancelled) workflow.Cancel(Now);
            else if (state is LocationEnrichmentState.Completed or LocationEnrichmentState.Failed)
                workflow.TransitionToTerminal(state, LocationEnrichmentOutcome.DataFailure, Now);
            else if (state == LocationEnrichmentState.PausedByAuthority)
                workflow.PauseForAuthority(LocationEnrichmentOutcome.AuthorityUnavailable, Now);
            else if (state == LocationEnrichmentState.Running) workflow.TryAcquireExecutionLease(Now, TimeSpan.FromMinutes(1));
            else workflow.ContinueAs(state, LocationEnrichmentOutcome.BudgetExhausted, Now.AddHours(1), Now);
            Db.Add(workflow);
            return workflow;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
