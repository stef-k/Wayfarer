using System;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Quartz;
using Quartz.Spi;
using Wayfarer.Jobs;
using Xunit;

namespace Wayfarer.Tests.Jobs;

/// <summary>
/// Verifies JobFactory resolves jobs from DI.
/// </summary>
public class JobFactoryTests
{
    [Fact]
    public void NewJob_ReturnsResolvedInstance()
    {
        var services = new ServiceCollection();
        services.AddSingleton<StubJob>();
        var provider = services.BuildServiceProvider();
        var job = provider.GetRequiredService<StubJob>();
        var bundle = CreateBundle(typeof(StubJob));
        var factory = new JobFactory(provider);

        var resolved = factory.NewJob(bundle, Mock.Of<IScheduler>());

        Assert.Same(job, resolved);
    }

    [Fact]
    public void ReturnJob_IsNoOp()
    {
        var factory = new JobFactory(Mock.Of<IServiceProvider>());

        factory.ReturnJob(new StubJob());

        Assert.True(true); // No exceptions thrown
    }

    private static TriggerFiredBundle CreateBundle(Type jobType)
    {
        var detail = JobBuilder.Create(jobType).WithIdentity("stub").Build();
        var trigger = new Mock<IOperableTrigger>();
        trigger.SetupGet(t => t.JobKey).Returns(detail.Key);
        return new TriggerFiredBundle(
            detail,
            trigger.Object,
            null,
            false,
            DateTimeOffset.UtcNow,
            null,
            null,
            null);
    }

    private class StubJob : IJob
    {
        public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
    }
}
