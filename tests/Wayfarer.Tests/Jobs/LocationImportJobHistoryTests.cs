using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Wayfarer.Areas.Admin.Controllers;
using Wayfarer.Jobs;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Jobs;

/// <summary>Proves bounded history at the production import job-to-listener seam.</summary>
public sealed class LocationImportJobHistoryTests : TestBase
{
    [Theory]
    [InlineData(LocationImportExecutionOutcome.Completed, "Completed")]
    [InlineData(LocationImportExecutionOutcome.Cancelled, "Cancelled")]
    [InlineData(LocationImportExecutionOutcome.Stale, "Cancelled")]
    [InlineData(LocationImportExecutionOutcome.Failed, "Failed")]
    public async Task ProductionOutcome_SurvivesJobAndListenerScope(
        LocationImportExecutionOutcome outcome, string expected)
    {
        await using var db = CreateDbContext();
        var service = new Mock<ILocationImportService>();
        service.As<ILocationImportExecutionService>()
            .Setup(item => item.ProcessImportExecution(7, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);
        var context = Context();
        await new LocationImportJob(service.Object, NullLogger<LocationImportJob>.Instance)
            .Execute(context.Object);

        await Listener(db).JobWasExecuted(context.Object, null, CancellationToken.None);

        var history = Assert.Single(db.JobHistories);
        Assert.Equal(expected, history.Status);
        Assert.DoesNotContain("7", history.Status);
    }

    [Fact]
    public async Task ProductFailure_PersistsOnlyBoundedStatusWithoutSensitiveExceptionText()
    {
        const string sensitive = "provider-token secret-import-path";
        await using var db = CreateDbContext();
        var service = new Mock<ILocationImportService>();
        service.As<ILocationImportExecutionService>()
            .Setup(item => item.ProcessImportExecution(7, 3, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(sensitive));
        var context = Context();
        var job = new LocationImportJob(service.Object, NullLogger<LocationImportJob>.Instance);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => job.Execute(context.Object));

        await Listener(db).JobWasExecuted(context.Object, new JobExecutionException(failure), CancellationToken.None);

        var history = Assert.Single(db.JobHistories);
        Assert.Equal("Failed", history.Status);
        Assert.DoesNotContain(sensitive, history.Status);
        Assert.DoesNotContain(sensitive, history.JobName);
    }

    [Fact]
    public async Task FencedProductFailure_PersistsEffectiveCancelledOutcome()
    {
        await using var db = CreateDbContext();
        var service = new Mock<ILocationImportService>();
        service.As<ILocationImportExecutionService>()
            .Setup(item => item.ProcessImportExecution(7, 3, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("sensitive failure"));
        var lifecycle = new Mock<ILocationImportLifecycle>();
        lifecycle.Setup(item => item.ConvergeExecutionAsync(7, 3, LocationImportExecutionOutcome.Failed,
                CancellationToken.None))
            .ReturnsAsync(LocationImportExecutionOutcome.Stale);
        var context = Context();
        var job = new LocationImportJob(service.Object, NullLogger<LocationImportJob>.Instance, lifecycle.Object);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => job.Execute(context.Object));

        await Listener(db).JobWasExecuted(context.Object, new JobExecutionException(failure), CancellationToken.None);

        Assert.Equal(LocationImportExecutionOutcome.Stale, context.Object.Result);
        Assert.Equal("Cancelled", Assert.Single(db.JobHistories).Status);
    }

    [Fact]
    public async Task SchedulerCancellation_IsCancelledRatherThanCompleted()
    {
        await using var db = CreateDbContext();
        var service = new Mock<ILocationImportService>();
        service.As<ILocationImportExecutionService>()
            .Setup(item => item.ProcessImportExecution(7, 3, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var context = Context();
        await new LocationImportJob(service.Object, NullLogger<LocationImportJob>.Instance)
            .Execute(context.Object);

        await Listener(db).JobWasExecuted(context.Object, null, CancellationToken.None);
        Assert.Equal("Cancelled", Assert.Single(db.JobHistories).Status);
    }

    private static Mock<IJobExecutionContext> Context()
    {
        var context = new Mock<IJobExecutionContext>();
        context.SetupGet(item => item.JobDetail).Returns(LocationImportSchedulerKeys.BuildJob(7, 3));
        context.SetupGet(item => item.CancellationToken).Returns(CancellationToken.None);
        context.SetupProperty(item => item.Result);
        return context;
    }

    private static JobExecutionListener Listener(ApplicationDbContext db)
    {
        var provider = new Mock<IServiceProvider>();
        provider.Setup(item => item.GetService(typeof(ApplicationDbContext))).Returns(db);
        provider.Setup(item => item.GetService(typeof(SseService))).Returns(new SseService());
        var scope = Mock.Of<IServiceScope>(item => item.ServiceProvider == provider.Object);
        return new JobExecutionListener(Mock.Of<IServiceScopeFactory>(item => item.CreateScope() == scope));
    }
}
