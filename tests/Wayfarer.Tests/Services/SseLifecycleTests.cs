using Microsoft.AspNetCore.Http;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Exercises exact channel lifetime orders and cancellation/failure convergence.</summary>
public class SseLifecycleTests
{
    /// <summary>Either last-cleanup order leaves the new subscriber reachable and retires the final state.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LastCleanupAndSubscribe(bool subscribeFirst)
    {
        var service = new SseService();
        using var old = new CancellationTokenSource();
        using var fresh = new CancellationTokenSource();
        var pump = new SsePumpContext();
        var previous = SynchronizationContext.Current;
        Task oldTask;
        try
        {
            SynchronizationContext.SetSynchronizationContext(pump);
            oldTask = service.SubscribeAsync("race", new DefaultHttpContext().Response, old.Token);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        old.Cancel();
        await pump.Posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var response = new DefaultHttpContext().Response;
        response.Body = new MemoryStream();
        Task freshTask;
        if (subscribeFirst)
        {
            freshTask = service.SubscribeAsync("race", response, fresh.Token);
            pump.Drain();
        }
        else
        {
            pump.Drain();
            freshTask = service.SubscribeAsync("race", response, fresh.Token);
        }
        await oldTask;
        await service.BroadcastAsync("race", "{}");
        Assert.Equal(10, response.Body.Length);
        Assert.Equal(1, service.ActiveConnectionCount);
        fresh.Cancel();
        await freshTask;
        Assert.Equal(0, service.ChannelCount);
        Assert.Equal(0, service.ActiveConnectionCount);
    }

    /// <summary>Failure first and cancellation first both join the send without disposal races.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailureAndCancellationConverge(bool failureFirst)
    {
        var service = new SseService();
        using var request = new CancellationTokenSource();
        using var body = new SseGateStream(fail: true);
        var subscription = service.SubscribeAsync("failure", body.Response(), request.Token);
        var send = service.BroadcastAsync("failure", "{}");
        await body.Entered.Task;
        if (failureFirst)
        {
            body.Release.SetResult();
            await send;
            await subscription.WaitAsync(TimeSpan.FromSeconds(5));
            request.Cancel();
        }
        else
        {
            request.Cancel();
            await subscription.WaitAsync(TimeSpan.FromSeconds(5));
            body.Release.SetResult();
            await send;
        }
        Assert.Equal(0, service.ChannelCount);
        Assert.Equal(0, service.ActiveConnectionCount);
    }

    /// <summary>Unique channel churn releases both dictionary entries and all admission buckets.</summary>
    [Fact]
    public async Task ChurnLeavesNoChannelsOrConnections()
    {
        var service = new SseService();
        for (var i = 0; i < 2000; i++)
        {
            using var request = new CancellationTokenSource();
            var task = service.SubscribeAsync($"churn-{i}", new DefaultHttpContext().Response, request.Token);
            request.Cancel();
            await task;
        }
        Assert.Equal(0, service.ChannelCount);
        Assert.Equal(0, service.ActiveConnectionCount);
    }

    /// <summary>Heartbeat failure terminates the subscription and bypasses protected delivery leases.</summary>
    [Fact]
    public async Task FailedHeartbeatTerminatesAndIsJoined()
    {
        var service = new SseService();
        using var body = new SseGateStream(fail: true);
        var leases = 0;
        var subscription = service.SubscribeAsync("heartbeat", body.Response(), CancellationToken.None,
            true, TimeSpan.FromMilliseconds(5), _ => { leases++; throw new Exception(); });
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(subscription.IsCompleted);
        body.Release.SetResult();
        await subscription.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, leases);
        Assert.Equal(1, body.Calls);
        Assert.Equal(0, service.ChannelCount);
    }
}
