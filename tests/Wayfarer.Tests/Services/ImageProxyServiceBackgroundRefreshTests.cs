using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wayfarer.Services;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Focused coverage for stale-hit image proxy background refresh coordination.
/// </summary>
public partial class ImageProxyServiceTests
{
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
