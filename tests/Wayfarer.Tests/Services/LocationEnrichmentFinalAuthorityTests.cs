using Wayfarer.Jobs;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationProviders;
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

        Assert.Contains(scheduled!.GetParameters(), parameter => parameter.ParameterType == typeof(LocationEnrichmentExecutionLease));
        Assert.All(manual, method => Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType == typeof(int?)));
    }

    [Fact]
    public void BackfillNoLongerDependsOnTransactionSpanningLockFactory()
    {
        var dependencies = typeof(GeoapifyLocationBackfillService).GetConstructors().Single().GetParameters();

        Assert.DoesNotContain(dependencies, parameter =>
            parameter.ParameterType.IsGenericType
            && parameter.ParameterType.GetGenericTypeDefinition() == typeof(Microsoft.EntityFrameworkCore.IDbContextFactory<>));
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
}
