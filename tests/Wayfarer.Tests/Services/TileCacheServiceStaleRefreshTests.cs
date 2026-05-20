using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Models;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

public partial class TileCacheServiceTests
{
    [Fact]
    public async Task RetrieveTileAsync_ExpiredHighZoomTile_ReturnsLocalBytesBeforeUpstreamCompletes()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var handler = new BlockingRevalidationHandler(etag: "\"stale-high\"");
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var service = CreateService(db, dir.Path, handler, hotCache: hotCache);

        await service.CacheTileAsync("http://tiles/9/21/22.png", "9", "21", "22");
        var cachedBytes = await File.ReadAllBytesAsync(Path.Combine(dir.Path, "9_21_22.png"));
        var meta = db.TileCacheMetadata.Single();
        meta.ExpiresAtUtc = DateTime.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();
        hotCache.Remove(9, 21, 22);

        var retrieveTask = service.RetrieveTileAsync("9", "21", "22", "http://tiles/9/21/22.png");
        var completed = await Task.WhenAny(retrieveTask, Task.Delay(250));

        Assert.Same(retrieveTask, completed);
        var result = await retrieveTask;
        Assert.Equal(cachedBytes, result.TileData);
        Assert.True(await handler.WaitForRevalidationStartedAsync());
        Assert.False(handler.RevalidationCompleted);

        handler.ReleaseRevalidation();
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync("9_21_22", TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task RetrieveTileAsync_ExpiredLowZoomSidecarTile_ReturnsLocalBytesBeforeUpstreamCompletes()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var handler = new BlockingRevalidationHandler(etag: "\"stale-low\"");
        var service = CreateService(db, dir.Path, handler);

        await service.CacheTileAsync("http://tiles/5/21/22.png", "5", "21", "22");
        var tileFilePath = Path.Combine(dir.Path, "5_21_22.png");
        var cachedBytes = await File.ReadAllBytesAsync(tileFilePath);
        await File.WriteAllTextAsync(tileFilePath + ".meta", JsonSerializer.Serialize(new TileSidecarMetadata
        {
            ETag = "\"stale-low\"",
            LastModifiedUpstream = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            ExpiresAtUtc = DateTime.UtcNow.AddHours(-1)
        }));
        TileCacheService.ResetStaticStateForTesting();

        var retrieveTask = service.RetrieveTileAsync("5", "21", "22", "http://tiles/5/21/22.png");
        var completed = await Task.WhenAny(retrieveTask, Task.Delay(250));

        Assert.Same(retrieveTask, completed);
        var result = await retrieveTask;
        Assert.Equal(cachedBytes, result.TileData);
        Assert.True(await handler.WaitForRevalidationStartedAsync());
        Assert.False(handler.RevalidationCompleted);

        handler.ReleaseRevalidation();
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync("5_21_22", TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task RetrieveTileAsync_BudgetExhaustion_DoesNotBlockStaleCachedTile()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var service = CreateService(db, dir.Path, hotCache: hotCache);

        await service.CacheTileAsync("http://tiles/9/31/32.png", "9", "31", "32");
        var cachedBytes = await File.ReadAllBytesAsync(Path.Combine(dir.Path, "9_31_32.png"));
        var meta = db.TileCacheMetadata.Single();
        meta.ExpiresAtUtc = DateTime.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();
        hotCache.Remove(9, 31, 32);
        TileCacheService.OutboundBudget.DrainForTesting();

        var result = await service.RetrieveTileAsync("9", "31", "32", "http://tiles/9/31/32.png");
        TileCacheService.CancelRefreshForTesting("9_31_32");
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync("9_31_32", TimeSpan.FromSeconds(2)));

        Assert.False(result.BudgetExhausted);
        Assert.Equal(cachedBytes, result.TileData);
    }

    /// <summary>
    /// Returns an initial cache response, then blocks conditional revalidation until released.
    /// </summary>
    private sealed class BlockingRevalidationHandler : HttpMessageHandler
    {
        private readonly string _etag;
        private readonly TaskCompletionSource _revalidationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseRevalidation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public bool RevalidationCompleted { get; private set; }

        public BlockingRevalidationHandler(string etag) => _etag = etag;

        public async Task<bool> WaitForRevalidationStartedAsync()
        {
            var completed = await Task.WhenAny(_revalidationStarted.Task, Task.Delay(1000));
            return completed == _revalidationStarted.Task;
        }

        public void ReleaseRevalidation() => _releaseRevalidation.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[] { 10, 20, 30, 40 })
                };
                response.Headers.ETag = EntityTagHeaderValue.Parse(_etag);
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(3600) };
                return response;
            }

            _revalidationStarted.TrySetResult();
            await _releaseRevalidation.Task.WaitAsync(cancellationToken);
            RevalidationCompleted = true;

            var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
            notModified.Headers.ETag = EntityTagHeaderValue.Parse(_etag);
            notModified.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(3600) };
            return notModified;
        }
    }
}
