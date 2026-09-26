using System.Net;
using System.Text;
using Moq;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves admission from actual bytes for both origin modes and untrusted legacy cache hits.</summary>
public partial class ImageProxyServiceTests
{
    /// <summary>All four formats accept lying or absent MIME without changing established output routing.</summary>
    [Theory]
    [InlineData("jpeg", "image/jpeg")]
    [InlineData("png", "image/png")]
    [InlineData("gif", "image/png")]
    [InlineData("webp", "image/jpeg")]
    public async Task RasterPolicy_SupportedBytesDetermineMime(string format, string optimizedMime)
    {
        var bytes = ImageProxyTestFactory.Raster(format);
        foreach (var optimize in new[] { false, true })
        foreach (var claimedMime in new string?[] { null, "text/html", "image/svg+xml" })
        {
            var handler = new CountingHttpMessageHandler(() =>
            {
                var content = new ByteArrayContent(bytes);
                if (claimedMime != null) content.Headers.ContentType = new(claimedMime);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            });
            var cache = CreateMissingCache();
            var service = CreateImageProxyService(handler, cache);
            var result = await service.GetOrFetchAsync(new("https://example.com/raster", Optimize: optimize), true);
            Assert.Equal(ImageProxyResultStatus.Fetched, result.Status);
            Assert.Equal(optimize ? optimizedMime : $"image/{format}", result.ContentType);
            if (!optimize) Assert.Equal(bytes, result.Bytes);
            cache.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), result.ContentType!,
                It.Is<CancellationToken>(token => token.CanBeCanceled)), Times.Once);
        }
    }

    /// <summary>Active, malformed and incidental decoder formats never reach cache publication.</summary>
    [Theory]
    [InlineData("html")]
    [InlineData("svg")]
    [InlineData("xml")]
    [InlineData("random")]
    [InlineData("truncated")]
    [InlineData("bmp")]
    [InlineData("tiff")]
    [InlineData("pbm")]
    [InlineData("tga")]
    [InlineData("qoi")]
    [InlineData("ico")]
    public async Task RasterPolicy_RejectsUnsafeBytesInBothModes(string kind)
    {
        var bytes = UnsafeBytes(kind);
        foreach (var optimize in new[] { true, false })
        {
            var cache = CreateMissingCache();
            var service = CreateImageProxyService(new MockHttpMessageHandler(HttpStatusCode.OK, bytes, "image/png"), cache);
            var result = await service.GetOrFetchAsync(new("https://example.com/unsafe", Optimize: optimize), true);
            Assert.Equal(ImageProxyResultStatus.Failed, result.Status);
            Assert.False(result.HasBytes);
            VerifyNeverStored(cache);
        }
    }

    /// <summary>Invalid fresh/stale legacy bytes cannot bypass miss admission or warm-up validation.</summary>
    [Theory]
    [InlineData(ProxiedImageCacheStatus.FreshHit)]
    [InlineData(ProxiedImageCacheStatus.StaleHit)]
    public async Task RasterPolicy_UnsafeLegacyIsAnUnusableMiss(ProxiedImageCacheStatus status)
    {
        var cache = CreateMissingCache();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProxiedImageCacheResult(status, UnsafeBytes("html"), "image/png", null));
        var handler = new CountingHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = CreateContent(ImageProxyTestFactory.Raster(), "text/html")
        });
        var service = CreateImageProxyService(handler, cache);
        var result = await service.GetOrFetchAsync(new("https://example.com/legacy"), false);
        Assert.Equal(ImageProxyResultStatus.OriginRequired, result.Status);
        Assert.False(result.HasBytes);
        Assert.Equal(0, handler.RequestCount);
        Assert.True(await service.FetchAndCacheAsync("https://example.com/legacy"));
        Assert.Equal(1, handler.RequestCount);
    }

    /// <summary>Safe legacy bytes retain freshness and get canonical MIME without origin admission.</summary>
    [Fact]
    public async Task RasterPolicy_ValidLegacyIgnoresStoredMime()
    {
        var cache = CreateMissingCache();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProxiedImageCacheResult(ProxiedImageCacheStatus.FreshHit,
                ImageProxyTestFactory.Raster("png"), "text/html", null));
        var service = CreateImageProxyService(cacheMock: cache);
        var result = await service.GetOrFetchAsync(new("https://example.com/legacy"), false);
        Assert.Equal(ImageProxyResultStatus.FreshHit, result.Status);
        Assert.Equal("image/png", result.ContentType);
        VerifyNeverStored(cache);
    }

    /// <summary>Provides representative content, rather than relying on misleading MIME strings.</summary>
    private static byte[] UnsafeBytes(string kind) => kind switch
    {
        "html" => Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"),
        "svg" => Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"),
        "xml" => Encoding.UTF8.GetBytes("<?xml version='1.0'?><image/>"),
        "random" => [1, 2, 3, 4, 5],
        "ico" => [0, 0, 1, 0, 1, 0],
        "truncated" => ImageProxyTestFactory.Raster("png")[..32],
        _ => ImageProxyTestFactory.Raster(kind)
    };
}
