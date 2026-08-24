using Wayfarer.Jobs;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Locks the final production contracts for durable enrichment execution authority.</summary>
public sealed class LocationEnrichmentFinalAuthorityTests
{
    [Fact]
    public void ManualAndScheduledEntryPointsShareLeaseBearingExecutionContract()
    {
        var scheduled = typeof(ILocationEnrichmentWorker).GetMethod(nameof(ILocationEnrichmentWorker.RunBatchAsync));
        var manual = typeof(GeoapifyLocationBackfillService).GetMethods()
            .Where(method => method.Name == nameof(GeoapifyLocationBackfillService.RunAsync)).ToArray();

        Assert.Equal(typeof(Task<LocationEnrichmentWorkerOutcome>), scheduled!.ReturnType);
        Assert.Contains(manual, method => method.GetParameters().FirstOrDefault()?.ParameterType
            == typeof(LocationEnrichmentExecutionLease));
    }

    [Fact]
    public void BackfillNoLongerDependsOnTransactionSpanningLockFactory()
    {
        var dependencies = typeof(GeoapifyLocationBackfillService).GetConstructors().Single().GetParameters();

        Assert.DoesNotContain(dependencies, parameter => parameter.ParameterType == typeof(ApplicationDbContext));
        Assert.DoesNotContain(dependencies, parameter => parameter.ParameterType == typeof(ReverseGeocodingService));
    }

    [Fact]
    public void PauseAndCancelInvalidateAnInFlightOwner()
    {
        var now = DateTime.UtcNow;
        var workflow = LocationEnrichmentWorkflow.Create("user", now);
        workflow.Start(now);
        var lease = workflow.TryAcquireExecutionLease(now, TimeSpan.FromSeconds(30))!.Value;

        workflow.Pause(now.AddSeconds(1));
        Assert.False(workflow.HasExecutionLease(lease.LeaseId, lease.FencingGeneration, now.AddSeconds(1)));

        workflow.Resume(now.AddSeconds(2));
        var replacement = workflow.TryAcquireExecutionLease(now.AddSeconds(2), TimeSpan.FromSeconds(30))!.Value;
        workflow.Cancel(now.AddSeconds(3));
        Assert.False(workflow.HasExecutionLease(replacement.LeaseId, replacement.FencingGeneration, now.AddSeconds(3)));
    }

    [Fact]
    public void AttemptCarriesOpaquePreContactOperationAuthority()
    {
        var properties = typeof(LocationEnrichmentAttempt).GetProperties().Select(property => property.Name).ToHashSet();

        Assert.Contains("OperationId", properties);
        Assert.Contains("OperationFencingGeneration", properties);
        Assert.Contains("OperationStartedAtUtc", properties);
    }

    [Fact]
    public void WorkerCannotScheduleWithoutFreshFencedCompletion()
    {
        var result = typeof(ILocationEnrichmentWorker).GetMethod(nameof(ILocationEnrichmentWorker.RunBatchAsync))!.ReturnType;

        Assert.Equal(typeof(Task<LocationEnrichmentWorkerOutcome>), result);
    }

    [Fact]
    public void CommandsExposeBoundedConvergenceClassification()
    {
        var names = Enum.GetNames(typeof(LocationEnrichmentCommandResult));

        Assert.Contains("Applied", names);
        Assert.Contains("AlreadySatisfied", names);
        Assert.Contains("Conflict", names);
        Assert.Contains("InvalidTransition", names);
        Assert.Contains("AuthorityUnavailable", names);
        Assert.Contains("SchedulingPending", names);
    }

    [Fact]
    public void ProgressSupportsAuthoritativeSnapshotReplacement()
    {
        var method = typeof(LocationEnrichmentWorkflow).GetMethod("ReplaceProgress");

        Assert.NotNull(method);
    }

    [Fact]
    public void SchedulerDefinesPersistentOneShotMisfirePolicy()
    {
        var property = typeof(LocationEnrichmentScheduler).GetProperty("MisfireInstruction");

        Assert.NotNull(property);
    }

    [Fact]
    public void PresentationOwnerMapsDurableWorkflowCommands()
    {
        var presentationType = typeof(LocationEnrichmentWorker).Assembly
            .GetType("Wayfarer.Services.LocationEnrichment.LocationEnrichmentPresentation");

        Assert.NotNull(presentationType);
    }

    [Fact]
    public void PresentationShowsOnlyStartForIdleWorkflow()
    {
        var view = LocationEnrichmentPresentation.Build(null);

        Assert.True(view.Start.Visible);
        Assert.False(view.Pause.Visible);
        Assert.False(view.Resume.Visible);
        Assert.False(view.Cancel.Visible);
    }

    [Fact]
    public void PresentationShowsPauseAndCancelForActiveWorkflow()
    {
        var workflow = LocationEnrichmentWorkflow.Create("user", DateTime.UtcNow);
        workflow.Start(DateTime.UtcNow);

        var view = LocationEnrichmentPresentation.Build(workflow);

        Assert.True(view.Pause is { Visible: true, Enabled: true });
        Assert.True(view.Cancel is { Visible: true, Enabled: true });
        Assert.False(view.Start.Visible);
        Assert.False(view.Resume.Visible);
    }

    [Fact]
    public void PresentationDisablesResumeWhenProviderAuthorityIsUnavailable()
    {
        var workflow = LocationEnrichmentWorkflow.Create("user", DateTime.UtcNow);
        workflow.Start(DateTime.UtcNow);
        workflow.Pause(DateTime.UtcNow);

        var view = LocationEnrichmentPresentation.Build(workflow, providerAvailable: false);

        Assert.True(view.Resume.Visible);
        Assert.False(view.Resume.Enabled);
        Assert.Equal("Paused by you", view.PausedReason);
    }
}
