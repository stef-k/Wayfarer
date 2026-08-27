using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationProviders;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Defines batch-to-workflow persistence and rescheduling behavior.</summary>
public sealed class LocationEnrichmentWorkerTests
{
    [Fact]
    public async Task SuccessfulBatchCommitsProgressAndDoesNotRepeatCompletedWork()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ApplicationDbContext(options, services);
        var workflow = LocationEnrichmentWorkflow.Create("user", DateTime.UtcNow);
        workflow.Start(DateTime.UtcNow);
        db.Add(workflow);
        await db.SaveChangesAsync();
        var contexts = new TestContextFactory(options, services);
        var authority = new LocationEnrichmentExecutionAuthority(contexts);
        var batch = new Mock<ILocationEnrichmentBatch>();
        batch.Setup(item => item.RunAsync(It.Is<LocationEnrichmentExecutionLease>(owner =>
                owner.UserId == "user" && owner.Epoch == workflow.Epoch), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeoapifyBackfillResult(5, 5, 0, 0, 0, false, Admitted: 5));
        var scheduler = new Mock<IWorkflowScheduleProjection>();

        await new LocationEnrichmentWorker(contexts, authority, batch.Object, scheduler.Object)
            .RunBatchAsync("user", workflow.Epoch, default);

        await db.Entry(workflow).ReloadAsync();
        Assert.Equal(LocationEnrichmentState.Completed, workflow.State);
        Assert.Equal(5, workflow.ProcessedCount);
        Assert.Equal(5, workflow.EnrichedCount);
        Assert.Equal(0, workflow.AdmittedUsageCount);
        batch.Verify(item => item.RunAsync(It.IsAny<LocationEnrichmentExecutionLease>(),
            It.IsAny<CancellationToken>()), Times.Once);
        scheduler.Verify(item => item.ProjectAsync(workflow.UserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Proves locally invalid rows use the durable permanent-deferred aggregate and converge.</summary>
    [Fact]
    public async Task InvalidOnlyBatchCommitsPermanentProgressAndCompletes()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ApplicationDbContext(options, services);
        var workflow = LocationEnrichmentWorkflow.Create("user", DateTime.UtcNow);
        workflow.Start(DateTime.UtcNow);
        db.Add(workflow);
        await db.SaveChangesAsync();
        var contexts = new TestContextFactory(options, services);
        var authority = new LocationEnrichmentExecutionAuthority(contexts);
        var batch = new Mock<ILocationEnrichmentBatch>();
        batch.Setup(item => item.RunAsync(It.IsAny<LocationEnrichmentExecutionLease>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeoapifyBackfillResult(2, 0, 0, 0, 0, false,
                PermanentlyDeferred: 2));
        var scheduler = new Mock<IWorkflowScheduleProjection>();

        await new LocationEnrichmentWorker(contexts, authority, batch.Object, scheduler.Object)
            .RunBatchAsync("user", workflow.Epoch, default);

        await db.Entry(workflow).ReloadAsync();
        Assert.Equal(LocationEnrichmentState.Completed, workflow.State);
        Assert.Equal(2, workflow.ProcessedCount);
        Assert.Equal(2, workflow.PermanentlyDeferredCount);
        Assert.Equal(0, workflow.RetryableDeferredCount);
        Assert.Equal(0, workflow.AdmittedUsageCount);
        Assert.Null(workflow.NextEligibleAtUtc);
        scheduler.Verify(item => item.ProjectAsync(workflow.UserId,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class TestContextFactory(DbContextOptions<ApplicationDbContext> options, IServiceProvider services)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options, services);
    }
}
