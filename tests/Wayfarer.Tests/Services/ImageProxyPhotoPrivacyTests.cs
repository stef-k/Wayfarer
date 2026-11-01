using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Final origin policy prevents the public image proxy retaining revocable photo bytes.</summary>
[Collection(ImageProxyStaticStateTestCollection.Name)]
public sealed class ImageProxyPhotoPrivacyTests
{
    /// <summary>Final redirected origins are checked before body reads; ordinary cacheable origins still succeed.</summary>
    [Theory]
    [InlineData("no-store", false)]
    [InlineData("private, max-age=300", false)]
    [InlineData("public, max-age=300", true)]
    public async Task FinalOriginPolicyControlsSuccessfulProxyStorage(string directives, bool accepted)
    {
        ImageProxyService.ResetStaticStateForTesting();
        var cache = new Mock<IProxiedImageCacheService>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProxiedImageCacheResult(ProxiedImageCacheStatus.Miss, null, null, null));
        cache.SetReturnsDefault(Task.FromResult(ProxiedImageCacheStoreResult.Success));
        var settings = new Mock<IApplicationSettingsService>(); settings.Setup(s => s.GetSettings()).Returns(new ApplicationSettings());
        var handler = new FinalOrigin(directives, accepted);
        using var client = new HttpClient(handler);
        var service = new ImageProxyService(client, cache.Object, settings.Object,
            ImageProxyTestFactory.ScopeFactory(client, cache.Object, settings.Object), NullLogger<ImageProxyService>.Instance);
        Assert.Equal(accepted, await service.FetchAndCacheAsync("https://example.com/redirect-to-photo"));
        cache.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            accepted ? Times.Once() : Times.Never());
    }

    /// <summary>Models HttpClient's final response after redirect resolution; rejected bodies must never be read.</summary>
    private sealed class FinalOrigin(string directives, bool readable) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://alias.example/Public/Trips/id/owner-photo/version"),
                Content = readable ? new ByteArrayContent(ImageProxyTestFactory.Raster()) : new StreamContent(new Unreadable())
            };
            response.Headers.CacheControl = CacheControlHeaderValue.Parse(directives);
            return Task.FromResult(response);
        }
    }
    private sealed class Unreadable : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Private response body was read.");
    }
}
