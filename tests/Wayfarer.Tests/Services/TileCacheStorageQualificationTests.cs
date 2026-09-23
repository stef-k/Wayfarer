using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Disposable old-native-cache qualification using the production service and fake HTTP seam.</summary>
[Collection("OutboundBudget")]
public sealed class TileCacheStorageQualificationTests
{
    private static readonly string Provider = TileProviderCatalog.CreateCacheIdentity(
        ApplicationSettings.DefaultTileProviderKey, ApplicationSettings.DefaultTileProviderUrlTemplate).Fingerprint;

    /// <summary>Current, scoped legacy and adopted flat metadata hit without downloads or path rewrites.</summary>
    [Theory]
    [InlineData("current")]
    [InlineData("scoped")]
    [InlineData("flat")]
    public async Task FreshHits_PreserveReferencesAndValidators(string kind)
    {
        await using var harness = new TileCacheTestHarness(distinctRoots: true);
        using var scope = harness.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await SeedAsync(harness, db, kind);
        var reference = row.TileFilePath;
        var service = scope.ServiceProvider.GetRequiredService<TileCacheService>();
        for (var hit = 0; hit < 2; hit++)
            Assert.Equal(new byte[] { 7, 8 }, (await service.RetrieveTileAsync("9", "1", "2", "https://tiles.test/9/1/2.png")).TileData);
        Assert.Empty(harness.Upstream.Requests);
        Assert.Equal(reference, row.TileFilePath);
        Assert.Equal(Provider, row.ProviderIdentity);
        Assert.Equal("\"old\"", row.ETag);
        Assert.True(row.ExpiresAtUtc > DateTime.UtcNow);
        if (kind != "current") Assert.False(Directory.Exists(harness.Storage.CurrentRoot));
    }

