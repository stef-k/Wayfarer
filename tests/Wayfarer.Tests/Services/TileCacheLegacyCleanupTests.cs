using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves bounded retirement of legacy entries whose provider provenance is untrusted.</summary>
[Collection("OutboundBudget")]
public sealed class TileCacheLegacyCleanupTests
{
    /// <summary>One maintenance invocation retires no more than its fixed fifty-entry batch.</summary>
    [Fact]
    public async Task CustomProvider_RetiresOneBoundedLegacyBatch()
    {
        await using var harness = new TileCacheTestHarness();
        harness.Settings.TileProviderKey = "custom";
        harness.Settings.TileProviderUrlTemplate = "https://tiles.example.test/{z}/{x}/{y}.png";
        using (var seedScope = harness.CreateScope())
        {
            var database = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            for (var index = 0; index < 55; index++)
            {
                var path = Path.Combine(harness.CacheDirectory, $"9_{index}_1.png");
                await File.WriteAllBytesAsync(path, [1, 2, 3]);
                database.TileCacheMetadata.Add(new TileCacheMetadata
                {
                    Zoom = 9,
                    X = index,
                    Y = 1,
                    TileLocation = new Point(index, 1),
                    LastAccessed = DateTime.UtcNow,
                    Size = 3,
                    TileFilePath = path,
                    ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
                });
            }

            await database.SaveChangesAsync();
        }

        int retired;
        using (var maintenanceScope = harness.CreateScope())
        {
            retired = await maintenanceScope.ServiceProvider
                .GetRequiredService<TileCacheService>()
                .RetireLegacyCacheBatchAsync(CancellationToken.None);
        }

        using var verifyScope = harness.CreateScope();
        var remaining = verifyScope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>()
            .TileCacheMetadata
            .Count();
        var remainingFiles = Directory
            .EnumerateFiles(harness.CacheDirectory, "*.png", SearchOption.TopDirectoryOnly)
            .Count();

        Assert.Equal(50, retired);
        Assert.Equal(5, remaining);
        Assert.Equal(5, remainingFiles);
    }

    /// <summary>Canonical OSM leaves unscoped entries available for lazy, no-download adoption.</summary>
    [Fact]
    public async Task CanonicalOsm_DoesNotRetireLegacyBatch()
    {
        await using var harness = new TileCacheTestHarness();
        var path = Path.Combine(harness.CacheDirectory, "5_1_1.png");
        await File.WriteAllBytesAsync(path, [1]);
        using var scope = harness.CreateScope();

        var retired = await scope.ServiceProvider
            .GetRequiredService<TileCacheService>()
            .RetireLegacyCacheBatchAsync(CancellationToken.None);

        Assert.Equal(0, retired);
        Assert.True(File.Exists(path));
    }

    /// <summary>Logical scoped ownership protects the same bytes referenced by a legacy absolute row.</summary>
    [Fact]
    public async Task Retirement_ProtectsMixedRepresentations()
    {
        await using var harness = new TileCacheTestHarness();
        harness.Settings.TileProviderKey = "custom";
        harness.Settings.TileProviderUrlTemplate = "https://tiles.example.test/{z}/{x}/{y}.png";
        var provider = new string('A', 64);
        var storage = new TileCacheStorage(harness.CacheDirectory, Path.Combine(harness.CacheDirectory, provider));
        Directory.CreateDirectory(storage.LegacyRoot);
        var path = storage.CurrentPath(provider, 9, 1, 2);
        await File.WriteAllBytesAsync(path, [7]);
        using var scope = harness.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.TileCacheMetadata.AddRange(
            new TileCacheMetadata { Zoom = 9, X = 1, Y = 2, TileFilePath = path, TileLocation = new Point(1, 2) },
            new TileCacheMetadata { Zoom = 9, X = 1, Y = 2, ProviderIdentity = provider,
                TileFilePath = TileCacheStorage.CreateReference(provider, 9, 1, 2), TileLocation = new Point(1, 2) });
        await db.SaveChangesAsync();
        var service = ActivatorUtilities.CreateInstance<TileCacheService>(scope.ServiceProvider, storage);
        Assert.Equal(1, await service.RetireLegacyCacheBatchAsync(CancellationToken.None));
        Assert.True(File.Exists(path));
        Assert.Equal(provider, Assert.Single(db.TileCacheMetadata).ProviderIdentity);
    }

    /// <summary>Malformed legacy metadata cannot authorize deleting an outside file.</summary>
    [Fact]
    public async Task Retirement_RejectsOutsidePath()
    {
        await using var harness = new TileCacheTestHarness();
        harness.Settings.TileProviderKey = "custom";
        harness.Settings.TileProviderUrlTemplate = "https://tiles.example.test/{z}/{x}/{y}.png";
        var outside = Path.GetTempFileName();
        try
        {
            using var scope = harness.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.TileCacheMetadata.Add(new TileCacheMetadata
            {
                Zoom = 9, X = 1, Y = 1, TileFilePath = outside, TileLocation = new Point(1, 1)
            });
            await db.SaveChangesAsync();
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<TileCacheService>()
                .RetireLegacyCacheBatchAsync(CancellationToken.None));
            Assert.True(File.Exists(outside));
        }
        finally { File.Delete(outside); }
    }
}
