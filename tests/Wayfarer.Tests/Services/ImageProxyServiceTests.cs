using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for <see cref="ImageProxyService"/>: SSRF check, fetch+cache pipeline,
/// upstream failures, already-cached entries, and oversized images.
/// </summary>
[Collection(ImageProxyStaticStateTestCollection.Name)]
public partial class ImageProxyServiceTests : TestBase
{
    public ImageProxyServiceTests()
    {
        ImageProxyService.ResetStaticStateForTesting();
    }

    /// <summary>Guards the non-parallel boundary required by shared image proxy static state.</summary>
    [Fact]
    public void StaticStateUsers_ShareTheNonParallelImageProxyCollection()
    {
        var staticStateUsers = new[]
        {
            typeof(ImageProxyServiceTests),
            typeof(Wayfarer.Tests.Controllers.TripViewerControllerTests),
            typeof(Wayfarer.Tests.Controllers.PublicTripImagesTests)
        };

        Assert.All(staticStateUsers, type =>
        {
            var attribute = Assert.Single(
                type.CustomAttributes,
                attribute => attribute.AttributeType == typeof(CollectionAttribute));
            Assert.Equal(ImageProxyStaticStateTestCollection.Name, attribute.ConstructorArguments.Single().Value);
        });

        var definition = typeof(ImageProxyStaticStateTestCollection)
            .GetCustomAttributes(typeof(CollectionDefinitionAttribute), inherit: false)
            .Cast<CollectionDefinitionAttribute>()
            .Single();
        Assert.True(definition.DisableParallelization);
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

    /// <summary>Optimize=false bypasses identification and preserves origin bytes and content type.</summary>
    [Fact]
    public async Task GetOrFetchAsync_OptimizeFalse_BypassesDecodeAndPreservesResponse()
    {
        var originBytes = new byte[] { 1, 2, 3 };
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, originBytes, "image/png");
        var cacheMock = new Mock<IProxiedImageCacheService>();
        cacheMock.Setup(c => c.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(new ProxiedImageCacheResult(ProxiedImageCacheStatus.Miss, null, null, null));
        var service = CreateImageProxyService(handler: handler, cacheMock: cacheMock);

        var result = await service.GetOrFetchAsync(
            new ImageProxyRequest("https://example.com/pass-through.png", Optimize: false),
            allowOriginFetch: true);

        Assert.Equal(ImageProxyResultStatus.Fetched, result.Status);
        Assert.Equal(originBytes, result.Bytes);
        Assert.Equal("image/png", result.ContentType);
    }

    /// <summary>A decoded-resource rejection is TooLarge and never populates the cache.</summary>
    [Fact]
    public async Task GetOrFetchAsync_DecodedResourceRejection_DoesNotPopulateCache()
    {
        var handler = new MockHttpMessageHandler(
            HttpStatusCode.OK,
            PngContractFixture.Create(8193, 1),
            "image/png");
        var cacheMock = new Mock<IProxiedImageCacheService>();
        cacheMock.Setup(c => c.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(new ProxiedImageCacheResult(ProxiedImageCacheStatus.Miss, null, null, null));
        var service = CreateImageProxyService(handler: handler, cacheMock: cacheMock);

        var result = await service.GetOrFetchAsync(
            new ImageProxyRequest("https://example.com/declared-wide.png"),
            allowOriginFetch: true);

        Assert.Equal(ImageProxyResultStatus.TooLarge, result.Status);
        cacheMock.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>Coalesced callers share one origin request and the same decoded rejection.</summary>
    [Fact]
    public async Task GetOrFetchAsync_CoalescesDecodedResourceRejection()
    {
        var handler = new GatedHttpMessageHandler(PngContractFixture.Create(8193, 1), "image/png");
        var cacheMock = new Mock<IProxiedImageCacheService>();
        cacheMock.Setup(c => c.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(new ProxiedImageCacheResult(ProxiedImageCacheStatus.Miss, null, null, null));
        var service = CreateImageProxyService(handler: handler, cacheMock: cacheMock);
        var request = new ImageProxyRequest("https://example.com/coalesced-wide.png");

        var first = service.GetOrFetchAsync(request, allowOriginFetch: true);
        await handler.WaitForRequestAsync();
        var second = service.GetOrFetchAsync(request, allowOriginFetch: true);
        handler.Release();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(ImageProxyResultStatus.TooLarge, result.Status));
        Assert.Equal(1, handler.RequestCount);
        cacheMock.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>()), Times.Never);
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

    /// <summary>Deterministically holds one response so a same-key waiter can join it.</summary>
    private sealed class GatedHttpMessageHandler(byte[] bytes, string contentType) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        public int RequestCount => _requestCount;

        public Task WaitForRequestAsync() => _requested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release() => _released.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            _requested.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = CreateContent(bytes, contentType)
            };
        }
    }

}
