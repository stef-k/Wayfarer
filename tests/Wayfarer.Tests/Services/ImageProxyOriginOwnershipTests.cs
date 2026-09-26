using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Gates actual shared origin operations to prove independent waiter and admission lifetimes.</summary>
public partial class ImageProxyServiceTests
{
    /// <summary>Either creator or joiner may disconnect without cancelling useful work for the survivor.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SharedOrigin_CancelledWaiterDoesNotOwnOperation(bool cancelCreator)
    {
        var handler = new GatedHttpMessageHandler(ImageProxyTestFactory.Raster(), "image/jpeg");
        var cache = CreateMissingCache();
        var service = CreateImageProxyService(handler, cache);
        using var creator = new CancellationTokenSource();
        using var joiner = new CancellationTokenSource();
        var request = new ImageProxyRequest("https://example.com/shared", Optimize: false);
        var first = service.GetOrFetchAsync(request, true, creator.Token);
        await handler.WaitForRequestAsync();
        var second = service.GetOrFetchAsync(request, true, joiner.Token);
        (cancelCreator ? creator : joiner).Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => (cancelCreator ? first : second).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False((cancelCreator ? second : first).IsCompleted);
        // Joining after cancellation must still find the original dictionary entry.
        var third = service.RefreshAsync(request);
        handler.Release();
        Assert.Equal(ImageProxyResultStatus.Fetched, (await (cancelCreator ? second : first)).Status);
        Assert.Equal(ImageProxyResultStatus.Fetched, (await third).Status);
        Assert.Equal(1, handler.RequestCount);
    }

    /// <summary>The worker cache scope survives disposal of the disconnected creator's request scope.</summary>
    [Fact]
    public async Task SharedOrigin_OwnsCacheScopeUntilPublicationCompletes()
    {
        var handler = new GatedHttpMessageHandler(ImageProxyTestFactory.Raster(), "image/jpeg");
        using var client = new HttpClient(handler);
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(s => s.GetSettings()).Returns(new ApplicationSettings());
        var disposed = new List<bool>();
        var services = new ServiceCollection();
        services.AddScoped<IProxiedImageCacheService>(_ =>
        {
            var index = disposed.Count;
            disposed.Add(false);
            var cache = CreateMissingCache();
            cache.As<IDisposable>().Setup(d => d.Dispose()).Callback(() => disposed[index] = true);
            cache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback(() => Assert.False(disposed[index]))
                .ReturnsAsync(ProxiedImageCacheStoreResult.Success);
            return cache.Object;
        });
        services.AddScoped<IImageProxyService>(sp => new ImageProxyService(client,
            sp.GetRequiredService<IProxiedImageCacheService>(), settings.Object,
            sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<ImageProxyService>.Instance));
        using var provider = services.BuildServiceProvider();
        using var callerScope = provider.CreateScope();
        using var abort = new CancellationTokenSource();
        var request = new ImageProxyRequest("https://example.com/scoped");
        var creator = callerScope.ServiceProvider.GetRequiredService<IImageProxyService>()
            .GetOrFetchAsync(request, true, abort.Token);
        await handler.WaitForRequestAsync();
        using var survivorScope = provider.CreateScope();
        var survivor = survivorScope.ServiceProvider.GetRequiredService<IImageProxyService>().RefreshAsync(request);
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creator);
        callerScope.Dispose();
        Assert.True(disposed[0]);
        Assert.False(disposed[1]);
        handler.Release();
        Assert.Equal(ImageProxyResultStatus.Fetched, (await survivor).Status);
        Assert.True(disposed[1]);
    }

    /// <summary>Four distinct keys occupy slots; the fifth fails immediately while a same-key waiter joins.</summary>
    [Fact]
    public async Task SharedOrigin_HasNoDistinctKeyQueue()
    {
        var handlers = Enumerable.Range(0, 4)
            .Select(_ => new GatedHttpMessageHandler(ImageProxyTestFactory.Raster(), "image/jpeg")).ToArray();
        var services = handlers.Select(h => CreateImageProxyService(h, CreateMissingCache())).ToArray();
        var requests = Enumerable.Range(0, 4)
            .Select(i => new ImageProxyRequest($"https://example.com/capacity-{i}", Optimize: false)).ToArray();
        var tasks = services.Select((s, i) => s.RefreshAsync(requests[i])).ToArray();
        await Task.WhenAll(handlers.Select(h => h.WaitForRequestAsync()));
        try
        {
            var rejected = await services[0].RefreshAsync(new("https://example.com/fifth"))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ImageProxyResultStatus.Unavailable, rejected.Status);
            var joined = services[0].RefreshAsync(requests[0]);
            Assert.False(joined.IsCompleted);
            handlers[0].Release();
            Assert.Equal(ImageProxyResultStatus.Fetched, (await joined).Status);
            Assert.Equal(1, handlers[0].RequestCount);
        }
        finally
        {
            foreach (var handler in handlers) handler.Release();
            await Task.WhenAll(tasks);
        }
    }

    /// <summary>The total operation deadline covers a stalled body and releases capacity only on completion.</summary>
    [Fact]
    public async Task SharedOrigin_DeadlineCancelsStalledBodyAndReleasesSlot()
    {
        var clock = new DeadlineClock();
        ImageProxyService.OriginTimeProvider = clock;
        var body = new StalledBody();
        var handler = new CountingHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body)
        });
        var cache = CreateMissingCache();
        var service = CreateImageProxyService(handler, cache);
        var request = new ImageProxyRequest("https://example.com/stalled");
        var work = service.RefreshAsync(request);
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(100), clock.DueTime);
        clock.Expire();
        Assert.Equal(ImageProxyResultStatus.Unavailable, (await work.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        VerifyNeverStored(cache);
        Assert.True(body.Cancelled);
        ImageProxyService.OriginTimeProvider = TimeProvider.System;
        // Reusing the key proves completed work no longer owns its dictionary entry or slot.
        var replacement = CreateImageProxyService(cacheMock: CreateMissingCache());
        Assert.Equal(ImageProxyResultStatus.Fetched, (await replacement.RefreshAsync(request)).Status);
    }

    /// <summary>A refresh-series waiter deadline can stop joining foreground work without owning its cancellation.</summary>
    [Fact]
    public async Task SharedOrigin_RefreshWaitHonorsSeriesCancellation()
    {
        var handler = new GatedHttpMessageHandler(ImageProxyTestFactory.Raster(), "image/jpeg");
        var service = CreateImageProxyService(handler, CreateMissingCache());
        var request = new ImageProxyRequest("https://example.com/foreground-with-refresh");
        var foreground = service.GetOrFetchAsync(request, true);
        await handler.WaitForRequestAsync();
        using var seriesDeadline = new CancellationTokenSource();
        var refresh = service.RefreshAsync(request, seriesDeadline.Token);
        seriesDeadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(foreground.IsCompleted);
        handler.Release();
        Assert.Equal(ImageProxyResultStatus.Fetched, (await foreground).Status);
        Assert.Equal(1, handler.RequestCount);
    }

    /// <summary>Fires the production CTS timer explicitly after the body enters its cancellable read.</summary>
    private sealed class DeadlineClock : TimeProvider
    {
        private Action? _expire;
        public TimeSpan DueTime { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            _expire = () => callback(state);
            return new DeadlineTimer();
        }
        public void Expire() => _expire!();
        private sealed class DeadlineTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>A response body that cannot finish until the operation token cancels it.</summary>
    private sealed class StalledBody : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
