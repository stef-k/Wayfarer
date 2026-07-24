using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Focused stale tile refresh coverage for scoped background work and metadata preservation.
/// </summary>
public partial class TileCacheServiceTests
{
    [Fact]
    public async Task BackgroundRefresh_UsesFreshScopeWithoutRequestHttpContext()
    {
        using var dir = new TempDir();
        var (db, dbName) = CreateNamedDbContext();
        var handler = new RefreshTestTileHandler(etag: "\"scope-v1\"");
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var accessor = new HttpContextAccessor { HttpContext = BuildRequestContext() };
        var service = CreateService(db, dir.Path, handler, httpContextAccessor: accessor, dbName: dbName, hotCache: hotCache);

        await service.CacheTileAsync("http://tiles/9/1/2.png", "9", "1", "2");
        ExpireDbTile(db, hotCache, 9, 1, 2);

        var result = await service.RetrieveTileAsync("9", "1", "2", "http://tiles/9/1/2.png");
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync("9_1_2", TimeSpan.FromSeconds(2)));

        Assert.NotNull(result.TileData);
        Assert.True(handler.Referrers.Count >= 2);
        Assert.NotNull(handler.Referrers[0]);
        Assert.Null(handler.Referrers[^1]);
    }

    [Fact]
    public async Task BackgroundRefresh_DelayedRetryConsumesGlobalBudgetWithoutRequestContext()
    {
        using var dir = new TempDir();
        var (db, dbName) = CreateNamedDbContext();
        var handler = new RefreshTestTileHandler(etag: "\"retry-budget\"") { DrainBudgetAfterFirstConditionalFailure = true };
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var accessor = new HttpContextAccessor { HttpContext = BuildRequestContext() };
        var service = CreateService(db, dir.Path, handler, httpContextAccessor: accessor, dbName: dbName, hotCache: hotCache);
        TileCacheService.SetRefreshRetryDelayForTesting(_ => TimeSpan.Zero);

        await service.CacheTileAsync("http://tiles/9/2/3.png", "9", "2", "3");
        ExpireDbTile(db, hotCache, 9, 2, 3);

        var result = await service.RetrieveTileAsync("9", "2", "3", "http://tiles/9/2/3.png");
        await Task.Delay(200);
        TileCacheService.CancelRefreshForTesting("9_2_3");
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync("9_2_3", TimeSpan.FromSeconds(5)));

        Assert.NotNull(result.TileData);
        Assert.Equal(1, handler.ConditionalCallCount);
        Assert.Null(handler.Referrers.Last());
    }

    [Fact]
    public async Task BackgroundRefresh_ExhaustedAttemptsAllowLaterRequestToStartNewSeries()
    {
        using var dir = new TempDir();
        var (db, dbName) = CreateNamedDbContext();
        var handler = new RefreshTestTileHandler(etag: "\"retry-reset\"") { ConditionalFailuresRemaining = 3 };
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var service = CreateService(db, dir.Path, handler, dbName: dbName, hotCache: hotCache);
        TileCacheService.SetRefreshRetryDelayForTesting(_ => TimeSpan.Zero);

        await service.CacheTileAsync("http://tiles/9/4/5.png", "9", "4", "5");
        ExpireDbTile(db, hotCache, 9, 4, 5);

        await service.RetrieveTileAsync("9", "4", "5", "http://tiles/9/4/5.png");
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync("9_4_5", TimeSpan.FromSeconds(2)));
        Assert.Equal(3, handler.ConditionalCallCount);

        await service.RetrieveTileAsync("9", "4", "5", "http://tiles/9/4/5.png");
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync("9_4_5", TimeSpan.FromSeconds(2)));

        Assert.Equal(4, handler.ConditionalCallCount);
    }

    [Fact]
    public async Task RetrieveTileAsync_ExpiredHighZoomReturnsBeforeRefreshCompletes()
    {
        using var dir = new TempDir();
        var (db, dbName) = CreateNamedDbContext();
        var handler = new BlockingRefreshTileHandler("\"high-block\"");
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var service = CreateService(db, dir.Path, handler, dbName: dbName, hotCache: hotCache);

        await service.CacheTileAsync("http://tiles/9/14/15.png", "9", "14", "15");
        var tilePath = service.GetTileFilePathForTesting("9", "14", "15");
        var originalBytes = await File.ReadAllBytesAsync(tilePath);
        ExpireDbTile(db, hotCache, 9, 14, 15);

        var retrieveTask = service.RetrieveTileAsync("9", "14", "15", "http://tiles/9/14/15.png");

        Assert.Same(retrieveTask, await Task.WhenAny(retrieveTask, Task.Delay(TimeSpan.FromSeconds(1))));
        var result = await retrieveTask;
        Assert.Equal(originalBytes, result.TileData);
        Assert.True(await handler.WaitForConditionalRequestAsync(TimeSpan.FromSeconds(2)));

        handler.CompleteConditionalRequest();
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync("9_14_15", TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task RetrieveTileAsync_ExpiredLowZoomReturnsBeforeRefreshCompletes()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var handler = new BlockingRefreshTileHandler("\"low-block\"");
        var service = CreateService(db, dir.Path, handler);
        var tilePath = Path.Combine(dir.Path, "5_14_15.png");
        var originalBytes = new byte[] { 7, 9, 11 };
        await File.WriteAllBytesAsync(tilePath, originalBytes);
        WriteSidecar(tilePath, "\"low-block\"", DateTime.UtcNow.AddHours(-1));

        var retrieveTask = service.RetrieveTileAsync("5", "14", "15", "http://tiles/5/14/15.png");

        Assert.Same(retrieveTask, await Task.WhenAny(retrieveTask, Task.Delay(TimeSpan.FromSeconds(1))));
        var result = await retrieveTask;
        Assert.Equal(originalBytes, result.TileData);
        Assert.True(await handler.WaitForConditionalRequestAsync(TimeSpan.FromSeconds(2)));

        handler.CompleteConditionalRequest();
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync("5_14_15", TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task BackgroundRefresh_ReplacementFailurePreservesOldFileAndMetadata()
    {
        using var dir = new TempDir();
        var (db, dbName) = CreateNamedDbContext();
        var handler = new RefreshTestTileHandler(etag: "\"old\"", newEtagOnRevalidation: "\"new\"")
        {
            ForceRevalidation200 = true
        };
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var service = CreateService(db, dir.Path, handler, dbName: dbName, hotCache: hotCache);
        TileCacheService.SetRefreshRetryDelayForTesting(_ => TimeSpan.Zero);
        TileCacheService.SetTileFileReplacerForTesting((_, _) => throw new IOException("replacement failed"));

        await service.CacheTileAsync("http://tiles/9/6/7.png", "9", "6", "7");
        var tilePath = service.GetTileFilePathForTesting("9", "6", "7");
        var originalBytes = await File.ReadAllBytesAsync(tilePath);
        ExpireDbTile(db, hotCache, 9, 6, 7);

        await service.RetrieveTileAsync("9", "6", "7", "http://tiles/9/6/7.png");
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync("9_6_7", TimeSpan.FromSeconds(2)));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(tilePath));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
        var meta = db.TileCacheMetadata.Single();
        Assert.Equal("\"old\"", meta.ETag);
        Assert.Equal(originalBytes.Length, meta.Size);
    }

    [Fact]
    public async Task RetrieveTileAsync_StaleHighZoomHitTouchesLastAccessed()
    {
        using var dir = new TempDir();
        var (db, dbName) = CreateNamedDbContext();
        var handler = new RefreshTestTileHandler(etag: "\"touch\"");
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var service = CreateService(db, dir.Path, handler, dbName: dbName, hotCache: hotCache);

        await service.CacheTileAsync("http://tiles/9/8/9.png", "9", "8", "9");
        var oldAccessed = DateTime.UtcNow.AddMinutes(-20);
        ExpireDbTile(db, hotCache, 9, 8, 9, oldAccessed);

        var result = await service.RetrieveTileAsync("9", "8", "9", "http://tiles/9/8/9.png");
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync("9_8_9", TimeSpan.FromSeconds(2)));

        Assert.NotNull(result.TileData);
        var meta = db.TileCacheMetadata.Single();
        Assert.True(meta.LastAccessed > oldAccessed);
    }

    [Fact]
    public async Task RetrieveTileAsync_LowZoom304RefreshUpdatesSidecarWithoutReplacingFile()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var handler = new RefreshTestTileHandler(etag: "\"low-304\"");
        var service = CreateService(db, dir.Path, handler);
        var tilePath = Path.Combine(dir.Path, "5_10_11.png");
        var originalBytes = new byte[] { 1, 3, 5 };
        await File.WriteAllBytesAsync(tilePath, originalBytes);
        WriteSidecar(tilePath, "\"low-304\"", DateTime.UtcNow.AddHours(-1));

        var result = await service.RetrieveTileAsync("5", "10", "11", "http://tiles/5/10/11.png");
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync("5_10_11", TimeSpan.FromSeconds(2)));

        Assert.Equal(originalBytes, result.TileData);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(tilePath));
        Assert.True(ReadSidecar(tilePath).ExpiresAtUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task RetrieveTileAsync_LowZoom200RefreshReplacesFileAndSidecar()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var handler = new RefreshTestTileHandler(etag: "\"low-old\"", newEtagOnRevalidation: "\"low-new\"")
        {
            ForceRevalidation200 = true
        };
        var service = CreateService(db, dir.Path, handler);
        var tilePath = Path.Combine(dir.Path, "5_12_13.png");
        await File.WriteAllBytesAsync(tilePath, new byte[] { 2, 4, 6 });
        WriteSidecar(tilePath, "\"low-old\"", DateTime.UtcNow.AddHours(-1));

        await service.RetrieveTileAsync("5", "12", "13", "http://tiles/5/12/13.png");
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync("5_12_13", TimeSpan.FromSeconds(2)));

        Assert.Equal(RefreshTestTileHandler.NewPayload, await File.ReadAllBytesAsync(tilePath));
        var sidecar = ReadSidecar(tilePath);
        Assert.Equal("\"low-new\"", sidecar.ETag);
        Assert.True(sidecar.ExpiresAtUtc > DateTime.UtcNow);
    }

    private static DefaultHttpContext BuildRequestContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("wayfarer.test");
        return context;
    }

    private static void ExpireDbTile(ApplicationDbContext db, TileMetadataHotCache hotCache,
        int zoom, int x, int y, DateTime? lastAccessed = null)
    {
        var meta = db.TileCacheMetadata.Single(t => t.Zoom == zoom && t.X == x && t.Y == y);
        meta.ExpiresAtUtc = DateTime.UtcNow.AddHours(-1);
        if (lastAccessed.HasValue)
        {
            meta.LastAccessed = lastAccessed.Value;
        }

        db.SaveChanges();
        hotCache.Remove(zoom, x, y);
    }

    private static void WriteSidecar(string tilePath, string etag, DateTime expiresAtUtc)
    {
        var sidecar = new TileSidecarMetadata
        {
            ETag = etag,
            ExpiresAtUtc = expiresAtUtc
        };
        File.WriteAllText(tilePath + ".meta", JsonSerializer.Serialize(sidecar));
    }

    private static TileSidecarMetadata ReadSidecar(string tilePath)
    {
        var json = File.ReadAllText(tilePath + ".meta");
        return JsonSerializer.Deserialize<TileSidecarMetadata>(json)!;
    }

    private static Func<ApplicationDbContext> CreateScopedDbFactory(ApplicationDbContext db, string? dbName)
    {
        if (dbName == null)
        {
            return () => db;
        }

        return () => new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            new ServiceCollection().BuildServiceProvider());
    }

    private static SingleScopeFactory CreateTileCacheScopeFactory(IConfiguration config, HttpClient httpClient,
        IApplicationSettingsService appSettings, TileMetadataHotCache hotCache,
        Func<ApplicationDbContext> scopedDbFactory)
    {
        SingleScopeFactory? scopeFactory = null;
        scopeFactory = new SingleScopeFactory(() =>
        {
            var scopedDb = scopedDbFactory();
            var scopedService = new TileCacheService(
                NullLogger<TileCacheService>.Instance,
                config,
                httpClient,
                scopedDb,
                appSettings,
                scopeFactory!,
                new HttpContextAccessor(),
                hotCache);
            return new ServiceCollection()
                .AddSingleton(scopedDb)
                .AddSingleton<ApplicationDbContext>(scopedDb)
                .AddSingleton(scopedService)
                .BuildServiceProvider();
        });
        return scopeFactory;
    }

    /// <summary>
    /// Conditional tile handler with failure and header capture controls for stale refresh tests.
    /// </summary>
    private sealed class RefreshTestTileHandler : HttpMessageHandler
    {
        public static readonly byte[] NewPayload = { 50, 60, 70, 80 };

        private readonly string? _etag;
        private readonly string? _newEtagOnRevalidation;
        private readonly byte[] _payload = { 10, 20, 30, 40 };

        public int ConditionalCallCount { get; private set; }
        public int ConditionalFailuresRemaining { get; set; }
        public bool DrainBudgetAfterFirstConditionalFailure { get; set; }
        public bool ForceRevalidation200 { get; set; }
        public List<Uri?> Referrers { get; } = new();

        public RefreshTestTileHandler(string? etag, string? newEtagOnRevalidation = null)
        {
            _etag = etag;
            _newEtagOnRevalidation = newEtagOnRevalidation;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Referrers.Add(request.Headers.Referrer);
            var isConditional = request.Headers.IfNoneMatch.Any() || request.Headers.IfModifiedSince.HasValue;
            if (isConditional)
            {
                ConditionalCallCount++;
                if (ConditionalFailuresRemaining > 0)
                {
                    ConditionalFailuresRemaining--;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }

                if (DrainBudgetAfterFirstConditionalFailure && ConditionalCallCount == 1)
                {
                    TileCacheService.OutboundBudget.DrainForTesting();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }
            }

            if (isConditional && !ForceRevalidation200)
            {
                var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
                notModified.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
                if (!string.IsNullOrEmpty(_etag))
                {
                    notModified.Headers.ETag = EntityTagHeaderValue.Parse(_etag);
                }

                return Task.FromResult(notModified);
            }

            var payload = isConditional && ForceRevalidation200 ? NewPayload : _payload;
            var etag = isConditional && ForceRevalidation200 ? _newEtagOnRevalidation : _etag;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            if (!string.IsNullOrEmpty(etag))
            {
                response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
            }

            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Blocks only conditional refresh requests so tests can prove stale bytes return first.
    /// </summary>
    private sealed class BlockingRefreshTileHandler : HttpMessageHandler
    {
        private readonly string _etag;
        private readonly byte[] _payload = { 10, 20, 30, 40 };
        private readonly TaskCompletionSource _conditionalStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseConditional =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingRefreshTileHandler(string etag) => _etag = etag;

        public Task<bool> WaitForConditionalRequestAsync(TimeSpan timeout) =>
            Task.WhenAny(_conditionalStarted.Task, Task.Delay(timeout))
                .ContinueWith(t => ReferenceEquals(t.Result, _conditionalStarted.Task));

        public void CompleteConditionalRequest() => _releaseConditional.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var isConditional = request.Headers.IfNoneMatch.Any() || request.Headers.IfModifiedSince.HasValue;
            if (isConditional)
            {
                _conditionalStarted.TrySetResult();
                await _releaseConditional.Task.WaitAsync(cancellationToken);
                var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
                notModified.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
                notModified.Headers.ETag = EntityTagHeaderValue.Parse(_etag);
                return notModified;
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload)
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            response.Headers.ETag = EntityTagHeaderValue.Parse(_etag);
            return response;
        }
    }
}
