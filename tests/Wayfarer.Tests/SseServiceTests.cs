using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests;

public class SseServiceTests
{
    [Fact]
    public async Task SubscribeAsync_WithHeartbeat_WritesComments()
    {
        var service = new SseService();
        var context = new DefaultHttpContext();
        var stream = new MemoryStream();
        context.Response.Body = stream;
        using var cts = new CancellationTokenSource(100);

        var task = service.SubscribeAsync("channel", context.Response, cts.Token, enableHeartbeat: true, heartbeatInterval: TimeSpan.FromMilliseconds(10));
        await Task.Delay(60);
        cts.Cancel();
        await task;

        stream.Position = 0;
        var text = new StreamReader(stream).ReadToEnd();
        Assert.Contains(":\n\n", text);
    }

    [Fact]
    public async Task BroadcastAsync_WritesDataToSubscribers()
    {
        var service = new SseService();
        var context = new DefaultHttpContext();
        var stream = new MemoryStream();
        context.Response.Body = stream;
        using var cts = new CancellationTokenSource(50);

        var subscribeTask = service.SubscribeAsync("channel", context.Response, cts.Token);
        await Task.Delay(10);
        await service.BroadcastAsync("channel", "{\"hello\":true}");
        cts.Cancel();
        await subscribeTask;

        stream.Position = 0;
        var text = new StreamReader(stream).ReadToEnd();
        Assert.Contains("data: {\"hello\":true}", text);
    }
}