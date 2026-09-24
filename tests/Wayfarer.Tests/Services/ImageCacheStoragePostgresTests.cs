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

    /// <summary>An hourly access write cannot revert a refresh committed after the reader captured its old xmin.</summary>
    [PostgresFact]
    public async Task LastAccessedConflictPreservesConcurrentRefreshAndServesItsGeneration()
    {
        await using var fixture = new PostgresMigrationTestFixture();
        await fixture.InitializeAsync();
        using var directory = new TestDirectory();
        var storage = new ImageCacheStorage(Path.Combine(directory.Path, "current"), Path.Combine(directory.Path, "legacy"));
        await using var readerDb = fixture.CreateContext();
        var oldRow = await ImageCacheStorageQualificationTests.SeedAsync(readerDb, storage, "legacy", stale: true);
        oldRow.LastAccessed = DateTime.UtcNow.AddHours(-2);
        await readerDb.SaveChangesAsync();
        var oldPath = oldRow.FilePath;
        var oldVersion = oldRow.RowVersion;
        await using var writerDb = fixture.CreateContext();
        var writer = ImageCacheStorageQualificationTests.Service(writerDb, storage);
        var reader = ImageCacheStorageQualificationTests.Service(readerDb, storage);
        var conflicts = 0;
        var refreshCommitted = false;
        string? refreshedReference = null;
        DateTime refreshedCreatedAt = default;
        ProxiedImageCacheService.SetMetadataSaverForTesting(async context =>
        {
            if (ReferenceEquals(context, readerDb) && !refreshCommitted)
            {
                // The reader has already captured stale fields and decided its hourly write is due.
                Assert.Equal(oldVersion, oldRow.RowVersion);
                Assert.True((await writer.SetAsync(oldRow.CacheKey, [3, 4, 5], "image/png")).Stored);
                var refreshed = await writerDb.ImageCacheMetadata.SingleAsync();
                refreshedReference = refreshed.FilePath;
                refreshedCreatedAt = refreshed.CreatedAt;
                Assert.NotEqual(oldVersion, refreshed.RowVersion);
                Assert.False(File.Exists(oldPath));
                refreshCommitted = true;
            }
            try { return await context.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException)
            {
                conflicts++;
                throw;
            }
        });
        ProxiedImageCacheResult result;
        try { result = await reader.GetAsync(oldRow.CacheKey); }
        finally { ProxiedImageCacheService.SetMetadataSaverForTesting(null); }

        Assert.True(refreshCommitted);
        Assert.Equal(1, conflicts);
        Assert.Equal(ProxiedImageCacheStatus.FreshHit, result.Status);
        Assert.Equal(new byte[] { 3, 4, 5 }, result.Bytes);
        Assert.Equal("image/png", result.ContentType);
        var current = await readerDb.ImageCacheMetadata.AsNoTracking().SingleAsync();
        Assert.Equal(refreshedReference, current.FilePath);
        Assert.True(ImageCacheStorage.IsReference(current.FilePath, current.CacheKey));
        Assert.Equal(3, current.Size);
        Assert.Equal("image/png", current.ContentType);
        // PostgreSQL stores microseconds while the publishing context initially retains .NET ticks.
        Assert.Equal(refreshedCreatedAt.Ticks / 10, current.CreatedAt.Ticks / 10);
        Assert.True(current.LastAccessed >= current.CreatedAt);
        Assert.Equal(storage.Resolve(current), result.FilePath);
        Assert.Equal(new byte[] { 3, 4, 5 }, await File.ReadAllBytesAsync(result.FilePath!));
        Assert.False(File.Exists(oldPath));
    }

}
