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
        Assert.True(workflow.TryClaim(workflow.Epoch, DateTime.UtcNow));
        db.Add(workflow);
        await db.SaveChangesAsync();
        var batch = new Mock<ILocationEnrichmentBatch>();
        batch.Setup(item => item.RunAsync("user", workflow.Epoch, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeoapifyBackfillResult(5, 5, 0, 0, 0, false, Admitted: 5));
        var scheduler = new Mock<IWorkflowScheduleProjection>();

        await new LocationEnrichmentWorker(db, batch.Object, scheduler.Object)
            .RunBatchAsync("user", workflow.Epoch, default);

        Assert.Equal(LocationEnrichmentState.Completed, workflow.State);
        Assert.Equal(5, workflow.ProcessedCount);
        Assert.Equal(5, workflow.EnrichedCount);
        Assert.Equal(5, workflow.AdmittedUsageCount);
        batch.Verify(item => item.RunAsync("user", workflow.Epoch, It.IsAny<CancellationToken>()), Times.Once);
        scheduler.Verify(item => item.ProjectAsync(workflow.UserId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
