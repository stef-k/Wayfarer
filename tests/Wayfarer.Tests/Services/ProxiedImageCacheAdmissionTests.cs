using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Util;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Exercise admission at the real disk reader with small ordinary files.</summary>
public partial class ProxiedImageCacheServiceTests
{
    /// <summary>A present cache file waits before reading when all validation capacity is active.</summary>
    [Fact]
    public async Task CachedRead_WaitsForValidationAdmissionAndHonorsCancellation()
    {
        var service = CreateService();
        var key = Key("admission");
        Assert.True((await service.SetAsync(key, [1, 2, 3], "image/png")).Stored);
        var allocations = 0;
        ProxiedImageCacheService.BeforeAllocationForTesting = _ =>
        {
            Interlocked.Increment(ref allocations);
            return Task.CompletedTask;
        };
        using var stopChildren = new CancellationTokenSource();
        var helpers = Enumerable.Range(0, 8).Select(_ => new FaultHelper("time.sleep(60)")).ToArray();
        var active = helpers.Select(helper => helper.Supervisor.ValidateAsync(
            ReadOnlyMemory<byte>.Empty, cancellationToken: stopChildren.Token)).ToArray();
        try
        {
            await Task.WhenAll(helpers.Select(helper => helper.WaitForPid()));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync(key, cancellation.Token));
            Assert.Equal(0, allocations);
            Assert.Equal(0, ImageHelperSupervisor.ValidationStatistics.CurrentQueuedCount);
        }
        finally
        {
            stopChildren.Cancel();
            foreach (var task in active)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            foreach (var helper in helpers)
            {
                ImageHelperSupervisorTests.AssertReaped(await helper.WaitForPid());
                helper.Dispose();
            }
        }
    }

    /// <summary>Fresh, stale and concurrent-refresh paths all fail closed before allocation, without origin work.</summary>
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task CachedRead_TimeoutIsUnavailableAcrossDiskPaths(bool stale, bool alternate)
    {
        var db = CreateDbContext();
        var cache = CreateService(db: db);
        var request = new ImageProxyRequest("https://example.com/admission.png");
        var key = ImageProxyHelper.ComputeImageCacheKey(request.Url, null, null, null, true);
        var bytes = ImageProxyTestFactory.Raster("png");
        await cache.SetAsync(key, bytes, "text/html");
        if (stale)
        {
            db.ImageCacheMetadata.Single().CreatedAt = DateTime.UtcNow.AddDays(-100);
            await db.SaveChangesAsync();
        }
        if (alternate)
            ProxiedImageCacheService.SetBeforeFileReadForTesting(async (_, _) =>
            {
                await cache.SetAsync(key, bytes, "text/html");
                if (stale)
                {
                    db.ImageCacheMetadata.Single().CreatedAt = DateTime.UtcNow.AddDays(-100);
                    await db.SaveChangesAsync();
                }
            });
        var allocations = 0;
        ProxiedImageCacheService.BeforeAllocationForTesting = _ =>
        {
            Interlocked.Increment(ref allocations);
            return Task.CompletedTask;
        };
        using var held = await ImageHelperSupervisor.AdmitCacheReadAsync(200 * 1024 * 1024, default);
        using var origin = new AdmissionOrigin(bytes);
        using var client = new HttpClient(origin);
        var proxy = AdmissionProxy(cache, client);
        var timeout = ImageHelperSupervisor.CacheAdmissionTimeout;
        ImageHelperSupervisor.CacheAdmissionTimeout = TimeSpan.FromMilliseconds(100);
        try
        {
            foreach (var allowOrigin in new[] { false, true })
                Assert.Equal(ImageProxyResultStatus.Unavailable,
                    (await proxy.GetOrFetchAsync(request, allowOrigin)).Status);
            Assert.False(await proxy.FetchAndCacheAsync(request.Url));
            Assert.Equal(0, allocations);
            Assert.Equal(0, origin.Requests);
            Assert.Equal(0, ImageHelperSupervisor.ValidationStatistics.CurrentQueuedCount);
            Assert.Single(db.ImageCacheMetadata);
        }
        finally { ImageHelperSupervisor.CacheAdmissionTimeout = timeout; }
    }

    /// <summary>A real miss never waits for cache trust capacity and retains the separate origin transform budget.</summary>
    [Fact]
    public async Task CachedRead_SaturationDoesNotBlockAnOrdinaryOriginMiss()
    {
        using var held = await ImageHelperSupervisor.AdmitCacheReadAsync(200 * 1024 * 1024, default);
        using var origin = new AdmissionOrigin(ImageProxyTestFactory.Raster("png"));
        using var client = new HttpClient(origin);
        var result = await AdmissionProxy(CreateService(), client).GetOrFetchAsync(
            new("https://example.com/missing.png"), true);
        Assert.Equal(ImageProxyResultStatus.Fetched, result.Status);
        Assert.Equal(1, origin.Requests);
        Assert.Equal(0, ImageHelperSupervisor.ValidationStatistics.CurrentQueuedCount);
    }

    /// <summary>The same real read lease survives read completion and borrowed helper cancellation/crash through reap.</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CachedRead_LeaseSurvivesValidationUntilReapAndDisposal(bool crash)
    {
        var cache = CreateService();
        var key = Key("reap");
        await cache.SetAsync(key, ImageProxyTestFactory.Raster("png"), "image/png");
        using var cached = await cache.GetAsync(key);
        Assert.NotNull(cached.ValidationAdmission);
        Assert.Equal(7, ImageHelperSupervisor.ValidationStatistics.CurrentAvailablePermits);
        using var remaining = await ImageHelperSupervisor.AdmitCacheReadAsync(175 * 1024 * 1024, default);
        using var helper = new FaultHelper(crash ? "os.kill(os.getpid(), signal.SIGKILL)" : "time.sleep(60)");
        using var cancellation = new CancellationTokenSource();
        var validation = helper.Supervisor.ValidateAdmittedAsync(cached.Bytes!, 50 * 1024 * 1024,
            cached.ValidationAdmission!, cancellation.Token);
        try
        {
            var pid = await helper.WaitForPid();
            using var waiting = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync(key, waiting.Token));
            if (crash)
                Assert.Equal(ImageTransformStatus.Unavailable, (await validation).Status);
            else
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validation);
            }
            ImageHelperSupervisorTests.AssertReaped(pid);
            Assert.Equal(0, ImageHelperSupervisor.ValidationStatistics.CurrentAvailablePermits);
        }
        finally
        {
            cancellation.Cancel();
            try { await validation; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        remaining.Dispose();
        Assert.Equal(7, ImageHelperSupervisor.ValidationStatistics.CurrentAvailablePermits);
        cached.Dispose();
        Assert.Equal(8, ImageHelperSupervisor.ValidationStatistics.CurrentAvailablePermits);
    }

    /// <summary>Short/growing files release admission on real I/O failure and cannot yield partially read bytes.</summary>
    [Theory]
    [InlineData(0)] [InlineData(4)]
    public async Task CachedRead_LengthChangeReleasesAdmissionAndRecovers(int length)
    {
        var cache = CreateService();
        var key = Key("read-failure");
        await cache.SetAsync(key, [1, 2, 3], "image/png");
        ProxiedImageCacheService.BeforeAllocationForTesting = path =>
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            file.SetLength(length);
            return Task.CompletedTask;
        };
        using var failed = await cache.GetAsync(key);
        Assert.Equal(ProxiedImageCacheStatus.DiskMissingOrError, failed.Status);
        Assert.Null(failed.Bytes);
        Assert.Equal(8, ImageHelperSupervisor.ValidationStatistics.CurrentAvailablePermits);
        ProxiedImageCacheService.BeforeAllocationForTesting = null;
        await cache.SetAsync(key, [1, 2, 3], "image/png");
        using var recovered = await cache.GetAsync(key);
        Assert.Equal(new byte[] { 1, 2, 3 }, recovered.Bytes);
    }

    /// <summary>Weighted reservations qualify 50/100/200 MiB concurrency without allocating any large fixtures.</summary>
    [Theory]
    [InlineData(25, 8)] [InlineData(50, 4)] [InlineData(100, 2)] [InlineData(200, 1)]
    public async Task CachedRead_EncodedSizeDeterminesEffectiveConcurrency(int megabytes, int concurrency)
    {
        var leases = new List<IDisposable>();
        try
        {
            for (var index = 0; index < concurrency; index++)
                leases.Add(await ImageHelperSupervisor.AdmitCacheReadAsync(megabytes * 1024 * 1024, default));
            Assert.Equal(0, ImageHelperSupervisor.ValidationStatistics.CurrentAvailablePermits);
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ImageHelperSupervisor.AdmitCacheReadAsync(1, cancelled.Token));
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
        Assert.Equal(8, ImageHelperSupervisor.ValidationStatistics.CurrentAvailablePermits);
    }

    /// <summary>Excess sustained demand is rejected before allocation; bounded waiters cancel without leaking permits.</summary>
    [Fact]
    public async Task CachedRead_WaiterLimitFailsClosedBeforeAllocation()
    {
        using var held = await ImageHelperSupervisor.AdmitCacheReadAsync(200 * 1024 * 1024, default);
        using var cancellation = new CancellationTokenSource();
        var readers = new List<ProxiedImageCacheService>();
        var key = Key("bounded-waiters");
        for (var index = 0; index < 33; index++)
        {
            var reader = CreateService();
            await reader.SetAsync(key, [1, 2, 3], "image/png");
            readers.Add(reader);
        }
        var allocations = 0;
        ProxiedImageCacheService.BeforeAllocationForTesting = _ =>
        {
            Interlocked.Increment(ref allocations);
            return Task.CompletedTask;
        };
        var pending = readers.Take(32).Select(reader => reader.GetAsync(key, cancellation.Token)).ToArray();
        try
        {
            Assert.Equal(32, ImageHelperSupervisor.ValidationStatistics.CurrentQueuedCount);
            await Assert.ThrowsAsync<ImageHelperUnavailableException>(() => readers[32].GetAsync(key));
            Assert.Equal(0, allocations);
        }
        finally
        {
            cancellation.Cancel();
            foreach (var task in pending)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
        Assert.Equal(0, ImageHelperSupervisor.ValidationStatistics.CurrentQueuedCount);
    }

    /// <summary>Concurrent warm pages wait before allocation, then validate every ordinary same/different-key image.</summary>
    [Theory]
    [InlineData(10, false)] [InlineData(20, false)]
    [InlineData(10, true)] [InlineData(20, true)]
    public async Task CachedRead_WarmBurstCompletesWithoutSheddingOrOrigin(int count, bool sameKey)
    {
        var requests = new List<(ImageProxyService Proxy, ImageProxyRequest Request, string Mime)>();
        using var origin = new AdmissionOrigin(ImageProxyTestFactory.Raster("png"));
        using var client = new HttpClient(origin);
        for (var index = 0; index < count; index++)
        {
            var request = new ImageProxyRequest($"https://example.com/warm-{(sameKey ? 0 : index)}.png");
            var cache = CreateService(); // Each real request owns a scoped DbContext.
            var key = ImageProxyHelper.ComputeImageCacheKey(request.Url, null, null, null, true);
            var format = sameKey ? "png" : new[] { "jpeg", "png", "gif", "webp" }[index % 4];
            await cache.SetAsync(key, ImageProxyTestFactory.Raster(format), "text/html");
            requests.Add((AdmissionProxy(cache, client), request, $"image/{format}"));
        }
        var entered = 0;
        var firstEight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProxiedImageCacheService.BeforeAllocationForTesting = async _ =>
        {
            var entry = Interlocked.Increment(ref entered);
            if (entry == 8) firstEight.TrySetResult();
            if (entry <= 8) await release.Task;
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = requests.Select(item => item.Proxy.GetOrFetchAsync(item.Request, true, deadline.Token)).ToArray();
        ImageProxyResult[] results;
        try
        {
            await firstEight.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(8, entered);
            Assert.Equal(count - 8, ImageHelperSupervisor.ValidationStatistics.CurrentQueuedCount);
            Assert.Equal(0, ImageHelperSupervisor.ValidationStatistics.CurrentAvailablePermits);
        }
        finally
        {
            release.TrySetResult();
            results = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(15));
        }
        for (var index = 0; index < results.Length; index++)
        {
            Assert.Equal(ImageProxyResultStatus.FreshHit, results[index].Status);
            Assert.Equal(requests[index].Mime, results[index].ContentType);
        }
        Assert.Equal(count, entered);
        Assert.Equal(0, origin.Requests);
        Assert.Equal(8, ImageHelperSupervisor.ValidationStatistics.CurrentAvailablePermits);
    }

    /// <summary>Use real disk caching and scoped origin ownership without network access.</summary>
    private static ImageProxyService AdmissionProxy(ProxiedImageCacheService cache, HttpClient client)
    {
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(x => x.GetSettings()).Returns(new ApplicationSettings());
        return new ImageProxyService(client, cache, settings.Object,
            ImageProxyTestFactory.ScopeFactory(client, cache, settings.Object), NullLogger<ImageProxyService>.Instance);
    }

    /// <summary>Count every attempted origin request; return only an ordinary small fixture.</summary>
    private sealed class AdmissionOrigin(byte[] bytes) : HttpMessageHandler
    {
        internal int Requests;

        /// <summary>Remain synchronous so origin count is observed before an owned operation completes.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
