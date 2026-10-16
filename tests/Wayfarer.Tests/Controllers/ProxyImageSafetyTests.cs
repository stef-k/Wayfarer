using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>HTTP compatibility and safe conditional responses at the anonymous proxy route.</summary>
public partial class TripViewerControllerTests
{
    /// <summary>Windows refuses both modes before cache work or conditional image headers; Linux retains 304.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProxyImage_PlatformPolicyPrecedesCacheAndConditionalResponse(bool optimize)
    {
        var bytes = ImageProxyTestFactory.Raster();
        var proxy = new Mock<IImageProxyService>(MockBehavior.Strict);
        proxy.Setup(p => p.GetOrFetchAsync(It.Is<ImageProxyRequest>(r => r.Optimize == optimize), false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageProxyResult(ImageProxyResultStatus.FreshHit, "key", bytes, "image/jpeg"));
        using var db = CreateDbContext();
        var controller = BuildController(db, proxyService: proxy.Object);
        var etag = $"\"raster-v1-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))}\"";
        controller.Request.Headers.IfNoneMatch = etag;

        var result = Assert.IsType<StatusCodeResult>(await controller.ProxyImage("https://example.com/image", optimize: optimize));

        Assert.Equal(OperatingSystem.IsWindows() ? 501 : 304, result.StatusCode);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
            Assert.False(controller.Response.Headers.ContainsKey("ETag"));
            Assert.False(controller.Response.Headers.ContainsKey("X-Wayfarer-Image-Cache"));
            Assert.False(controller.Response.Headers.ContainsKey("X-Content-Type-Options"));
        }
        else
        {
            Assert.Equal(etag, controller.Response.Headers.ETag.ToString());
            proxy.Verify(p => p.GetOrFetchAsync(It.IsAny<ImageProxyRequest>(), false, It.IsAny<CancellationToken>()), Times.Once);
        }
        proxy.VerifyNoOtherCalls();
    }

