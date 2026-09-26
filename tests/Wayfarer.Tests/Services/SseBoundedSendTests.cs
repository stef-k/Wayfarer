using Microsoft.AspNetCore.Http;
using Wayfarer.Models.Options;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Gated transport evidence for cancellation, serialization, deadlines and bounded fanout.</summary>
public class SseBoundedSendTests
{
    /// <summary>One blocked recipient cannot suppress a healthy recipient, and request cancellation drains both send owners.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowRecipientDoesNotBlockHealthyRecipient(bool flush)
    {
        var service = new SseService();
        using var request = new CancellationTokenSource();
        using var slow = new SseGateStream(flush);
        using var healthy = new SseGateStream();
        healthy.Release.SetResult();
        var one = service.SubscribeAsync("fanout", slow.Response(), request.Token);
        var two = service.SubscribeAsync("fanout", healthy.Response(), request.Token);
        var send = service.BroadcastAsync("fanout", "{}");
        await slow.Entered.Task;
        await healthy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = service.BroadcastAsync("fanout", "{}");
        Assert.Equal(1, slow.Calls);
        Assert.True(slow.ObservedToken.CanBeCanceled);
        request.Cancel();
        await Task.WhenAll(one, two, send, queued).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(slow.ObservedToken.IsCancellationRequested);
        Assert.Equal(0, service.ChannelCount);
    }

    /// <summary>A third send evicts instead of allocating an unbounded semaphore queue.</summary>
    [Fact]
    public async Task BacklogOverflowTerminatesConnection()
    {
        var service = new SseService();
        using var body = new SseGateStream();
        var subscription = service.SubscribeAsync("backlog", body.Response(), CancellationToken.None);
        var first = service.BroadcastAsync("backlog", "one");
        await body.Entered.Task;
        var second = service.BroadcastAsync("backlog", "two");
        await service.BroadcastAsync("backlog", "overflow");
        await Task.WhenAll(first, second, subscription).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, body.Calls);
        Assert.Equal(0, service.ActiveConnectionCount);
    }

    /// <summary>Slot acquisition precedes the lease and one deadline covers a blocked lease, write or flush.</summary>
    [Theory]
    [InlineData("lease")]
    [InlineData("write")]
    [InlineData("flush")]
    public async Task DeadlineTerminatesBlockedStage(string stage)
    {
        var service = new SseService(new SseOptions { SendTimeout = TimeSpan.FromMilliseconds(100) });
        using var body = new SseGateStream(stage == "flush");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        async Task<IAsyncDisposable?> Lease(CancellationToken token)
        {
            observed = token;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return null;
        }
        var subscription = service.SubscribeAsync("deadline", body.Response(), CancellationToken.None,
            deliveryLease: stage == "lease" ? Lease : null);
        await service.BroadcastAsync("deadline", "{}").WaitAsync(TimeSpan.FromSeconds(5));
        await subscription.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stage == "lease" ? observed.IsCancellationRequested : body.ObservedToken.IsCancellationRequested);
        Assert.Equal(0, service.ActiveConnectionCount);
    }

    /// <summary>Cancellation during a lease reaches the same token, while a queued attempt has not acquired a lease.</summary>
    [Fact]
    public async Task RequestCancellationReachesLeaseAfterSlot()
    {
        var service = new SseService();
        using var request = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CancellationToken observed = default;
        async Task<IAsyncDisposable?> Lease(CancellationToken token)
        {
            calls++;
            observed = token;
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return null;
        }
        var subscription = service.SubscribeAsync("lease", new DefaultHttpContext().Response, request.Token, deliveryLease: Lease);
        var first = service.BroadcastAsync("lease", "{}");
        await entered.Task;
        var queued = service.BroadcastAsync("lease", "{}");
        Assert.Equal(1, calls);
        request.Cancel();
        await Task.WhenAll(first, queued, subscription).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(observed.IsCancellationRequested);
    }

    /// <summary>The overall horizon stops worker waves but does not evict a recipient never attempted.</summary>
    [Fact]
    public async Task HorizonBoundsWorkersAndPreservesUnattemptedRecipient()
    {
        var service = new SseService(new SseOptions { FanoutConcurrency = 2, BroadcastTimeout = TimeSpan.FromMilliseconds(150) });
        using var request = new CancellationTokenSource();
        using var first = new SseGateStream();
        using var second = new SseGateStream();
        using var third = new SseGateStream();
        third.Release.SetResult();
        var subscriptions = new[] { first, second, third }
            .Select(body => service.SubscribeAsync("horizon", body.Response(), request.Token)).ToArray();
        var send = service.BroadcastAsync("horizon", "{}");
        await Task.WhenAll(first.Entered.Task, second.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, third.Calls);
        await send.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(subscriptions.Take(2)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(subscriptions[2].IsCompleted);
        await service.BroadcastAsync("horizon", "survivor");
        Assert.Equal(1, third.Calls);
        request.Cancel();
        await Task.WhenAll(subscriptions);
    }

    /// <summary>Heartbeat uses the same slot as an event and request shutdown joins its cancellable write.</summary>
    [Fact]
    public async Task HeartbeatAndEventSerialize()
    {
        var service = new SseService();
        using var body = new SseGateStream();
        using var request = new CancellationTokenSource();
        var subscription = service.SubscribeAsync("heartbeat", body.Response(), request.Token, true, TimeSpan.FromMilliseconds(5));
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var send = service.BroadcastAsync("heartbeat", "{}");
        Assert.Equal(1, body.Calls);
        request.Cancel();
        await Task.WhenAll(subscription, send).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, body.Calls);
        Assert.Equal(0, service.ActiveConnectionCount);
    }

    /// <summary>Lease exceptions remain best effort for the producer and terminate only the affected stream.</summary>
    [Fact]
    public async Task FailedLeaseTerminates()
    {
        var service = new SseService();
        var subscription = service.SubscribeAsync("denied", new DefaultHttpContext().Response, CancellationToken.None,
            deliveryLease: _ => throw new IOException("database unavailable"));
        await service.BroadcastAsync("denied", "{}");
        await subscription.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, service.ChannelCount);
    }
}
