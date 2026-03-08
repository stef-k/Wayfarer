using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, jpegBytes, "image/jpeg");
        var cacheMock = new Mock<IProxiedImageCacheService>();

        // No existing cache entry
        cacheMock.Setup(c => c.GetAsync(It.IsAny<string>()))
            .ReturnsAsync((ValueTuple<byte[], string>?)null);

        var service = CreateImageProxyService(handler: handler, cacheMock: cacheMock);

        var result = await service.FetchAndCacheAsync("https://example.com/photo.jpg");

        Assert.True(result);
        cacheMock.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task FetchAndCacheAsync_ReturnsFalse_WhenUpstreamFails()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.NotFound, Array.Empty<byte>(), "text/html");
        var cacheMock = new Mock<IProxiedImageCacheService>();
        cacheMock.Setup(c => c.GetAsync(It.IsAny<string>()))
            .ReturnsAsync((ValueTuple<byte[], string>?)null);

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
            .ReturnsAsync((new byte[] { 1, 2, 3 }, "image/jpeg"));

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
            .ReturnsAsync((ValueTuple<byte[], string>?)null);

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
            .ReturnsAsync((ValueTuple<byte[], string>?)null);

        var service = CreateImageProxyService(handler: handler, cacheMock: cacheMock, settingsMock: settingsMock);

        var result = await service.FetchAndCacheAsync("https://example.com/big.jpg");

        Assert.False(result);
        cacheMock.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Creates an <see cref="ImageProxyService"/> with test doubles.
    /// </summary>
    private ImageProxyService CreateImageProxyService(
        MockHttpMessageHandler? handler = null,
        Mock<IProxiedImageCacheService>? cacheMock = null,
        Mock<IApplicationSettingsService>? settingsMock = null)
    {
        handler ??= new MockHttpMessageHandler(HttpStatusCode.OK, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, "image/jpeg");
        cacheMock ??= new Mock<IProxiedImageCacheService>();
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
}
