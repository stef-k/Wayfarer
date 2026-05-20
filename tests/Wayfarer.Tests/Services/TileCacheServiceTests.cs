using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Linq;
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
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tile cache behaviors: storing, retrieving, and purging cached tiles.
/// </summary>
/// <remarks>
/// Shares the "OutboundBudget" collection with <see cref="Controllers.TilesControllerTests"/>
/// to prevent parallel execution — both classes mutate <see cref="TileCacheService.OutboundBudget"/>
/// static state via DrainForTesting/ResetForTesting.
/// </remarks>
[Collection("OutboundBudget")]
public partial class TileCacheServiceTests : TestBase
{
    [Fact]
    public async Task CacheTileAsync_StoresFileAndMetadata_ForZoomNine()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var service = CreateService(db, dir.Path);

        await service.CacheTileAsync("http://tiles/9/1/2.png", "9", "1", "2");

        var filePath = Path.Combine(dir.Path, "9_1_2.png");
        Assert.True(File.Exists(filePath));
        var meta = Assert.Single(db.TileCacheMetadata);
        Assert.Equal(9, meta.Zoom);
        Assert.Equal(filePath, meta.TileFilePath);
    }

    [Fact]
    public async Task RetrieveTileAsync_UpdatesLastAccessed()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var service = CreateService(db, dir.Path, hotCache: hotCache);
        await service.CacheTileAsync("http://tiles/9/3/4.png", "9", "3", "4");
        var meta = db.TileCacheMetadata.Single();
        var old = DateTime.UtcNow.AddMinutes(-10);
        meta.LastAccessed = old;
        db.SaveChanges();
        hotCache.Remove(9, 3, 4);

        var result = await service.RetrieveTileAsync("9", "3", "4");
        var bytes = result.TileData;

        Assert.NotNull(bytes);
        Assert.True(db.TileCacheMetadata.Single().LastAccessed > old);
    }

    [Fact]
    public async Task PurgeAllCacheAsync_RemovesFilesAndMetadata()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var service = CreateService(db, dir.Path, hotCache: hotCache);
        await service.CacheTileAsync("http://tiles/9/5/6.png", "9", "5", "6");
        await service.CacheTileAsync("http://tiles/9/7/8.png", "9", "7", "8");
        Assert.True(Directory.GetFiles(dir.Path, "*.png").Length >= 2);
        Assert.Equal(2, db.TileCacheMetadata.Count());
        Assert.True(hotCache.TryGet(ApplicationSettings.DefaultTileMetadataHotCacheSizeMB, 9, 5, 6, out _));

        await service.PurgeAllCacheAsync();

        Assert.Empty(Directory.GetFiles(dir.Path));
        Assert.Empty(db.TileCacheMetadata);
        Assert.False(hotCache.TryGet(ApplicationSettings.DefaultTileMetadataHotCacheSizeMB, 9, 5, 6, out _));
    }

    [Fact]
    public async Task CacheTileAsync_EvictsLru_WhenCacheOverLimit()
    {
        using var dir = new TempDir();
        var (db, dbName) = CreateNamedDbContext();
        var handler = new SizedTileHandler(600_000); // ~0.57 MB tiles
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var service = CreateService(db, dir.Path, handler, maxCacheMb: 1, dbName: dbName, hotCache: hotCache);

        await service.CacheTileAsync("http://tiles/9/1/1.png", "9", "1", "1"); // fits
        await service.CacheTileAsync("http://tiles/9/1/2.png", "9", "1", "2"); // triggers eviction of oldest

        // Reload from DB to see scoped eviction changes (eviction uses its own DbContext).
        var remaining = db.TileCacheMetadata.ToList();
        Assert.Single(remaining);
        Assert.Equal(1, remaining[0].X);
        Assert.Equal(2, remaining[0].Y);
        Assert.False(hotCache.TryGet(ApplicationSettings.DefaultTileMetadataHotCacheSizeMB, 9, 1, 1, out _));
        Assert.True(hotCache.TryGet(ApplicationSettings.DefaultTileMetadataHotCacheSizeMB, 9, 1, 2, out _));
    }

    [Fact]
    public async Task RetrieveTileAsync_UpdatesLastAccessed_ForExistingTile()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var service = CreateService(db, dir.Path, hotCache: hotCache);
        await service.CacheTileAsync("http://tiles/9/3/4.png", "9", "3", "4");
        var meta = db.TileCacheMetadata.Single();
        meta.LastAccessed = DateTime.UtcNow.AddMinutes(-6);
        db.SaveChanges();
        hotCache.Remove(9, 3, 4);
        var old = meta.LastAccessed;

        await Task.Delay(5);
        await service.RetrieveTileAsync("9", "3", "4");

        Assert.True(db.TileCacheMetadata.Single().LastAccessed > old);
    }

    [Fact]
    public async Task RetrieveTileAsync_HotHit_ThrottlesLastAccessedWritesToFiveMinutes()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var service = CreateService(db, dir.Path, hotCache: hotCache);
        await service.CacheTileAsync("http://tiles/9/30/40.png", "9", "30", "40");

        var meta = db.TileCacheMetadata.Single();
        meta.LastAccessed = DateTime.UtcNow.AddMinutes(-10);
        await db.SaveChangesAsync();

        await service.RetrieveTileAsync("9", "30", "40", "http://tiles/9/30/40.png");
        db.Entry(meta).Reload();
        var touchedAt = meta.LastAccessed;

        await Task.Delay(5);
        await service.RetrieveTileAsync("9", "30", "40", "http://tiles/9/30/40.png");
        db.Entry(meta).Reload();

        Assert.Equal(touchedAt, meta.LastAccessed);
    }

    [Fact]
    public void TileMetadataHotCache_TryBeginLastAccessedPersist_IsAtomicPerTile()
    {
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);

        var results = Enumerable.Range(0, 10)
            .AsParallel()
            .Select(_ => hotCache.TryBeginLastAccessedPersist(9, 77, 88))
            .ToList();

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public void TileMetadataHotCache_AbortLastAccessedPersist_AllowsImmediateRetry()
    {
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);

        Assert.True(hotCache.TryBeginLastAccessedPersist(9, 90, 91));
        hotCache.AbortLastAccessedPersist(9, 90, 91);

        Assert.True(hotCache.TryBeginLastAccessedPersist(9, 90, 91));
    }

    [Fact]
    public async Task GetCacheFileSizeInMbAsync_ReturnsZeroWhenEmpty()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var service = CreateService(db, dir.Path);

        var size = await service.GetCacheFileSizeInMbAsync();

        Assert.Equal(0, size);
    }

    [Fact]
    public async Task CacheSizeAndCountHelpers_ReportDiskValues()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var service = CreateService(db, dir.Path);
        var fileA = Path.Combine(dir.Path, "a.bin");
        var fileB = Path.Combine(dir.Path, "b.bin");
        await File.WriteAllBytesAsync(fileA, new byte[512 * 1024]); // 0.5 MB
        await File.WriteAllBytesAsync(fileB, new byte[256 * 1024]); // 0.25 MB

        var size = await service.GetCacheFileSizeInMbAsync();
        var count = await service.GetTotalCachedFilesAsync();

        Assert.InRange(size, 0.74, 0.76);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task LruHelpers_UseDatabaseMetadata()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        db.TileCacheMetadata.AddRange(
            new TileCacheMetadata
            {
                Zoom = 9, X = 1, Y = 1,
                Size = 1024,
                TileLocation = new NetTopologySuite.Geometries.Point(0, 0) { SRID = 4326 },
                TileFilePath = Path.Combine(dir.Path, "1.png"),
                LastAccessed = DateTime.UtcNow
            },
            new TileCacheMetadata
            {
                Zoom = 9, X = 1, Y = 2,
                Size = 2048,
                TileLocation = new NetTopologySuite.Geometries.Point(0, 0) { SRID = 4326 },
                TileFilePath = Path.Combine(dir.Path, "2.png"),
                LastAccessed = DateTime.UtcNow
            });
        await db.SaveChangesAsync();
        var service = CreateService(db, dir.Path);

        var mb = await service.GetLruCachedInMbFilesAsync();
        var total = await service.GetLruTotalFilesInDbAsync();

        Assert.InRange(mb, 0.0029, 0.0031); // 3 KB -> ~0.0029 MB
        Assert.Equal(2, total);
    }

    [Fact]
    public async Task SendTileRequest_SetsRefererFromHttpContext()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var handler = new StubTileHandler();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("myapp.example.com");
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var service = CreateService(db, dir.Path, handler, httpContextAccessor: accessor);

        await service.CacheTileAsync("http://tiles/9/1/2.png", "9", "1", "2");

        Assert.True(handler.WasCalled);
        Assert.Equal(new System.Uri("https://myapp.example.com"), handler.LastReferrer);
    }

    [Fact]
    public async Task SendTileRequest_SetsHonestUserAgent()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var handler = new StubTileHandler();
        var service = CreateService(db, dir.Path, handler);

        await service.CacheTileAsync("http://tiles/9/1/2.png", "9", "1", "2");

        Assert.True(handler.WasCalled);
        Assert.NotNull(handler.LastUserAgent);
        Assert.Contains("Wayfarer/1.0", handler.LastUserAgent!);
        Assert.DoesNotContain("Chrome", handler.LastUserAgent);
        Assert.DoesNotContain("Mozilla", handler.LastUserAgent);
    }

    [Fact]
    public async Task SendTileRequest_NoReferer_WhenNoHttpContext()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var handler = new StubTileHandler();
        var accessor = new HttpContextAccessor(); // no HttpContext set
        var service = CreateService(db, dir.Path, handler, httpContextAccessor: accessor);

        await service.CacheTileAsync("http://tiles/9/1/2.png", "9", "1", "2");

        Assert.True(handler.WasCalled);
        Assert.Null(handler.LastReferrer);
    }

    [Fact]
    public async Task SendTileRequest_HandlesSpecialCharactersInContactEmail()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var handler = new StubTileHandler();
        // An unbalanced parenthesis is invalid in RFC 7230 comment tokens.
        // TryParseAdd should fail gracefully and fall back to "Wayfarer/1.0".
        var service = CreateService(db, dir.Path, handler, contactEmail: "user)bad@example.com");

        await service.CacheTileAsync("http://tiles/9/1/2.png", "9", "1", "2");

        Assert.True(handler.WasCalled);
        Assert.NotNull(handler.LastUserAgent);
        Assert.Contains("Wayfarer/1.0", handler.LastUserAgent!);
        // Should not contain the malformed email — fallback was used
        Assert.DoesNotContain("bad@example", handler.LastUserAgent!);
    }

    // ── Conditional request and cache expiry tests ──────────────────────

    [Fact]
    public async Task CacheTileAsync_StoresETagAndExpiry_ForZoomNineOrAbove()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var handler = new ConditionalTileHandler(etag: "\"abc123\"", maxAgeSeconds: 3600);
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var service = CreateService(db, dir.Path, handler, hotCache: hotCache);

        await service.CacheTileAsync("http://tiles/9/1/2.png", "9", "1", "2");

        var meta = Assert.Single(db.TileCacheMetadata);
        Assert.Equal("\"abc123\"", meta.ETag);
        Assert.NotNull(meta.ExpiresAtUtc);
        Assert.True(meta.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(50)); // ~1 hour expiry
        Assert.True(hotCache.TryGet(ApplicationSettings.DefaultTileMetadataHotCacheSizeMB, 9, 1, 2, out var entry));
        Assert.Equal("\"abc123\"", entry!.ETag);
    }

    [Fact]
    public async Task CacheTileAsync_WritesSidecarMetadata_ForLowZoom()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var handler = new ConditionalTileHandler(etag: "\"low-zoom\"", maxAgeSeconds: 7200);
        var service = CreateService(db, dir.Path, handler);

        await service.CacheTileAsync("http://tiles/5/10/20.png", "5", "10", "20");

        var tileFile = Path.Combine(dir.Path, "5_10_20.png");
        var sidecarFile = tileFile + ".meta";
        Assert.True(File.Exists(tileFile));
        Assert.True(File.Exists(sidecarFile));

        var json = await File.ReadAllTextAsync(sidecarFile);
        var sidecar = JsonSerializer.Deserialize<TileSidecarMetadata>(json);
        Assert.NotNull(sidecar);
        Assert.Equal("\"low-zoom\"", sidecar!.ETag);
        Assert.NotNull(sidecar.ExpiresAtUtc);
        // No DB metadata should exist for zoom < 9
        Assert.Empty(db.TileCacheMetadata);
    }

    [Fact]
    public async Task RetrieveTileAsync_ServesFromCache_WhenNotExpired()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var handler = new ConditionalTileHandler(etag: "\"fresh\"", maxAgeSeconds: 3600);
        var service = CreateService(db, dir.Path, handler);

        // Cache the tile first (this makes 1 HTTP call)
        await service.CacheTileAsync("http://tiles/9/1/2.png", "9", "1", "2");
        var callCount = handler.CallCount;

        // Retrieve should serve from cache without HTTP call
        var result = await service.RetrieveTileAsync("9", "1", "2", "http://tiles/9/1/2.png");
        var bytes = result.TileData;

        Assert.NotNull(bytes);
        Assert.Equal(callCount, handler.CallCount); // No additional HTTP calls
    }

    [Fact]
    public async Task RetrieveTileAsync_FirstWarmHit_PopulatesHotMetadataCache()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var service = CreateService(db, dir.Path, hotCache: hotCache);

        await service.CacheTileAsync("http://tiles/9/11/12.png", "9", "11", "12");

        hotCache.Remove(9, 11, 12);
        var result = await service.RetrieveTileAsync("9", "11", "12", "http://tiles/9/11/12.png");

        Assert.NotNull(result.TileData);
        Assert.True(hotCache.TryGet(ApplicationSettings.DefaultTileMetadataHotCacheSizeMB, 9, 11, 12, out var entry));
        Assert.NotNull(entry);
        Assert.NotNull(entry!.ExpiresAtUtc);
    }

    [Fact]
    public async Task RetrieveTileAsync_SecondWarmHit_ServesWithoutDbMetadataRead()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var service = CreateService(db, dir.Path, hotCache: hotCache);

        await service.CacheTileAsync("http://tiles/9/13/14.png", "9", "13", "14");
        await service.RetrieveTileAsync("9", "13", "14", "http://tiles/9/13/14.png");

        db.TileCacheMetadata.RemoveRange(db.TileCacheMetadata.Where(t => t.Zoom == 9 && t.X == 13 && t.Y == 14));
        await db.SaveChangesAsync();

        var result = await service.RetrieveTileAsync("9", "13", "14", "http://tiles/9/13/14.png");

        Assert.NotNull(result.TileData);
    }

    [Fact]
    public async Task RetrieveTileAsync_FreshHotCacheHitWithMissingFile_RemovesEntryAndFallsBack()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var handler = new ConditionalTileHandler(etag: "\"fresh-missing\"", maxAgeSeconds: 3600);
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var service = CreateService(db, dir.Path, handler, hotCache: hotCache);

        await service.CacheTileAsync("http://tiles/9/15/16.png", "9", "15", "16");
        await service.RetrieveTileAsync("9", "15", "16", "http://tiles/9/15/16.png");
        File.Delete(Path.Combine(dir.Path, "9_15_16.png"));

        var result = await service.RetrieveTileAsync("9", "15", "16", "http://tiles/9/15/16.png");

        Assert.NotNull(result.TileData);
        Assert.True(File.Exists(Path.Combine(dir.Path, "9_15_16.png")));
        Assert.True(hotCache.TryGet(ApplicationSettings.DefaultTileMetadataHotCacheSizeMB, 9, 15, 16, out _));
    }

    [Fact]
    public async Task RetrieveTileAsync_SendsConditionalRequest_WhenExpired()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var handler = new ConditionalTileHandler(etag: "\"expired\"", maxAgeSeconds: 3600);
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var service = CreateService(db, dir.Path, handler, hotCache: hotCache);

        // Cache the tile (1 HTTP call)
        await service.CacheTileAsync("http://tiles/9/1/2.png", "9", "1", "2");
        var callCountAfterCache = handler.CallCount;

        // Manually expire the tile by setting ExpiresAtUtc to the past
        var meta = db.TileCacheMetadata.Single();
        meta.ExpiresAtUtc = DateTime.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();
        hotCache.Remove(9, 1, 2);

        // Retrieve should send conditional request because tile is expired
        var result = await service.RetrieveTileAsync("9", "1", "2", "http://tiles/9/1/2.png");
        var bytes = result.TileData;
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync("9_1_2", TimeSpan.FromSeconds(2)));

        Assert.NotNull(bytes);
        Assert.True(handler.CallCount > callCountAfterCache, "Expected conditional HTTP request");
    }

    [Fact]
    public async Task RetrieveTileAsync_HandlesNotModified304_WithoutRedownload()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var handler = new ConditionalTileHandler(etag: "\"v1\"", maxAgeSeconds: 3600);
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var service = CreateService(db, dir.Path, handler, hotCache: hotCache);

        // Cache the tile
        await service.CacheTileAsync("http://tiles/9/1/2.png", "9", "1", "2");
        var originalFile = await File.ReadAllBytesAsync(Path.Combine(dir.Path, "9_1_2.png"));

        // Manually expire the tile
        var meta = db.TileCacheMetadata.Single();
        meta.ExpiresAtUtc = DateTime.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();
        hotCache.Remove(9, 1, 2);

        // Retrieve: tile is expired, handler returns 304 when If-None-Match matches
        var result = await service.RetrieveTileAsync("9", "1", "2", "http://tiles/9/1/2.png");
        var bytes = result.TileData;
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync("9_1_2", TimeSpan.FromSeconds(2)));

        Assert.NotNull(bytes);
        Assert.Equal(originalFile, bytes); // Same data, not re-downloaded

        // Verify expiry was updated in DB (refreshed from 304 response)
        db.Entry(meta).Reload();
        Assert.NotNull(meta.ExpiresAtUtc);
        Assert.True(meta.ExpiresAtUtc > DateTime.UtcNow);
        Assert.True(hotCache.TryGet(ApplicationSettings.DefaultTileMetadataHotCacheSizeMB, 9, 1, 2, out var entry));
        Assert.Equal(meta.ExpiresAtUtc, entry!.ExpiresAtUtc);
    }

    [Fact]
    public async Task RetrieveTileAsync_ReplacesFile_On200AfterExpiry()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        // First call returns etag "\"v1\"", revalidation returns new data with etag "\"v2\""
        var handler = new ConditionalTileHandler(etag: "\"v1\"", maxAgeSeconds: 3600, newEtagOnRevalidation: "\"v2\"");
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var service = CreateService(db, dir.Path, handler, hotCache: hotCache);

        // Cache the tile
        await service.CacheTileAsync("http://tiles/9/1/2.png", "9", "1", "2");

        // Manually expire the tile
        var meta = db.TileCacheMetadata.Single();
        meta.ExpiresAtUtc = DateTime.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();
        hotCache.Remove(9, 1, 2);

        // Force the handler to return 200 on revalidation (different etag = new content)
        handler.ForceRevalidation200 = true;

        var result = await service.RetrieveTileAsync("9", "1", "2", "http://tiles/9/1/2.png");
        var bytes = result.TileData;
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync("9_1_2", TimeSpan.FromSeconds(2)));

        Assert.NotNull(bytes);
        Assert.Equal(new byte[] { 50, 60, 70, 80 }, await File.ReadAllBytesAsync(Path.Combine(dir.Path, "9_1_2.png")));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
        // DB metadata should now have the new etag
        db.Entry(meta).Reload();
        Assert.Equal("\"v2\"", meta.ETag);
        Assert.True(hotCache.TryGet(ApplicationSettings.DefaultTileMetadataHotCacheSizeMB, 9, 1, 2, out var entry));
        Assert.Equal("\"v2\"", entry!.ETag);
    }

    [Fact]
    public async Task RetrieveTileAsync_ServesStaleCache_WhenRevalidationFails()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var handler = new ConditionalTileHandler(etag: "\"stale\"", maxAgeSeconds: 3600);
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var service = CreateService(db, dir.Path, handler, hotCache: hotCache);

        // Cache the tile
        await service.CacheTileAsync("http://tiles/9/1/2.png", "9", "1", "2");

        // Manually expire the tile
        var meta = db.TileCacheMetadata.Single();
        meta.ExpiresAtUtc = DateTime.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();
        hotCache.Remove(9, 1, 2);

        // Make handler fail on next call
        handler.FailNextRequest = true;

        // Retrieve should serve stale cached file despite re-validation failure
        var result = await service.RetrieveTileAsync("9", "1", "2", "http://tiles/9/1/2.png");
        var bytes = result.TileData;
        TileCacheService.CancelRefreshForTesting("9_1_2");
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync("9_1_2", TimeSpan.FromSeconds(2)));

        Assert.NotNull(bytes);
    }

    [Fact]
    public void ParseCacheExpiry_ParsesMaxAge()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(2) };

        var expiry = TileCacheService.ParseCacheExpiry(response);

        // Should be ~2 hours from now
        Assert.InRange(expiry, DateTime.UtcNow.AddHours(1.9), DateTime.UtcNow.AddHours(2.1));
    }

    [Fact]
    public void ParseCacheExpiry_DefaultsTo7Days_WhenNoHeaders()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        // No Cache-Control or Expires headers

        var expiry = TileCacheService.ParseCacheExpiry(response);

        // Should be ~7 days from now
        Assert.InRange(expiry, DateTime.UtcNow.AddDays(6.9), DateTime.UtcNow.AddDays(7.1));
    }

    [Fact]
    public async Task PurgeAllCacheAsync_DeletesSidecarFiles()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var handler = new ConditionalTileHandler(etag: "\"purge-test\"", maxAgeSeconds: 3600);
        var service = CreateService(db, dir.Path, handler);

        // Cache a low-zoom tile (creates sidecar)
        await service.CacheTileAsync("http://tiles/5/1/1.png", "5", "1", "1");
        var sidecarPath = Path.Combine(dir.Path, "5_1_1.png.meta");
        Assert.True(File.Exists(sidecarPath));

        await service.PurgeAllCacheAsync();

        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task RetrieveTileAsync_CoalescesConcurrentRevalidations()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var handler = new ConditionalTileHandler(etag: "\"coalesce\"", maxAgeSeconds: 3600);
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var service = CreateService(db, dir.Path, handler, hotCache: hotCache);

        // Cache the tile first
        await service.CacheTileAsync("http://tiles/9/5/5.png", "9", "5", "5");
        var callCountAfterCache = handler.CallCount;

        // Manually expire the tile
        var meta = db.TileCacheMetadata.Single();
        meta.ExpiresAtUtc = DateTime.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();
        hotCache.Remove(9, 5, 5);

        // Fire 5 concurrent retrieve requests for the same expired tile
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => service.RetrieveTileAsync("9", "5", "5", "http://tiles/9/5/5.png"))
            .ToList();

        var results = await Task.WhenAll(tasks);
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync("9_5_5", TimeSpan.FromSeconds(2)));

        // All should return data
        Assert.All(results, r => Assert.NotNull(r.TileData));

        // Only 1 additional HTTP request should have been made (coalesced)
        var additionalCalls = handler.CallCount - callCountAfterCache;
        Assert.Equal(1, additionalCalls);
    }

    [Fact]
    public async Task CacheTileAsync_StoresSidecarWithLastModified_ForLowZoom()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var lastMod = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var handler = new ConditionalTileHandler(etag: null, maxAgeSeconds: 3600, lastModified: lastMod);
        var service = CreateService(db, dir.Path, handler);

        await service.CacheTileAsync("http://tiles/3/1/1.png", "3", "1", "1");

        var sidecarFile = Path.Combine(dir.Path, "3_1_1.png.meta");
        Assert.True(File.Exists(sidecarFile));
        var json = await File.ReadAllTextAsync(sidecarFile);
        var sidecar = JsonSerializer.Deserialize<TileSidecarMetadata>(json);
        Assert.NotNull(sidecar);
        Assert.NotNull(sidecar!.LastModifiedUpstream);
    }

    [Fact]
    public async Task CacheTileAsync_ReturnsFalse_WhenBudgetExhausted()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var service = CreateService(db, dir.Path);

        // Drain all tokens and stop replenishment — next AcquireAsync returns false.
        TileCacheService.OutboundBudget.DrainForTesting();

        // CacheTileAsync should return false (budget exhausted) and not write any file or metadata.
        var result = await service.CacheTileAsync("http://tiles/10/1/1.png", "10", "1", "1");

        Assert.False(result);
        Assert.False(File.Exists(Path.Combine(dir.Path, "10_1_1.png")));
        Assert.Empty(db.TileCacheMetadata.ToList());
    }

    [Fact]
    public async Task RetrieveTileAsync_ReturnsThrottled_WhenBudgetExhausted()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var service = CreateService(db, dir.Path);

        TileCacheService.OutboundBudget.DrainForTesting();

        var result = await service.RetrieveTileAsync("10", "1", "1", "http://tiles/10/1/1.png");

        Assert.True(result.BudgetExhausted);
        Assert.Null(result.TileData);
    }

    /// <summary>
    /// Creates an <see cref="ApplicationDbContext"/> with a known database name, so that
    /// <see cref="SingleScopeFactory"/> can create independent DbContext instances that
    /// share the same in-memory database store.
    /// </summary>
    private (ApplicationDbContext Db, string DbName) CreateNamedDbContext()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new ApplicationDbContext(options, new ServiceCollection().BuildServiceProvider());
        return (db, dbName);
    }

    private TileCacheService CreateService(ApplicationDbContext db, string cacheDir, HttpMessageHandler? handler = null,
        int maxCacheMb = 10, IHttpContextAccessor? httpContextAccessor = null, string contactEmail = "test@example.com",
        string? dbName = null, TileMetadataHotCache? hotCache = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CacheSettings:TileCacheDirectory"] = cacheDir,
                ["Application:ContactEmail"] = contactEmail
            }).Build();
        var httpClient = new HttpClient(handler ?? new StubTileHandler());

        // Mirror the HttpClient configuration from Program.cs AddHttpClient registration.
        httpClient.Timeout = TimeSpan.FromSeconds(10);
        if (!httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd(
                $"Wayfarer/1.0 (contact: {contactEmail})"))
        {
            // "Wayfarer/1.0" is always a valid product token; TryParseAdd cannot fail here.
            httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("Wayfarer/1.0");
        }

        // Reset static state so tests don't interfere with each other.
        TileCacheService.ResetStaticStateForTesting();

        // Create a scope factory that returns independent DbContext instances sharing
        // the same in-memory database by name. This mirrors production behavior where
        // each scope gets its own DbContext with a separate change tracker.
        var appSettings = new StubSettingsService(maxCacheMb);
        var effectiveHotCache = hotCache ?? new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var scopedDbFactory = CreateScopedDbFactory(db, dbName);
        var scopeFactory = CreateTileCacheScopeFactory(config, httpClient, appSettings, effectiveHotCache, scopedDbFactory);
        return new TileCacheService(
            NullLogger<TileCacheService>.Instance,
            config,
            httpClient,
            db,
            appSettings,
            scopeFactory,
            httpContextAccessor ?? new HttpContextAccessor(),
            effectiveHotCache);
    }

    /// <summary>
    /// Simple stub that returns 200 OK with a small payload.
    /// Captures header values for assertions.
    /// </summary>
    private sealed class StubTileHandler : HttpMessageHandler
    {
        /// <summary>
        /// Captures header values from the last request for assertions.
        /// Values are captured inside SendAsync before the caller disposes the request.
        /// </summary>
        public Uri? LastReferrer { get; private set; }
        public string? LastUserAgent { get; private set; }
        public bool WasCalled { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastReferrer = request.Headers.Referrer;
            LastUserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
            });
        }
    }

    /// <summary>
    /// Handler that returns configurable ETag, Cache-Control, and Last-Modified headers.
    /// Supports conditional requests: returns 304 when If-None-Match matches the stored ETag.
    /// </summary>
    private sealed class ConditionalTileHandler : HttpMessageHandler
    {
        private readonly string? _etag;
        private readonly int _maxAgeSeconds;
        private readonly DateTime? _lastModified;
        private readonly string? _newEtagOnRevalidation;
        private readonly byte[] _payload = { 10, 20, 30, 40 };
        private readonly byte[] _newPayload = { 50, 60, 70, 80 };

        /// <summary>
        /// Thread-safe call counter for assertions.
        /// </summary>
        public int CallCount => _callCount;
        private volatile int _callCount;

        /// <summary>
        /// When true, the next request will return 500 Internal Server Error.
        /// </summary>
        public bool FailNextRequest { get; set; }

        /// <summary>
        /// When true, conditional requests return 200 with new data instead of 304.
        /// Simulates a tile that has been updated upstream.
        /// </summary>
        public bool ForceRevalidation200 { get; set; }

        public ConditionalTileHandler(string? etag, int maxAgeSeconds, DateTime? lastModified = null,
            string? newEtagOnRevalidation = null)
        {
            _etag = etag;
            _maxAgeSeconds = maxAgeSeconds;
            _lastModified = lastModified;
            _newEtagOnRevalidation = newEtagOnRevalidation;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);

            if (FailNextRequest)
            {
                FailNextRequest = false;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            // Check for conditional request (If-None-Match)
            var ifNoneMatch = request.Headers.IfNoneMatch.FirstOrDefault()?.Tag;
            if (!ForceRevalidation200 && !string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == _etag)
            {
                // 304 Not Modified — tile hasn't changed
                var notModifiedResponse = new HttpResponseMessage(HttpStatusCode.NotModified);
                if (_maxAgeSeconds >= 0)
                {
                    notModifiedResponse.Headers.CacheControl = new CacheControlHeaderValue
                    {
                        MaxAge = TimeSpan.FromSeconds(3600) // Refresh expiry on 304
                    };
                }

                if (!string.IsNullOrEmpty(_etag))
                {
                    notModifiedResponse.Headers.ETag = EntityTagHeaderValue.Parse(_etag);
                }

                return Task.FromResult(notModifiedResponse);
            }

            // 200 OK with full tile content
            var responseEtag = ForceRevalidation200
                ? (_newEtagOnRevalidation ?? _etag)
                : _etag;
            var responsePayload = ForceRevalidation200 ? _newPayload : _payload;

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responsePayload)
            };

            if (!string.IsNullOrEmpty(responseEtag))
            {
                response.Headers.ETag = EntityTagHeaderValue.Parse(responseEtag);
            }

            if (_maxAgeSeconds >= 0)
            {
                response.Headers.CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromSeconds(_maxAgeSeconds)
                };
            }

            if (_lastModified.HasValue)
            {
                response.Content.Headers.LastModified = new DateTimeOffset(_lastModified.Value, TimeSpan.Zero);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class SizedTileHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;
        public SizedTileHandler(int sizeBytes) => _payload = Enumerable.Repeat((byte)5, sizeBytes).ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload)
            });
        }
    }

    /// <summary>
    /// Verifies that the outbound budget throttle can be acquired up to burst capacity.
    /// </summary>
    [Fact]
    public async Task OutboundBudget_AcquireAsync_GrantsTokensUpToBurstCapacity()
    {
        TileCacheService.OutboundBudget.ResetForTesting();

        // Burst capacity is 12 — first 12 should succeed immediately.
        for (int i = 0; i < TileCacheService.OutboundBudget.BurstCapacity; i++)
        {
            var acquired = await TileCacheService.OutboundBudget.AcquireAsync();
            Assert.True(acquired, $"Token {i + 1} should have been acquired");
        }
    }

    /// <summary>
    /// Verifies that once all burst tokens are consumed and the replenisher hasn't ticked yet,
    /// further acquire attempts time out (return false) rather than blocking forever.
    /// </summary>
    [Fact]
    public async Task OutboundBudget_AcquireAsync_ReturnsFalse_WhenBudgetExhausted()
    {
        TileCacheService.OutboundBudget.ResetForTesting();

        // Consume all burst tokens.
        for (int i = 0; i < TileCacheService.OutboundBudget.BurstCapacity; i++)
        {
            await TileCacheService.OutboundBudget.AcquireAsync();
        }

        // Use a short timeout to verify exhaustion without waiting the full AcquireTimeout.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        bool acquired;
        try
        {
            acquired = await TileCacheService.OutboundBudget.AcquireAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            acquired = false;
        }

        Assert.False(acquired, "Should not acquire a token when budget is exhausted");
    }

    private sealed class StubSettingsService : IApplicationSettingsService
    {
        private readonly int _maxCache;
        public StubSettingsService(int maxCacheMb = 10) => _maxCache = maxCacheMb;

        public ApplicationSettings GetSettings() => new ApplicationSettings
        {
            Id = 1,
            MaxCacheTileSizeInMB = _maxCache,
            UploadSizeLimitMB = 5,
            IsRegistrationOpen = true
        };

        public string GetUploadsDirectoryPath() => Path.Combine(Path.GetTempPath(), "uploads");
        public void RefreshSettings() { }
    }

    /// <summary>
    /// Creates independent <see cref="ApplicationDbContext"/> instances per scope, sharing the
    /// same in-memory database by name. This mirrors production behavior where each scope gets
    /// its own DbContext with a separate change tracker, preventing tests from masking
    /// change-tracker conflicts between the request context and scoped contexts.
    /// </summary>
    private sealed class SingleScopeFactory : IServiceScopeFactory
    {
        private readonly Func<IServiceProvider> _providerFactory;

        public SingleScopeFactory(Func<IServiceProvider> providerFactory) => _providerFactory = providerFactory;

        public IServiceScope CreateScope()
        {
            return new SimpleScope(_providerFactory());
        }

        private sealed class SimpleScope : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; }
            public SimpleScope(IServiceProvider provider) => ServiceProvider = provider;
            public void Dispose() { }
        }
    }

    // ── Purge concurrency guard and SSE progress tests ───────────────────

    [Fact]
    public async Task PurgeAllCacheAsync_RejectsSecondConcurrentPurge()
    {
        using var dir = new TempDir();
        var (db, dbName) = CreateNamedDbContext();
        // Use a slow handler to keep the first purge running long enough for the race.
        var handler = new SlowTileHandler(delayMs: 50);
        var service1 = CreateService(db, dir.Path, handler, dbName: dbName);

        // Seed some tiles so the purge has work to do.
        await service1.CacheTileAsync("http://tiles/9/1/1.png", "9", "1", "1");
        await service1.CacheTileAsync("http://tiles/9/1/2.png", "9", "1", "2");

        // Create a second service instance sharing the same static state.
        var db2 = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            new ServiceCollection().BuildServiceProvider());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CacheSettings:TileCacheDirectory"] = dir.Path,
                ["Application:ContactEmail"] = "test@example.com"
            }).Build();
        var httpClient = new HttpClient(new StubTileHandler());
        var appSettings = new StubSettingsService();
        var hotCache = new TileMetadataHotCache(NullLogger<TileMetadataHotCache>.Instance);
        var scopeFactory = CreateTileCacheScopeFactory(
            config,
            httpClient,
            appSettings,
            hotCache,
            CreateScopedDbFactory(db2, dbName));
        var service2 = new TileCacheService(
            NullLogger<TileCacheService>.Instance,
            config,
            httpClient,
            db2,
            appSettings,
            scopeFactory,
            new HttpContextAccessor(),
            hotCache);

        // Start the first purge.
        var firstPurge = service1.PurgeAllCacheAsync();

        // Second purge should be rejected immediately.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service2.PurgeAllCacheAsync());

        Assert.Contains("already in progress", ex.Message);

        // Wait for the first purge to complete.
        await firstPurge;

        // After completion, the guard should be released — a new purge should succeed.
        // Re-seed some data.
        await service1.CacheTileAsync("http://tiles/9/2/1.png", "9", "2", "1");
        await service1.PurgeAllCacheAsync();
    }

    [Fact]
    public async Task PurgeLRUCacheAsync_RejectsSecondConcurrentPurge()
    {
        using var dir = new TempDir();
        var (db, dbName) = CreateNamedDbContext();
        var service1 = CreateService(db, dir.Path, dbName: dbName);

        // Seed a tile so the purge has work to do.
        await service1.CacheTileAsync("http://tiles/9/3/1.png", "9", "3", "1");

        // Use a DelayingSseService to keep the first purge in flight — the "started"
        // broadcast happens after the CompareExchange guard, so the delay keeps the
        // guard held while the second call fires.
        var delaySse = new DelayingSseService(delayMs: 200);

        // Start first purge with the delaying SSE (do not await — keep it in flight).
        var firstPurge = service1.PurgeLRUCacheAsync(delaySse, "test-channel");

        // Give the first purge a moment to hit the CompareExchange guard.
        await Task.Delay(10);

        // Second purge should be rejected immediately.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service1.PurgeLRUCacheAsync());
        Assert.Contains("already in progress", ex.Message);

        await firstPurge;
    }

    [Fact]
    public async Task PurgeAllCacheAsync_BroadcastsProgressViaSse()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var service = CreateService(db, dir.Path);

        // Seed tiles.
        await service.CacheTileAsync("http://tiles/9/5/5.png", "9", "5", "5");
        await service.CacheTileAsync("http://tiles/9/5/6.png", "9", "5", "6");

        var spy = new SpySseService();
        await service.PurgeAllCacheAsync(spy, "test-channel");

        // Should have at least one progress broadcast.
        Assert.NotEmpty(spy.Messages);
        Assert.All(spy.Messages, m => Assert.Equal("test-channel", m.Channel));

        // Verify the last progress event has the expected structure.
        var lastPayload = spy.Messages.Last().Data;
        Assert.Contains("percentComplete", lastPayload);
    }

    [Fact]
    public async Task PurgeLRUCacheAsync_BroadcastsProgressViaSse()
    {
        using var dir = new TempDir();
        var (db, dbName) = CreateNamedDbContext();
        var service = CreateService(db, dir.Path, dbName: dbName);

        // Seed a tile.
        await service.CacheTileAsync("http://tiles/9/6/6.png", "9", "6", "6");

        var spy = new SpySseService();
        await service.PurgeLRUCacheAsync(spy, "test-channel");

        Assert.NotEmpty(spy.Messages);
        Assert.All(spy.Messages, m => Assert.Equal("test-channel", m.Channel));
    }

    [Fact]
    public async Task PurgeAllCacheAsync_GuardIsReleasedAfterFailure()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var service = CreateService(db, dir.Path);

        // Seed a tile then remove the directory to force an error during purge.
        await service.CacheTileAsync("http://tiles/9/7/7.png", "9", "7", "7");

        // First purge succeeds (or at least completes and releases the guard).
        await service.PurgeAllCacheAsync();

        // Should be able to purge again (guard released).
        Assert.False(TileCacheService.IsPurgeInProgress);
    }

    /// <summary>
    /// Spy implementation of <see cref="SseService"/> that captures broadcast calls.
    /// </summary>
    private sealed class SpySseService : SseService
    {
        public List<(string Channel, string Data)> Messages { get; } = new();

        public override Task BroadcastAsync(string channel, string data)
        {
            Messages.Add((channel, data));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// SSE implementation that introduces a delay on each broadcast, keeping the purge
    /// in-flight long enough for concurrent guard tests. The "started" broadcast happens
    /// after the CompareExchange guard, so the delay holds the guard while the second
    /// purge call fires.
    /// </summary>
    private sealed class DelayingSseService : SseService
    {
        private readonly int _delayMs;
        public DelayingSseService(int delayMs = 200) => _delayMs = delayMs;

        public override async Task BroadcastAsync(string channel, string data)
        {
            await Task.Delay(_delayMs);
        }
    }

    /// <summary>
    /// Handler that introduces a small delay to simulate slow tile fetching,
    /// useful for testing concurrent purge guard timing.
    /// </summary>
    private sealed class SlowTileHandler : HttpMessageHandler
    {
        private readonly int _delayMs;
        public SlowTileHandler(int delayMs = 100) => _delayMs = delayMs;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(_delayMs, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
            };
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tiles-{Guid.NewGuid():N}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