    /// <summary>Both stale responses preserve the legacy path and publish changes at its physical authority.</summary>
    [Theory]
    [InlineData("current", HttpStatusCode.OK)]
    [InlineData("scoped", HttpStatusCode.OK)]
    [InlineData("scoped", HttpStatusCode.NotModified)]
    [InlineData("flat", HttpStatusCode.NotModified)]
    public async Task StaleHits_RefreshInPlace(string kind, HttpStatusCode status)
    {
        var upstream = new RecordingTileHandler((_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([3, 4, 5]) };
            response.Headers.TryAddWithoutValidation("ETag", "\"new\"");
            response.Headers.TryAddWithoutValidation("Cache-Control", "max-age=3600");
            return Task.FromResult(response);
        });
        await using var harness = new TileCacheTestHarness(upstream, distinctRoots: true);
        string reference;
        using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await SeedAsync(harness, db, kind);
            reference = row.TileFilePath;
            row.ExpiresAtUtc = DateTime.UtcNow.AddHours(-1);
            await db.SaveChangesAsync();
            var result = await scope.ServiceProvider.GetRequiredService<TileCacheService>()
                .RetrieveTileAsync("9", "1", "2", "https://tiles.test/9/1/2.png");
            Assert.Equal(new byte[] { 7, 8 }, result.TileData);
        }
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync("9_1_2", TimeSpan.FromSeconds(5)));
        using var verification = harness.CreateScope();
        var persisted = await verification.ServiceProvider.GetRequiredService<ApplicationDbContext>().TileCacheMetadata.SingleAsync();
        Assert.Equal(reference, persisted.TileFilePath);
        Assert.True(persisted.ExpiresAtUtc > DateTime.UtcNow);
        Assert.Equal("\"new\"", persisted.ETag);
        Assert.Equal("\"old\"", Assert.Single(upstream.Requests).Headers["If-None-Match"].Single());
        Assert.Equal(status == HttpStatusCode.OK ? new byte[] { 3, 4, 5 } : [7, 8],
            await File.ReadAllBytesAsync(harness.Storage.Resolve(persisted)!));
        if (kind != "current") Assert.False(Directory.Exists(harness.Storage.CurrentRoot));
    }

    /// <summary>Low-zoom sidecars travel with their original tile and never require DB metadata.</summary>
    [Theory]
    [InlineData("current")]
    [InlineData("scoped")]
    [InlineData("flat")]
    public async Task LowZoom_HitsPreserveSidecars(string kind)
    {
        await using var harness = new TileCacheTestHarness(distinctRoots: true);
        var path = PhysicalPath(harness.Storage, kind, 5);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [7, 8]);
        await File.WriteAllTextAsync(path + ".meta", JsonSerializer.Serialize(new TileSidecarMetadata
        {
            ProviderIdentity = kind == "flat" ? null : Provider,
            ETag = "\"old\"", ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        }));
        using var scope = harness.CreateScope();
        Assert.Equal(new byte[] { 7, 8 }, (await scope.ServiceProvider.GetRequiredService<TileCacheService>()
            .RetrieveTileAsync("5", "1", "2", "https://tiles.test/5/1/2.png")).TileData);
        Assert.Empty(harness.Upstream.Requests);
        Assert.Empty(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().TileCacheMetadata);
        Assert.Equal("\"old\"", JsonSerializer.Deserialize<TileSidecarMetadata>(await File.ReadAllTextAsync(path + ".meta"))!.ETag);
    }

    /// <summary>New files and metadata use only the current authority.</summary>
    [Fact]
    public async Task NewWrites_AreLogicalAndCurrentRootOnly()
    {
        await using var harness = new TileCacheTestHarness(distinctRoots: true);
        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TileCacheService>();
        await service.CacheTileAsync("https://tiles.test/9/1/2.png", "9", "1", "2");
        await service.CacheTileAsync("https://tiles.test/5/1/2.png", "5", "1", "2");
        var row = Assert.Single(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().TileCacheMetadata);
        Assert.Equal(TileCacheStorage.CreateReference(Provider, 9, 1, 2), row.TileFilePath);
        Assert.True(File.Exists(harness.Storage.CurrentPath(Provider, 9, 1, 2)));
        Assert.True(File.Exists(harness.Storage.CurrentPath(Provider, 5, 1, 2) + ".meta"));
        Assert.Empty(Directory.EnumerateFiles(harness.CacheDirectory, "*", SearchOption.AllDirectories));
    }

    /// <summary>Nested current/legacy files contribute to totals and DB-first purge; outside rows stay harmless.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Maintenance_ResolvesRowsAndIncludesBothRoots(bool lruOnly)
    {
        await using var harness = new TileCacheTestHarness(distinctRoots: true);
        using var scope = harness.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await SeedAsync(harness, db, "current");
        await SeedAsync(harness, db, "scoped", x: 3);
        var outside = Path.GetTempFileName();
        try
        {
            db.TileCacheMetadata.Add(new TileCacheMetadata
            {
                ProviderIdentity = Provider, Zoom = 9, X = 4, Y = 2, TileLocation = new Point(4, 2),
                TileFilePath = outside, Size = 1
            });
            await db.SaveChangesAsync();
            var low = harness.Storage.CurrentPath(Provider, 5, 1, 2);
            await File.WriteAllBytesAsync(low, [1]);
            await File.WriteAllTextAsync(low + ".meta", "{}");
            var service = scope.ServiceProvider.GetRequiredService<TileCacheService>();
            Assert.Equal(4, await service.GetTotalCachedFilesAsync());
            Assert.Equal(7 / 1024.0 / 1024.0, await service.GetCacheFileSizeInMbAsync());
            if (lruOnly) await service.PurgeLRUCacheAsync();
            else await service.PurgeAllCacheAsync();
            Assert.True(File.Exists(outside));
            Assert.Equal(lruOnly, File.Exists(low));
            using var verify = harness.CreateScope();
            Assert.Empty(verify.ServiceProvider.GetRequiredService<ApplicationDbContext>().TileCacheMetadata);
            Assert.Equal(lruOnly ? 2 : 0, await service.GetTotalCachedFilesAsync());
        }
        finally { File.Delete(outside); }
    }

    /// <summary>LRU removes the referenced scoped file, never a reconstructed flat sibling.</summary>
    [Theory]
    [InlineData("current")]
    [InlineData("scoped")]
    public async Task Eviction_DeletesResolvedFile(string kind)
    {
        var upstream = new RecordingTileHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[600_000])
        }));
        await using var harness = new TileCacheTestHarness(upstream, distinctRoots: true);
        harness.Settings.MaxCacheTileSizeInMB = 1;
        using var scope = harness.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await SeedAsync(harness, db, kind);
        row.Size = 600_000;
        await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<TileCacheService>();
        service.Initialize();
        var flatSibling = Path.Combine(harness.Storage.CurrentRoot, "9_1_2.png");
        await File.WriteAllBytesAsync(flatSibling, [99]);
        await service.CacheTileAsync("https://tiles.test/9/3/2.png", "9", "3", "2");
        Assert.False(File.Exists(harness.Storage.Resolve(row)));
        Assert.True(File.Exists(flatSibling));
        using var verification = harness.CreateScope();
        Assert.Equal(3, Assert.Single(verification.ServiceProvider.GetRequiredService<ApplicationDbContext>().TileCacheMetadata).X);
    }

    /// <summary>Even a matching coordinate file cannot turn unsafe metadata into a fresh hit.</summary>
    [Fact]
    public async Task UnsafeMetadata_DoesNotServeOrDeleteOutsideBytes()
    {
        await using var harness = new TileCacheTestHarness(distinctRoots: true);
        using var scope = harness.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await SeedAsync(harness, db, "current");
        var outside = Path.GetTempFileName();
        try
        {
            row.TileFilePath = outside;
            await db.SaveChangesAsync();
            var service = scope.ServiceProvider.GetRequiredService<TileCacheService>();
            Assert.Null((await service.RetrieveTileAsync("9", "1", "2", "https://tiles.test/9/1/2.png")).TileData);
            Assert.Empty(harness.Upstream.Requests);
            await service.PurgeLRUCacheAsync();
            Assert.True(File.Exists(outside));
            Assert.True(File.Exists(harness.Storage.CurrentPath(Provider, 9, 1, 2)));
        }
        finally { File.Delete(outside); }
    }

    /// <summary>Seeds representative metadata and bytes, preserving the old native serialization.</summary>
    internal static async Task<TileCacheMetadata> SeedAsync(TileCacheTestHarness harness,
        ApplicationDbContext db, string kind, int x = 1)
    {
        var path = PhysicalPath(harness.Storage, kind, 9, x);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [7, 8]);
        var row = new TileCacheMetadata
        {
            ProviderIdentity = kind == "flat" ? null : Provider, Zoom = 9, X = x, Y = 2,
            TileLocation = new Point(x, 2), Size = 2, LastAccessed = DateTime.UtcNow.AddHours(-1),
            TileFilePath = kind == "current" ? TileCacheStorage.CreateReference(Provider, 9, x, 2) : path,
            ETag = "\"old\"", LastModifiedUpstream = DateTime.UtcNow.AddDays(-1),
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        db.TileCacheMetadata.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    /// <summary>Builds disposable fixture locations for the three accepted reference classes.</summary>
    private static string PhysicalPath(TileCacheStorage storage, string kind, int zoom, int x = 1) => kind switch
    {
        "current" => storage.CurrentPath(Provider, zoom, x, 2),
        "scoped" => Path.Combine(storage.LegacyRoot, Provider, $"{zoom}_{x}_2.png"),
        _ => Path.Combine(storage.LegacyRoot, $"{zoom}_{x}_2.png")
    };
}
