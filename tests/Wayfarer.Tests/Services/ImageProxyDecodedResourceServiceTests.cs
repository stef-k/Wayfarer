using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Focused service coverage for decoded-resource result, cache, logging, and coalescing behavior.</summary>
public partial class ImageProxyServiceTests
{
    /// <summary>Optimize=false bypasses identification and preserves origin bytes and content type.</summary>
    [Fact]
    public async Task GetOrFetchAsync_OptimizeFalse_BypassesDecodeAndPreservesResponse()
    {
        var originBytes = new byte[] { 1, 2, 3 };
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, originBytes, "image/png");
        var cacheMock = CreateMissingCache();
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
        var cacheMock = CreateMissingCache();
        var service = CreateImageProxyService(handler: handler, cacheMock: cacheMock);

        var result = await service.GetOrFetchAsync(
            new ImageProxyRequest("https://example.com/declared-wide.png"),
            allowOriginFetch: true);

        Assert.Equal(ImageProxyResultStatus.TooLarge, result.Status);
        VerifyNeverStored(cacheMock);
    }

    /// <summary>Coalesced callers share one origin request and the same decoded rejection.</summary>
    [Fact]
    public async Task GetOrFetchAsync_CoalescesDecodedResourceRejection()
    {
        var handler = new GatedHttpMessageHandler(PngContractFixture.Create(8193, 1), "image/png");
        var cacheMock = CreateMissingCache();
        var service = CreateImageProxyService(handler: handler, cacheMock: cacheMock);
        var request = new ImageProxyRequest("https://example.com/coalesced-wide.png");

        var first = service.GetOrFetchAsync(request, allowOriginFetch: true);
        await handler.WaitForRequestAsync();
        var second = service.GetOrFetchAsync(request, allowOriginFetch: true);
        handler.Release();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(ImageProxyResultStatus.TooLarge, result.Status));
        Assert.Equal(1, handler.RequestCount);
        VerifyNeverStored(cacheMock);
    }

    /// <summary>Malformed optimized image bytes remain Failed and are never cached.</summary>
    [Fact]
    public async Task GetOrFetchAsync_MalformedImage_RemainsFailed()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, new byte[] { 1, 2, 3 }, "image/png");
        var cacheMock = CreateMissingCache();
        var service = CreateImageProxyService(handler: handler, cacheMock: cacheMock);

        var result = await service.GetOrFetchAsync(
            new ImageProxyRequest("https://example.com/malformed.png"),
            allowOriginFetch: true);

        Assert.Equal(ImageProxyResultStatus.Failed, result.Status);
        VerifyNeverStored(cacheMock);
    }

    /// <summary>Caller cancellation remains observable rather than being mapped to a proxy failure.</summary>
    [Fact]
    public async Task GetOrFetchAsync_PreservesCallerCancellation()
    {
        var cacheMock = CreateMissingCache();
        var service = CreateImageProxyService(cacheMock: cacheMock);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetOrFetchAsync(
            new ImageProxyRequest("https://example.com/cancelled.png"),
            allowOriginFetch: true,
            cancellation.Token));

        VerifyNeverStored(cacheMock);
    }

    /// <summary>A rejected refresh leaves the stale cache authority untouched.</summary>
    [Fact]
    public async Task RefreshAsync_DecodedResourceRejection_PreservesStaleEntry()
    {
        var handler = new MockHttpMessageHandler(
            HttpStatusCode.OK,
            PngContractFixture.Create(8193, 1),
            "image/png");
        var cacheMock = new Mock<IProxiedImageCacheService>();
        var service = CreateImageProxyService(handler: handler, cacheMock: cacheMock);

        var result = await service.RefreshAsync(new ImageProxyRequest("https://example.com/stale-wide.png"));

        Assert.Equal(ImageProxyResultStatus.TooLarge, result.Status);
        VerifyNeverStored(cacheMock);
        cacheMock.Verify(c => c.GetAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>One coalesced rejection emits one bounded structured policy event without the origin URL.</summary>
    [Fact]
    public async Task GetOrFetchAsync_CoalescedRejection_LogsOneBoundedEvent()
    {
        using var provider = new TestLogProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var handler = new GatedHttpMessageHandler(PngContractFixture.Create(8193, 1), "image/png");
        var cacheMock = CreateMissingCache();
        var settingsMock = new Mock<IApplicationSettingsService>();
        settingsMock.Setup(service => service.GetSettings()).Returns(new ApplicationSettings());
        var service = new ImageProxyService(
            new HttpClient(handler),
            cacheMock.Object,
            settingsMock.Object,
            Mock.Of<IServiceScopeFactory>(),
            loggerFactory.CreateLogger<ImageProxyService>());
        var request = new ImageProxyRequest("https://example.com/private.png?token=secret");

        var first = service.GetOrFetchAsync(request, allowOriginFetch: true);
        await handler.WaitForRequestAsync();
        var second = service.GetOrFetchAsync(request, allowOriginFetch: true);
        handler.Release();
        await Task.WhenAll(first, second);

        var entry = Assert.Single(provider.Entries, item => item.Fields.ContainsKey("LimitName"));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("width", entry.Fields["LimitName"]);
        Assert.Equal(8193L, entry.Fields["Observed"]);
        Assert.Equal(8192L, entry.Fields["Limit"]);
        Assert.DoesNotContain("private.png", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", entry.Message, StringComparison.Ordinal);
    }

    private static Mock<IProxiedImageCacheService> CreateMissingCache()
    {
        var cacheMock = new Mock<IProxiedImageCacheService>();
        cacheMock.Setup(cache => cache.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(new ProxiedImageCacheResult(ProxiedImageCacheStatus.Miss, null, null, null));
        cacheMock.SetReturnsDefault(Task.FromResult(ProxiedImageCacheStoreResult.Success));
        return cacheMock;
    }

    private static void VerifyNeverStored(Mock<IProxiedImageCacheService> cacheMock) =>
        cacheMock.Verify(cache => cache.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>()), Times.Never);

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
