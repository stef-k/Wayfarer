using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for the SseService which manages Server-Sent Events subscriptions and broadcasts.
/// </summary>
public class SseServiceTests
{
    [Fact]
    public async Task SubscribeAsync_WithHeartbeat_SetsHeadersAndCleansUpOnCancel()
    {
        var service = new SseService();
        var context = new DefaultHttpContext();
        var stream = new MemoryStream();
        context.Response.Body = stream;
        using var cts = new CancellationTokenSource(200);

        var task = service.SubscribeAsync("channel", context.Response, cts.Token, enableHeartbeat: true, heartbeatInterval: TimeSpan.FromMilliseconds(10));
        await Task.Delay(50);
        cts.Cancel();
        await task;

        Assert.Equal("text/event-stream", context.Response.Headers["Content-Type"].ToString());
        Assert.Equal("no-cache", context.Response.Headers["Cache-Control"].ToString());

        stream.SetLength(0);
        await service.BroadcastAsync("channel", "{\"hello\":true}");
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task SubscribeAsync_WithoutHeartbeat_DoesNotWriteFrames()
    {
        var service = new SseService();
        var context = new DefaultHttpContext();
        var stream = new MemoryStream();
        context.Response.Body = stream;
        using var cts = new CancellationTokenSource(50);

        var task = service.SubscribeAsync("channel", context.Response, cts.Token, enableHeartbeat: false);
        await Task.Delay(40);
        cts.Cancel();
        await task;

        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task BroadcastAsync_WritesDataToSubscribers()
    {
        var service = new SseService();
        var context = new DefaultHttpContext();
        var stream = new MemoryStream();
        context.Response.Body = stream;
        using var cts = new CancellationTokenSource(2000);

        var subscribeTask = service.SubscribeAsync("channel", context.Response, cts.Token);
        await Task.Delay(100);
        await service.BroadcastAsync("channel", "{\"hello\":true}");
        await Task.Delay(100);
        cts.Cancel();
        await subscribeTask;

        stream.Position = 0;
        var text = new StreamReader(stream).ReadToEnd();
        Assert.Contains("data: {\"hello\":true}", text);
    }
}
