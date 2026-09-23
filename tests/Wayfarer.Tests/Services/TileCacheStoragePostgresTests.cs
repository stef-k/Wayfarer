using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Qualifies restored tile references, adoption, revalidation and xmin against disposable PostgreSQL.</summary>
[Collection("OutboundBudget")]
public sealed class TileCacheStoragePostgresTests
{
    /// <summary>Real relational metadata retains its path across hits and refresh, with concurrency still enforced.</summary>
    [PostgresFact]
    public async Task RestoredCache_HitsRefreshesAndPurgesWithoutRewritingReferences()
    {
        var fixture = new PostgresMigrationTestFixture();
        await fixture.InitializeAsync();
        try
        {
            var upstream = new RecordingTileHandler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.NotModified);
                response.Headers.TryAddWithoutValidation("Cache-Control", "max-age=3600");
                return Task.FromResult(response);
            });
            await using var harness = new TileCacheTestHarness(upstream, distinctRoots: true,
                databaseFactory: () => fixture.CreateContext());
            using var scope = harness.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var canonical = await TileCacheStorageQualificationTests.SeedAsync(harness, db, "current");
            var legacy = await TileCacheStorageQualificationTests.SeedAsync(harness, db, "scoped", 3);
            var flat = await TileCacheStorageQualificationTests.SeedAsync(harness, db, "flat", 4);
            var paths = new[] { canonical.TileFilePath, legacy.TileFilePath, flat.TileFilePath };
            var service = scope.ServiceProvider.GetRequiredService<TileCacheService>();
            foreach (var x in new[] { "1", "3", "4" })
                Assert.Equal(new byte[] { 7, 8 }, (await service.RetrieveTileAsync("9", x, "2", "https://tiles.test/9/1/2.png")).TileData);
            Assert.Empty(upstream.Requests);
            Assert.NotEqual(0u, canonical.RowVersion);
            Assert.NotNull(flat.ProviderIdentity);

            legacy.ExpiresAtUtc = DateTime.UtcNow.AddHours(-1);
            await db.SaveChangesAsync();
            scope.ServiceProvider.GetRequiredService<TileMetadataHotCache>().Clear();
            await service.RetrieveTileAsync("9", "3", "2", "https://tiles.test/9/3/2.png");
            Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync("9_3_2", TimeSpan.FromSeconds(5)));
            Assert.Single(upstream.Requests);
            legacy.ETag = "\"concurrent\"";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();
            Assert.Equal(paths, await db.TileCacheMetadata.OrderBy(t => t.X).Select(t => t.TileFilePath).ToArrayAsync());
            Assert.True((await db.TileCacheMetadata.SingleAsync(t => t.X == 3)).ExpiresAtUtc > DateTime.UtcNow);
            await service.PurgeAllCacheAsync();
            Assert.Empty(await db.TileCacheMetadata.AsNoTracking().ToListAsync());
            Assert.Empty(harness.Storage.EnumerateFiles());
        }
        finally { await fixture.DisposeAsync(); }
    }
}
