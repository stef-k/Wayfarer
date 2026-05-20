using System.Net;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Util;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for <see cref="ImageProxyService"/>: SSRF check, fetch+cache pipeline,
/// upstream failures, already-cached entries, and oversized images.
/// </summary>
public class ImageProxyServiceTests : TestBase
{
    public ImageProxyServiceTests()
    {
        ImageProxyService.ResetStaticStateForTesting();
    }

    [Fact]
    public async Task FetchAndCacheAsync_ReturnsFalse_ForDisallowedUrl()
    {
        var service = CreateImageProxyService();

        var result = await service.FetchAndCacheAsync("http://localhost/evil.jpg");

        Assert.False(result);
    }

    [Fact]
    public async Task FetchAndCacheAsync_ReturnsTrue_AndCachesImage()
    {
        // Minimal valid JPEG bytes (SOI + EOI markers)
        var jpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, jpegBytes, "application/octet-stream");
        var cacheMock = new Mock<IProxiedImageCacheService>();

        // No existing cache entry
        cacheMock.Setup(c => c.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(new ProxiedImageCacheResult(ProxiedImageCacheStatus.Miss, null, null, null));

        var service = CreateImageProxyService(handler: handler, cacheMock: cacheMock);

        var result = await service.FetchAndCacheAsync("https://example.com/photo.jpg");

        Assert.True(result);
        cacheMock.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task FetchAndCacheAsync_ReturnsFalse_WhenCacheStoreFails()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, new byte[] { 1, 2, 3 }, "application/octet-stream");
        var cacheMock = new Mock<IProxiedImageCacheService>();
        cacheMock.Setup(c => c.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(new ProxiedImageCacheResult(ProxiedImageCacheStatus.Miss, null, null, null));
        cacheMock.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>()))
            .ReturnsAsync(ProxiedImageCacheStoreResult.Failure);

        var service = CreateImageProxyService(handler: handler, cacheMock: cacheMock);

        var result = await service.FetchAndCacheAsync("https://example.com/store-fails.bin");

        Assert.False(result);
    }

    [Fact]
    public async Task GetOrFetchAsync_ReturnsFailed_WhenCacheStoreFails()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, new byte[] { 1, 2, 3 }, "application/octet-stream");
        var cacheMock = new Mock<IProxiedImageCacheService>();
        cacheMock.Setup(c => c.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(new ProxiedImageCacheResult(ProxiedImageCacheStatus.Miss, null, null, null));
        cacheMock.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>()))
            .ReturnsAsync(ProxiedImageCacheStoreResult.Failure);
        var service = CreateImageProxyService(handler: handler, cacheMock: cacheMock);

        var result = await service.GetOrFetchAsync(
            new ImageProxyRequest("https://example.com/store-fails-public.bin", Optimize: false),
            allowOriginFetch: true);

        Assert.Equal(ImageProxyResultStatus.Failed, result.Status);
        Assert.False(result.HasBytes);
    }

    [Fact]
    public async Task FetchAndCacheAsync_ReturnsFalse_WhenUpstreamFails()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.NotFound, Array.Empty<byte>(), "text/html");
        var cacheMock = new Mock<IProxiedImageCacheService>();
        cacheMock.Setup(c => c.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(new ProxiedImageCacheResult(ProxiedImageCacheStatus.Miss, null, null, null));

        var service = CreateImageProxyService(handler: handler, cacheMock: cacheMock);

        var result = await service.FetchAndCacheAsync("https://example.com/missing.jpg");

        Assert.False(result);
        cacheMock.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task FetchAndCacheAsync_ReturnsFalse_WhenAlreadyCached()
    {
        var cacheMock = new Mock<IProxiedImageCacheService>();
        cacheMock.Setup(c => c.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(new ProxiedImageCacheResult(
                ProxiedImageCacheStatus.FreshHit,
                new byte[] { 1, 2, 3 },
                "image/jpeg",
                null));

        var service = CreateImageProxyService(cacheMock: cacheMock);

        var result = await service.FetchAndCacheAsync("https://example.com/cached.jpg");

        Assert.False(result);
    }

    [Fact]
    public async Task FetchAndCacheAsync_ReturnsFalse_ForOversizedImage()
    {
        // Content-Length header indicates 55 MB (exceeds default 50 MB limit)
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, Array.Empty<byte>(), "image/jpeg", contentLength: 55L * 1024 * 1024);
        var cacheMock = new Mock<IProxiedImageCacheService>();
        cacheMock.Setup(c => c.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(new ProxiedImageCacheResult(ProxiedImageCacheStatus.Miss, null, null, null));

        var service = CreateImageProxyService(handler: handler, cacheMock: cacheMock);

        var result = await service.FetchAndCacheAsync("https://example.com/huge.jpg");

        Assert.False(result);
        cacheMock.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task FetchAndCacheAsync_RespectsConfigurableDownloadLimit()
    {
        // Set limit to 10 MB — image at 12 MB should be rejected
        var settingsMock = new Mock<IApplicationSettingsService>();
        settingsMock.Setup(s => s.GetSettings()).Returns(new ApplicationSettings
        {
            MaxProxyImageDownloadMB = 10
        });

        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, Array.Empty<byte>(), "image/jpeg", contentLength: 12L * 1024 * 1024);
        var cacheMock = new Mock<IProxiedImageCacheService>();
        cacheMock.Setup(c => c.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(new ProxiedImageCacheResult(ProxiedImageCacheStatus.Miss, null, null, null));

        var service = CreateImageProxyService(handler: handler, cacheMock: cacheMock, settingsMock: settingsMock);

        var result = await service.FetchAndCacheAsync("https://example.com/big.jpg");

        Assert.False(result);
        cacheMock.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetOrFetchAsync_CoalescesConcurrentCacheMisses_ByCacheKey()
    {
        var handler = new CountingHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = CreateContent(new byte[] { 1, 2, 3 }, "application/octet-stream")
            },
            delay: TimeSpan.FromMilliseconds(50));
        var cacheMock = new Mock<IProxiedImageCacheService>();
        cacheMock.Setup(c => c.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(new ProxiedImageCacheResult(ProxiedImageCacheStatus.Miss, null, null, null));
        var service = CreateImageProxyService(handler: handler, cacheMock: cacheMock);
        var request = new ImageProxyRequest("https://example.com/coalesced.bin", Optimize: false);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => service.GetOrFetchAsync(request, allowOriginFetch: true)));

        Assert.All(results, result => Assert.Equal(ImageProxyResultStatus.Fetched, result.Status));
        Assert.Equal(1, handler.RequestCount);
        cacheMock.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task RefreshAsync_CoalescesConcurrentRefreshes_ByCacheKey()
    {
        var handler = new CountingHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = CreateContent(new byte[] { 4, 5, 6 }, "application/octet-stream")
            },
            delay: TimeSpan.FromMilliseconds(50));
        var cacheMock = new Mock<IProxiedImageCacheService>();
        var service = CreateImageProxyService(handler: handler, cacheMock: cacheMock);
        var request = new ImageProxyRequest("https://example.com/stale-refresh.bin", Optimize: false);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => service.RefreshAsync(request)));

        Assert.All(results, result => Assert.Equal(ImageProxyResultStatus.Fetched, result.Status));
        Assert.Equal(1, handler.RequestCount);
        cacheMock.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task RefreshAsync_FailurePreservesStaleCacheMetadata()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.NotFound, Array.Empty<byte>(), "text/html");
        var cacheMock = new Mock<IProxiedImageCacheService>();
        var service = CreateImageProxyService(handler: handler, cacheMock: cacheMock);

        var result = await service.RefreshAsync(new ImageProxyRequest("https://example.com/missing.jpg"));

        Assert.Equal(ImageProxyResultStatus.NotFound, result.Status);
        cacheMock.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetOrFetchAsync_LimitsDistinctOriginWork_ToFourConcurrentOperations()
    {
        var handler = new SlowCountingHttpMessageHandler();
        var cacheMock = new Mock<IProxiedImageCacheService>();
        cacheMock.Setup(c => c.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(new ProxiedImageCacheResult(ProxiedImageCacheStatus.Miss, null, null, null));
        var service = CreateImageProxyService(handler: handler, cacheMock: cacheMock);

        await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(i => service.GetOrFetchAsync(
                new ImageProxyRequest($"https://example.com/image-{i}.bin", Optimize: false),
                allowOriginFetch: true)));

        Assert.True(handler.MaxActiveRequests <= 4);
    }

    [Fact]
    public async Task FetchAndCacheAsync_ConcurrentWarmupUsesPerKeyCoalescing()
    {
        var handler = new CountingHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = CreateContent(new byte[] { 7, 8, 9 }, "application/octet-stream")
            },
            delay: TimeSpan.FromMilliseconds(50));
        var cacheMock = new Mock<IProxiedImageCacheService>();
        cacheMock.Setup(c => c.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(new ProxiedImageCacheResult(ProxiedImageCacheStatus.Miss, null, null, null));
        var service = CreateImageProxyService(handler: handler, cacheMock: cacheMock);

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => service.FetchAndCacheAsync("https://example.com/warmup.bin")));

        Assert.Contains(true, results);
        Assert.Equal(1, handler.RequestCount);
        cacheMock.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task GetOrFetchAsync_StaleHitSchedulesBackgroundRefresh()
    {
        var probe = new ScheduledRefreshProbe(ImageProxyResultStatus.Fetched);
        var cacheMock = CreateStaleCacheMock(new byte[] { 9 }, "image/jpeg");
        var service = CreateImageProxyService(
            cacheMock: cacheMock,
            scopeFactory: CreateScopeFactory(probe));
        var request = new ImageProxyRequest("https://example.com/stale-public.jpg", Optimize: false);

        var result = await service.GetOrFetchAsync(request, allowOriginFetch: false);
        Assert.True(await ImageProxyService.WaitForRefreshIdleForTestingAsync(result.CacheKey, TimeSpan.FromSeconds(2)));

        Assert.Equal(ImageProxyResultStatus.StaleHit, result.Status);
        Assert.Equal(1, probe.AttemptCount);
        Assert.Equal(1, probe.InstanceCount);
    }

    [Fact]
    public async Task GetOrFetchAsync_BackgroundRefreshUsesFreshScopedServices()
    {
        var probe = new ScheduledRefreshProbe(
            ImageProxyResultStatus.Failed,
            ImageProxyResultStatus.Failed,
            ImageProxyResultStatus.Failed);
        var cacheMock = CreateStaleCacheMock(new byte[] { 8 }, "image/jpeg");
        var service = CreateImageProxyService(
            cacheMock: cacheMock,
            scopeFactory: CreateScopeFactory(probe));
        ImageProxyService.SetRefreshRetryDelayForTesting(_ => TimeSpan.Zero);

        var result = await service.GetOrFetchAsync(
            new ImageProxyRequest("https://example.com/scoped-refresh.jpg", Optimize: false),
            allowOriginFetch: false);
        Assert.True(await ImageProxyService.WaitForRefreshIdleForTestingAsync(result.CacheKey, TimeSpan.FromSeconds(2)));

        Assert.Equal(3, probe.AttemptCount);
        Assert.Equal(3, probe.InstanceCount);
    }

    [Fact]
    public async Task GetOrFetchAsync_BackgroundRefreshHonorsMaxAttempts()
    {
        var probe = new ScheduledRefreshProbe(
            ImageProxyResultStatus.Failed,
            ImageProxyResultStatus.Failed,
            ImageProxyResultStatus.Failed,
            ImageProxyResultStatus.Fetched);
        var cacheMock = CreateStaleCacheMock(new byte[] { 7 }, "image/jpeg");
        var service = CreateImageProxyService(
            cacheMock: cacheMock,
            scopeFactory: CreateScopeFactory(probe));
        ImageProxyService.SetRefreshRetryDelayForTesting(_ => TimeSpan.Zero);

        var result = await service.GetOrFetchAsync(
            new ImageProxyRequest("https://example.com/max-attempts.jpg", Optimize: false),
            allowOriginFetch: false);
        Assert.True(await ImageProxyService.WaitForRefreshIdleForTestingAsync(result.CacheKey, TimeSpan.FromSeconds(2)));

        Assert.Equal(3, probe.AttemptCount);
    }

    [Fact]
    public async Task GetOrFetchAsync_BackgroundRefreshExercisesDelayedRetry()
    {
        var delayAttempts = new ConcurrentQueue<int>();
        var probe = new ScheduledRefreshProbe(ImageProxyResultStatus.Failed, ImageProxyResultStatus.Fetched);
        var cacheMock = CreateStaleCacheMock(new byte[] { 6 }, "image/jpeg");
        var service = CreateImageProxyService(
            cacheMock: cacheMock,
            scopeFactory: CreateScopeFactory(probe));
        ImageProxyService.SetRefreshRetryDelayForTesting(attempt =>
        {
            delayAttempts.Enqueue(attempt);
            return TimeSpan.FromMilliseconds(10);
        });

        var result = await service.GetOrFetchAsync(
            new ImageProxyRequest("https://example.com/delayed-retry.jpg", Optimize: false),
            allowOriginFetch: false);
        Assert.True(await ImageProxyService.WaitForRefreshIdleForTestingAsync(result.CacheKey, TimeSpan.FromSeconds(2)));

        Assert.Equal(2, probe.AttemptCount);
        Assert.Contains(1, delayAttempts);
    }

    [Fact]
    public async Task GetOrFetchAsync_BackgroundRefreshDeadlineCancelsSlowAttempt()
    {
        var probe = new ScheduledRefreshProbe();
        probe.OnAttemptAsync = async ct =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref probe.CanceledCount);
                throw;
            }

            return ImageProxyResultStatus.Fetched;
        };
        var cacheMock = CreateStaleCacheMock(new byte[] { 5 }, "image/jpeg");
        var service = CreateImageProxyService(
            cacheMock: cacheMock,
            scopeFactory: CreateScopeFactory(probe));
        ImageProxyService.SetRefreshSeriesMaxDurationForTesting(TimeSpan.FromMilliseconds(50));

        var result = await service.GetOrFetchAsync(
            new ImageProxyRequest("https://example.com/slow-refresh.jpg", Optimize: false),
            allowOriginFetch: false);
        Assert.True(await ImageProxyService.WaitForRefreshIdleForTestingAsync(result.CacheKey, TimeSpan.FromSeconds(2)));

        Assert.Equal(1, probe.AttemptCount);
        Assert.Equal(1, probe.CanceledCount);
    }

    [Fact]
    public async Task GetOrFetchAsync_ExhaustedBackgroundSeriesAllowsLaterSeries()
    {
        var probe = new ScheduledRefreshProbe(
            ImageProxyResultStatus.Failed,
            ImageProxyResultStatus.Failed,
            ImageProxyResultStatus.Failed,
            ImageProxyResultStatus.Failed,
            ImageProxyResultStatus.Failed,
            ImageProxyResultStatus.Failed);
        var cacheMock = CreateStaleCacheMock(new byte[] { 4 }, "image/jpeg");
        var service = CreateImageProxyService(
            cacheMock: cacheMock,
            scopeFactory: CreateScopeFactory(probe));
        ImageProxyService.SetRefreshRetryDelayForTesting(_ => TimeSpan.Zero);
        var request = new ImageProxyRequest("https://example.com/restart-series.jpg", Optimize: false);

        var first = await service.GetOrFetchAsync(request, allowOriginFetch: false);
        Assert.True(await ImageProxyService.WaitForRefreshIdleForTestingAsync(first.CacheKey, TimeSpan.FromSeconds(2)));
        var second = await service.GetOrFetchAsync(request, allowOriginFetch: false);
        Assert.True(await ImageProxyService.WaitForRefreshIdleForTestingAsync(second.CacheKey, TimeSpan.FromSeconds(2)));

        Assert.Equal(6, probe.AttemptCount);
    }

    /// <summary>
    /// Creates an <see cref="ImageProxyService"/> with test doubles.
    /// </summary>
    private ImageProxyService CreateImageProxyService(
        HttpMessageHandler? handler = null,
        Mock<IProxiedImageCacheService>? cacheMock = null,
        Mock<IApplicationSettingsService>? settingsMock = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        handler ??= new MockHttpMessageHandler(HttpStatusCode.OK, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, "image/jpeg");
        cacheMock ??= new Mock<IProxiedImageCacheService>();
        cacheMock.SetReturnsDefault(Task.FromResult(ProxiedImageCacheStoreResult.Success));
        if (settingsMock == null)
        {
            settingsMock = new Mock<IApplicationSettingsService>();
            settingsMock.Setup(s => s.GetSettings()).Returns(new ApplicationSettings());
        }

        var httpClient = new HttpClient(handler);
        return new ImageProxyService(
            httpClient,
            cacheMock.Object,
            settingsMock.Object,
            scopeFactory ?? Mock.Of<IServiceScopeFactory>(),
            NullLogger<ImageProxyService>.Instance);
    }

    private static Mock<IProxiedImageCacheService> CreateStaleCacheMock(byte[] bytes, string contentType)
    {
        var cacheMock = new Mock<IProxiedImageCacheService>();
        cacheMock.Setup(c => c.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(new ProxiedImageCacheResult(ProxiedImageCacheStatus.StaleHit, bytes, contentType, null));
        return cacheMock;
    }

    private static IServiceScopeFactory CreateScopeFactory(ScheduledRefreshProbe probe)
    {
        var services = new ServiceCollection();
        services.AddScoped<IImageProxyService>(_ => new ScheduledRefreshImageProxyService(probe));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>
    /// Simple HttpMessageHandler mock that returns a fixed response.
    /// </summary>
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly byte[] _content;
        private readonly string _contentType;
        private readonly long? _contentLength;

        public MockHttpMessageHandler(HttpStatusCode statusCode, byte[] content, string contentType, long? contentLength = null)
        {
            _statusCode = statusCode;
            _content = content;
            _contentType = contentType;
            _contentLength = contentLength;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new ByteArrayContent(_content)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType);
            if (_contentLength.HasValue)
                response.Content.Headers.ContentLength = _contentLength.Value;
            return Task.FromResult(response);
        }
    }

    private static ByteArrayContent CreateContent(byte[] bytes, string contentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return content;
    }

    private sealed class CountingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _responseFactory;
        private readonly TimeSpan _delay;
        private int _requestCount;

        public int RequestCount => _requestCount;

        public CountingHttpMessageHandler(Func<HttpResponseMessage> responseFactory, TimeSpan? delay = null)
        {
            _responseFactory = responseFactory;
            _delay = delay ?? TimeSpan.Zero;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken);
            }

            return _responseFactory();
        }
    }

    private sealed class SlowCountingHttpMessageHandler : HttpMessageHandler
    {
        private int _activeRequests;
        private int _maxActiveRequests;

        public int MaxActiveRequests => _maxActiveRequests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _activeRequests);
            UpdateMaxActive(active);
            try
            {
                await Task.Delay(50, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = CreateContent(new byte[] { 1 }, "application/octet-stream")
                };
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }

        private void UpdateMaxActive(int active)
        {
            int current;
            do
            {
                current = _maxActiveRequests;
                if (active <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _maxActiveRequests, active, current) != current);
        }
    }

    private sealed class ScheduledRefreshProbe
    {
        private readonly ConcurrentQueue<ImageProxyResultStatus> _statuses;
        private int _attemptCount;
        private int _instanceCount;

        public int AttemptCount => _attemptCount;
        public int InstanceCount => _instanceCount;
        public int CanceledCount;
        public Func<CancellationToken, Task<ImageProxyResultStatus>>? OnAttemptAsync { get; set; }

        public ScheduledRefreshProbe(params ImageProxyResultStatus[] statuses)
        {
            _statuses = new ConcurrentQueue<ImageProxyResultStatus>(statuses);
        }

        public void RecordInstance() => Interlocked.Increment(ref _instanceCount);

        public async Task<ImageProxyResultStatus> RecordAttemptAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _attemptCount);
            if (OnAttemptAsync != null)
            {
                return await OnAttemptAsync(ct);
            }

            return _statuses.TryDequeue(out var status) ? status : ImageProxyResultStatus.Failed;
        }
    }

    private sealed class ScheduledRefreshImageProxyService : IImageProxyService
    {
        private readonly ScheduledRefreshProbe _probe;

        public ScheduledRefreshImageProxyService(ScheduledRefreshProbe probe)
        {
            _probe = probe;
            _probe.RecordInstance();
        }

        public Task<ImageProxyResult> GetOrFetchAsync(
            ImageProxyRequest request,
            bool allowOriginFetch,
            CancellationToken ct = default) =>
            Task.FromResult(new ImageProxyResult(ImageProxyResultStatus.Failed, string.Empty, null, null));

        public async Task<ImageProxyResult> RefreshAsync(ImageProxyRequest request, CancellationToken ct = default)
        {
            var status = await _probe.RecordAttemptAsync(ct);
            var cacheKey = ImageProxyHelper.ComputeImageCacheKey(
                request.Url,
                request.MaxWidth,
                request.MaxHeight,
                request.Quality,
                request.Optimize);
            return new ImageProxyResult(status, cacheKey, null, null);
        }

        public Task<bool> FetchAndCacheAsync(string imageUrl, CancellationToken ct = default) =>
            Task.FromResult(false);
    }
}
