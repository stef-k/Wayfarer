using Moq;
using Quartz;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Verifies QuartzHostedService wires scheduler lifecycle.
/// </summary>
public class QuartzHostedServiceTests
{
    [Fact]
    public async Task StartAsync_StartsScheduler()
    {
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(s => s.Start(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var hosted = new QuartzHostedService(scheduler.Object);
        var token = new CancellationToken();

        await hosted.StartAsync(token);

        scheduler.Verify(s => s.Start(token), Times.Once);
    }

    [Fact]
    public async Task StopAsync_ShutsDownSchedulerGracefully()
    {
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(s => s.Shutdown(true, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var hosted = new QuartzHostedService(scheduler.Object);
        var token = new CancellationTokenSource().Token;

        await hosted.StopAsync(token);

        scheduler.Verify(s => s.Shutdown(true, token), Times.Once);
    }
}
