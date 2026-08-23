using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Quartz;
using Wayfarer.Jobs;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationEnrichment;
using Xunit;

namespace Wayfarer.Tests.Jobs;

/// <summary>Proves stale Quartz deliveries cannot enter the provider worker.</summary>
public sealed class LocationEnrichmentJobTests
{
    [Fact]
    public async Task StaleEpochIsNoOpBeforeWorkerContact()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ApplicationDbContext(options, services);
        var workflow = LocationEnrichmentWorkflow.Create("user", DateTime.UtcNow);
        workflow.Start(DateTime.UtcNow);
        db.Add(workflow);
        await db.SaveChangesAsync();
        var worker = new Mock<ILocationEnrichmentWorker>();
        var context = new Mock<IJobExecutionContext>();
        context.SetupGet(item => item.MergedJobDataMap).Returns(new JobDataMap
        {
            ["workflowId"] = workflow.SchedulerId.ToString("N"), ["schema"] = 1, ["epoch"] = workflow.Epoch + 1
        });

        await new LocationEnrichmentJob(db, worker.Object).Execute(context.Object);

        worker.Verify(item => item.RunBatchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
