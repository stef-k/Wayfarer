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

    /// <summary>Ownership protection filters scoped metadata by only the selected candidate paths.</summary>
    [Fact]
    public void ScopedOwnershipProtection_IsRestrictedToCandidatePaths()
    {
        var candidatePaths = Enumerable
            .Range(0, 50)
            .Select(index => $"candidate-{index}.png")
            .ToArray();
        var metadata = new[]
        {
            new TileCacheMetadata
            {
                ProviderIdentity = "scoped",
                TileFilePath = candidatePaths[0],
                TileLocation = new Point(0, 0)
            },
            new TileCacheMetadata
            {
                ProviderIdentity = "scoped",
                TileFilePath = candidatePaths[1].ToUpperInvariant(),
                TileLocation = new Point(1, 1)
            },
            new TileCacheMetadata
            {
                ProviderIdentity = null,
                TileFilePath = candidatePaths[2],
                TileLocation = new Point(2, 2)
            },
            new TileCacheMetadata
            {
                ProviderIdentity = "scoped",
                TileFilePath = "unrelated.png",
                TileLocation = new Point(3, 3)
            }
        }.AsQueryable();
        var query = TileCacheService.BuildScopedPathProtectionQuery(metadata, candidatePaths);

        Assert.Equal(
            [candidatePaths[0], candidatePaths[1].ToUpperInvariant()],
            query.ToArray());
    }
}