    /// <summary>Cover visibility and SSRF checks precede the shared platform decision without touching image services.</summary>
    [Theory]
    [InlineData(true, true, "https://example.com/cover", 0)]
    [InlineData(false, true, "https://example.com/cover", 404)]
    [InlineData(true, false, "https://example.com/cover", 404)]
    [InlineData(true, true, null, 404)]
    [InlineData(true, true, "http://127.0.0.1/private", 400)]
    [InlineData(true, true, "file:///private", 400)]
    public async Task CoverImage_PlatformPolicyPreservesValidation(bool exists, bool isPublic, string? url, int expectedStatus)
    {
        using var db = CreateDbContext();
        var id = Guid.NewGuid();
        if (exists)
        {
            db.Trips.Add(new Trip { Id = id, UserId = "owner", Name = "Cover fixture", IsPublic = isPublic, CoverImageUrl = url });
            await db.SaveChangesAsync();
        }
        var proxy = new Mock<IImageProxyService>(MockBehavior.Strict);
        proxy.Setup(p => p.GetOrFetchAsync(It.IsAny<ImageProxyRequest>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageProxyResult(ImageProxyResultStatus.FreshHit, "key", "safe"u8.ToArray(), "image/png"));
        var controller = BuildController(db, proxyService: proxy.Object);

        var result = await controller.GetCoverImage(id);

        if (expectedStatus == 0 && !OperatingSystem.IsWindows())
        {
            Assert.IsType<FileContentResult>(result);
            proxy.Verify(p => p.GetOrFetchAsync(It.IsAny<ImageProxyRequest>(), false, It.IsAny<CancellationToken>()), Times.Once);
        }
        else
        {
            var status = result as StatusCodeResult;
            var responseStatus = status?.StatusCode ?? Assert.IsType<BadRequestObjectResult>(result).StatusCode;
            Assert.Equal(expectedStatus == 0 ? 501 : expectedStatus, responseStatus);
            if (responseStatus == 501)
                Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
            Assert.False(controller.Response.Headers.ContainsKey("ETag"));
            Assert.False(controller.Response.Headers.ContainsKey("X-Wayfarer-Image-Cache"));
        }
        proxy.VerifyNoOtherCalls();
    }

    /// <summary>No validator, including the old vulnerable key, can skip resolving safe bytes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProxyImage_UnsafeLegacyCannotReturn304(bool stale)
    {
        const string url = "https://example.com/old-active";
        var cache = new Mock<IProxiedImageCacheService>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProxiedImageCacheResult(stale ? ProxiedImageCacheStatus.StaleHit : ProxiedImageCacheStatus.FreshHit,
                "<html>active</html>"u8.ToArray(), "image/png", null));
        var controller = BuildController(CreateDbContext(),
            handler: new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound)), imageCacheService: cache.Object);
        controller.Request.Headers.IfNoneMatch = $"\"{ImageProxyHelper.ComputeImageCacheKey(url, null, null, null, true)}\"";
        Assert.IsType<NotFoundResult>(await controller.ProxyImage(url));
        Assert.False(controller.Response.Headers.ContainsKey("ETag"));
    }

    /// <summary>The old key cannot revalidate even a safe entry; output validators change with actual bytes.</summary>
    [Fact]
    public async Task ProxyImage_EtagVersionsActualSafeRepresentation()
    {
        const string url = "https://example.com/changed";
        var cache = new Mock<IProxiedImageCacheService>();
        var bytes = ImageProxyTestFactory.Raster();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ProxiedImageCacheResult(ProxiedImageCacheStatus.FreshHit, bytes, "text/html", null));
        var controller = BuildController(CreateDbContext(), imageCacheService: cache.Object);
        controller.Request.Headers.IfNoneMatch = $"\"{ImageProxyHelper.ComputeImageCacheKey(url, null, null, null, true)}\"";
        Assert.IsType<FileContentResult>(await controller.ProxyImage(url));
        var etag = controller.Response.Headers.ETag.ToString();
        Assert.StartsWith("\"raster-v1-", etag);
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"]);
        controller.Request.Headers.IfNoneMatch = etag;
        bytes = ImageProxyTestFactory.Raster("png");
        var changed = Assert.IsType<FileContentResult>(await controller.ProxyImage(url));
        Assert.Equal("image/png", changed.ContentType);
        Assert.NotEqual(etag, controller.Response.Headers.ETag.ToString());
    }

    /// <summary>Cache/origin exhaustion returns 503; a busy cache never enters origin admission and forwards cancellation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProxyImage_UnavailableMapsTo503_AndForwardsRequestCancellation(bool cacheBusy)
    {
        using var abort = new CancellationTokenSource();
        var proxy = new Mock<IImageProxyService>();
        proxy.Setup(p => p.GetOrFetchAsync(It.IsAny<ImageProxyRequest>(), false, abort.Token))
            .ReturnsAsync(new ImageProxyResult(cacheBusy ? ImageProxyResultStatus.Unavailable : ImageProxyResultStatus.OriginRequired,
                "key", null, null));
        proxy.Setup(p => p.GetOrFetchAsync(It.IsAny<ImageProxyRequest>(), true, abort.Token))
            .ReturnsAsync(new ImageProxyResult(ImageProxyResultStatus.Unavailable, "key", null, null));
        var controller = BuildController(CreateDbContext(), proxyService: proxy.Object);
        controller.HttpContext.RequestAborted = abort.Token;
        var result = Assert.IsType<StatusCodeResult>(await controller.ProxyImage("https://example.com/busy"));
        Assert.Equal(503, result.StatusCode);
        proxy.Verify(p => p.GetOrFetchAsync(It.IsAny<ImageProxyRequest>(), false, abort.Token), Times.Once);
        proxy.Verify(p => p.GetOrFetchAsync(It.IsAny<ImageProxyRequest>(), true, abort.Token), cacheBusy ? Times.Never : Times.Once);
        proxy.Setup(p => p.GetOrFetchAsync(It.IsAny<ImageProxyRequest>(), false, abort.Token))
            .ThrowsAsync(new OperationCanceledException(abort.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.ProxyImage("https://example.com/cancelled"));
    }

    /// <summary>Authenticated web principals keep bypassing anonymous miss admission.</summary>
    [Fact]
    public async Task ProxyImage_AuthenticatedMissesBypassAnonymousLimiter()
    {
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(s => s.GetSettings()).Returns(new ApplicationSettings
        {
            ProxyImageRateLimitEnabled = true, ProxyImageRateLimitPerMinute = 1
        });
        var controller = BuildController(CreateDbContext(), settingsService: settings.Object,
            handler: new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(ImageProxyTestFactory.Raster())
            }));
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "user") }, "cookie"));
        for (var i = 0; i < 2; i++)
            Assert.IsType<FileContentResult>(await controller.ProxyImage($"https://example.com/auth-{i}"));
    }
}
