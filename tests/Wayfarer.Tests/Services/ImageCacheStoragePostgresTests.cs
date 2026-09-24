using Microsoft.EntityFrameworkCore;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Real PostgreSQL qualification of restored image rows, commit points, and xmin concurrency.</summary>
[Collection(ImageProxyStaticStateTestCollection.Name)]
public sealed class ImageCacheStoragePostgresTests
{
    /// <summary>Both legacy generations survive failed refresh and promote only after committed replacement.</summary>
    [PostgresFact]
    public async Task RestoredRowsPreserveOnFailurePromoteOnCommitAndEnforceXmin()
    {
        await using var fixture = new PostgresMigrationTestFixture();
        await fixture.InitializeAsync();
        using var directory = new TestDirectory();
        var storage = new ImageCacheStorage(Path.Combine(directory.Path, "current"), Path.Combine(directory.Path, "legacy"));
        await using var db = fixture.CreateContext();
        var service = ImageCacheStorageQualificationTests.Service(db, storage);
        foreach (var replacement in new[] { false, true })
        {
            var row = await ImageCacheStorageQualificationTests.SeedAsync(db, storage, "legacy", replacement, stale: true);
            var legacyPath = row.FilePath;
            var version = row.RowVersion;
            Assert.NotEqual(0u, version);
            Assert.Equal(ProxiedImageCacheStatus.StaleHit, (await service.GetAsync(row.CacheKey)).Status);
            Assert.Equal(legacyPath, row.FilePath);
            await ImageCacheStorageQualificationTests.AssertFailurePreservesAsync(db, storage, row);
            Assert.Equal(version, row.RowVersion);

            await using var competing = fixture.CreateContext();
            var staleRow = await competing.ImageCacheMetadata.SingleAsync(x => x.Id == row.Id);
            ProxiedImageCacheService.SetMetadataSaverForTesting(async context =>
            {
                // A separate reader still sees the old reference while replacement bytes exist uncommitted.
                await using var observer = fixture.CreateContext();
                Assert.Equal(legacyPath, (await observer.ImageCacheMetadata.SingleAsync(x => x.Id == row.Id)).FilePath);
                Assert.True(File.Exists(legacyPath));
                Assert.True(File.Exists(storage.Resolve(row)));
                return await context.SaveChangesAsync();
            });
            try { Assert.True((await service.SetAsync(row.CacheKey, [3, 4, 5], "image/png")).Stored); }
            finally { ProxiedImageCacheService.SetMetadataSaverForTesting(null); }
            Assert.NotEqual(version, row.RowVersion);
            Assert.True(ImageCacheStorage.IsReference(row.FilePath, row.CacheKey));
            Assert.False(File.Exists(legacyPath));
            Assert.Equal(new byte[] { 3, 4, 5 }, (await service.GetAsync(row.CacheKey)).Bytes);
            staleRow.ContentType = "image/webp";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => competing.SaveChangesAsync());
        }
    }
}
