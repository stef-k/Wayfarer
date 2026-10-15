using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SixLabors.ImageSharp;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Util;
using Wayfarer.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Services;

/// <summary>Exercise real codec processes at the existing proxy seam with controlled origin/cache I/O.</summary>
[Collection(ImageProxyStaticStateTestCollection.Name)]
public sealed class ImageProxyRustIntegrationTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>The retained exact 236-byte counterexample cannot be served, cached or trusted on rollback.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MaliciousWebp_FailsOriginAndLegacyCache(bool optimize)
    {
        var bytes = File.ReadAllBytes(Path.Combine(ImageHelperSupervisorTests.Fixtures, "huffman-exact-extent.webp"));
        Assert.Equal(236, bytes.Length);
        Assert.Equal("FE8DDB4C9FE18AF4B4F62F94B756EA3AF49810ED73AE789C594A94832CA4FFA8",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
        var cache = Cache();
        using var handler = new Origin(bytes);
        var service = Service(handler, cache);
        var request = new ImageProxyRequest("https://example.com/counterexample.webp", Optimize: optimize);
        Assert.Equal(ImageProxyResultStatus.Failed, (await service.GetOrFetchAsync(request, true)).Status);
        cache.Verify(x => x.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        foreach (var status in new[] { ProxiedImageCacheStatus.FreshHit, ProxiedImageCacheStatus.StaleHit })
        {
            cache.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProxiedImageCacheResult(status, bytes, "image/jpeg", null));
            Assert.Equal(ImageProxyResultStatus.OriginRequired, (await service.GetOrFetchAsync(request, false)).Status);
        }
        Assert.Equal(1, handler.Requests);
    }

    /// <summary>Four occupied origin operations leave eight independently validated cache hits available.</summary>
    [Fact]
    public async Task FourOrigins_RejectFifthWhileEightCacheHitsComplete()
    {
        var cache = Cache();
        var bytes = ImageProxyTestFactory.Raster("png");
        var hit = new ImageProxyRequest("https://example.com/cached.png");
        var hitKey = ImageProxyHelper.ComputeImageCacheKey(hit.Url, null, null, null, true);
        cache.Setup(x => x.GetAsync(hitKey, It.IsAny<CancellationToken>())).ReturnsAsync(
            new ProxiedImageCacheResult(ProxiedImageCacheStatus.FreshHit, bytes, "text/html", null));
        using var origin = new Origin(bytes, gated: true);
        var service = Service(origin, cache);
        var pending = Enumerable.Range(0, 4).Select(i => service.GetOrFetchAsync(
            new($"https://example.com/origin-{i}.png"), true)).ToArray();
        try
        {
            await origin.AllFour.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var rejected = await Task.WhenAll(Enumerable.Range(4, 16).Select(i => service.GetOrFetchAsync(
                new($"https://example.com/burst-{i}.png"), true)));
            Assert.All(rejected, result => Assert.Equal(ImageProxyResultStatus.Unavailable, result.Status));
            var hits = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.GetOrFetchAsync(hit, false)));
            Assert.All(hits, result => {
                Assert.Equal(ImageProxyResultStatus.FreshHit, result.Status);
                Assert.Equal("image/png", result.ContentType);
                Assert.Equal(bytes, result.Bytes);
            });
            Assert.Equal(4, origin.Requests);
        }
        finally
        {
            origin.Release.TrySetResult();
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.All(pending, task => Assert.Equal(ImageProxyResultStatus.Fetched, task.Result.Status));
    }

    /// <summary>A finite client feeder loads real mixed-format trip images without changing non-queuing admission.</summary>
    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(40)]
    public async Task TripLoad_CompletesBoundedBatches(int count)
    {
        var names = new[] { "photo.jpg", "photo.webp", "opaque.png", "photo.gif" };
        var bytes = names.Select(name => File.ReadAllBytes(Path.Combine(ImageHelperSupervisorTests.Fixtures, name))).ToArray();
        using var origin = new Origin(bytes);
        var service = Service(origin, Cache());
        var clock = Stopwatch.StartNew();
        double? first = null;
        for (var start = 0; start < count; start += 4)
        {
            var tasks = Enumerable.Range(start, Math.Min(4, count - start)).Select(async i => {
                var result = await service.GetOrFetchAsync(new($"https://example.com/trip-{i}.image"), true);
                Assert.Equal(ImageProxyResultStatus.Fetched, result.Status);
                using var image = Image.Load(result.Bytes!);
                Assert.InRange(image.Width, 1, 2048);
                Assert.InRange(image.Height, 1, 2048);
                Assert.Null(image.Metadata.ExifProfile);
                Assert.Null(image.Metadata.XmpProfile);
                lock (clock) first ??= clock.Elapsed.TotalMilliseconds;
            });
            await Task.WhenAll(tasks);
        }
        using var host = Process.GetCurrentProcess();
        output.WriteLine("count={0}; success={0}; rejected=0; first-image-ms={1:F3}; completion-ms={2:F3}; host-peak-RSS={3}",
            count, first, clock.Elapsed.TotalMilliseconds, host.PeakWorkingSet64);
        // In the established finite-container qualification this includes the test host and all children.
        if (File.Exists("/sys/fs/cgroup/memory.max"))
        {
            output.WriteLine("cgroup-memory-max={0}; peak={1}; events={2}",
                File.ReadAllText("/sys/fs/cgroup/memory.max").Trim(),
                File.ReadAllText("/sys/fs/cgroup/memory.peak").Trim(),
                File.ReadAllText("/sys/fs/cgroup/memory.events").Trim());
        }
        Assert.Equal(count, origin.Requests);
    }

    /// <summary>Default quality aliases explicit 90 without colliding with a distinct caller override.</summary>
    [Fact]
    public void WebDefaults_CacheIdentityMatchesResolvedQuality()
    {
        const string url = "https://example.com/photo.jpg";
        Assert.Equal(ImageProxyHelper.ComputeImageCacheKey(url, null, null, 90, true),
            ImageProxyHelper.ComputeImageCacheKey(url, null, null, null, true));
        Assert.NotEqual(ImageProxyHelper.ComputeImageCacheKey(url, null, null, 95, true),
            ImageProxyHelper.ComputeImageCacheKey(url, null, null, null, true));
    }

    /// <summary>Reuse existing mocked cache contracts; publication success is needed for proxy success.</summary>
    private static Mock<IProxiedImageCacheService> Cache()
    {
        var cache = new Mock<IProxiedImageCacheService>();
        cache.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(
            new ProxiedImageCacheResult(ProxiedImageCacheStatus.Miss, null, null, null));
        cache.SetReturnsDefault(Task.FromResult(ProxiedImageCacheStoreResult.Success));
        return cache;
    }

    /// <summary>Reuse scoped origin ownership without starting a database or opening a network socket.</summary>
    private static ImageProxyService Service(HttpMessageHandler handler, Mock<IProxiedImageCacheService> cache)
    {
        var client = new HttpClient(handler);
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(x => x.GetSettings()).Returns(new ApplicationSettings());
        return new ImageProxyService(client, cache.Object, settings.Object,
            ImageProxyTestFactory.ScopeFactory(client, cache.Object, settings.Object), NullLogger<ImageProxyService>.Instance);
    }

    /// <summary>Restore only the existing process-wide origin coordinator after owned work completes.</summary>
    public void Dispose() => ImageProxyService.ResetStaticStateForTesting();

    /// <summary>Controlled origin bytes and an optional four-request barrier reuse ordinary HTTP handler tests.</summary>
    private sealed class Origin(byte[][] sources, bool gated = false) : HttpMessageHandler
    {
        internal Origin(byte[] source, bool gated = false) : this([source], gated) { }
        internal readonly TaskCompletionSource AllFour = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Requests;

        /// <summary>Count admitted originals and preserve their cancellation boundary.</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var index = Interlocked.Increment(ref Requests) - 1;
            if (index == 3) AllFour.TrySetResult();
            if (gated) await Release.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(sources[index % sources.Length]) };
        }
    }
}
