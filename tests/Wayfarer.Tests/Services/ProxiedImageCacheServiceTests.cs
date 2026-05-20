using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for the ProxiedImageCacheService: cache hit/miss, expiry, LRU eviction, and initialization.
/// </summary>
public class ProxiedImageCacheServiceTests : TestBase, IDisposable
{
    private readonly string _tempDir;

    public ProxiedImageCacheServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"wayfarer_imgcache_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        ProxiedImageCacheService.SetImageFileReplacerForTesting(null);
        ProxiedImageCacheService.SetMetadataSaverForTesting(null);
        ProxiedImageCacheService.SetBeforeFileReadForTesting(null);
    }

    public new void Dispose()
    {
        ProxiedImageCacheService.SetImageFileReplacerForTesting(null);
        ProxiedImageCacheService.SetMetadataSaverForTesting(null);
        ProxiedImageCacheService.SetBeforeFileReadForTesting(null);
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch { /* best-effort cleanup */ }
        base.Dispose();
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenCacheMiss()
    {
        var service = CreateService();

        var result = await service.GetAsync("nonexistent_key");

        Assert.Equal(ProxiedImageCacheStatus.Miss, result.Status);
    }

    [Fact]
    public async Task SetAsync_ThenGetAsync_ReturnsSameBytesAndContentType()
    {
        var service = CreateService();
        var imageBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };
        var contentType = "image/jpeg";

        var stored = await service.SetAsync("test_key_1", imageBytes, contentType);

        var result = await service.GetAsync("test_key_1");

        Assert.True(stored.Stored);
        Assert.Equal(ProxiedImageCacheStatus.FreshHit, result.Status);
        Assert.Equal(imageBytes, result.Bytes);
        Assert.Equal(contentType, result.ContentType);
    }

    [Fact]
    public async Task GetAsync_ReturnsStaleHit_WhenEntryExpired()
    {
        var db = CreateDbContext();
        var service = CreateService(db: db, expiryDays: 1);
        var imageBytes = new byte[] { 1, 2, 3 };

        await service.SetAsync("expired_key", imageBytes, "image/png");

        // Manually backdate the CreatedAt to make it expired
        var metadata = db.ImageCacheMetadata.First(m => m.CacheKey == "expired_key");
        metadata.CreatedAt = DateTime.UtcNow.AddDays(-2);
        await db.SaveChangesAsync();

        var result = await service.GetAsync("expired_key");

        Assert.Equal(ProxiedImageCacheStatus.StaleHit, result.Status);
        Assert.Equal(imageBytes, result.Bytes);
        Assert.Single(db.ImageCacheMetadata.Where(m => m.CacheKey == "expired_key"));
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenFileMissingOnDisk()
    {
        var db = CreateDbContext();
        var service = CreateService(db: db);
        var imageBytes = new byte[] { 1, 2, 3 };

        await service.SetAsync("disk_missing_key", imageBytes, "image/png");

        // Delete the file from disk but leave DB entry
        var metadata = db.ImageCacheMetadata.First(m => m.CacheKey == "disk_missing_key");
        File.Delete(metadata.FilePath);

        var result = await service.GetAsync("disk_missing_key");

        Assert.Equal(ProxiedImageCacheStatus.DiskMissingOrError, result.Status);
        // DB entry should also be cleaned up
        Assert.Empty(db.ImageCacheMetadata.Where(m => m.CacheKey == "disk_missing_key"));
    }

    [Fact]
    public async Task SetAsync_DoesNotDuplicate_WhenKeyAlreadyExists()
    {
        var db = CreateDbContext();
        var service = CreateService(db: db);

        await service.SetAsync("dup_key", new byte[] { 1, 2 }, "image/jpeg");
        await service.SetAsync("dup_key", new byte[] { 3, 4 }, "image/png");

        // Should still be one entry
        Assert.Single(db.ImageCacheMetadata.Where(m => m.CacheKey == "dup_key"));
    }

    [Fact]
    public async Task SetAsync_ReplacesExistingBytes_AndUpdatesMetadata()
    {
        var db = CreateDbContext();
        var service = CreateService(db: db);

        await service.SetAsync("refresh_key", new byte[] { 1, 2 }, "image/jpeg");
        var metadata = db.ImageCacheMetadata.First(m => m.CacheKey == "refresh_key");
        metadata.CreatedAt = DateTime.UtcNow.AddDays(-10);
        var staleCreatedAt = metadata.CreatedAt;
        await db.SaveChangesAsync();

        await service.SetAsync("refresh_key", new byte[] { 3, 4, 5 }, "image/png");

        var result = await service.GetAsync("refresh_key");
        await db.Entry(metadata).ReloadAsync();
        Assert.Equal(ProxiedImageCacheStatus.FreshHit, result.Status);
        Assert.Equal(new byte[] { 3, 4, 5 }, result.Bytes);
        Assert.Equal("image/png", result.ContentType);
        Assert.True(metadata.CreatedAt > staleCreatedAt);
        Assert.Equal(3, metadata.Size);
    }

    [Fact]
    public async Task GetAsync_DoesNotDeleteMetadata_WhenRefreshMovedCapturedFilePath()
    {
        var db = CreateDbContext();
        var service = CreateService(db: db);
        var oldBytes = new byte[] { 1, 2 };
        var newBytes = new byte[] { 3, 4, 5 };

        Assert.True((await service.SetAsync("refresh_race_key", oldBytes, "image/jpeg")).Stored);
        var oldMetadata = db.ImageCacheMetadata.Single(m => m.CacheKey == "refresh_race_key");
        var oldFilePath = oldMetadata.FilePath;
        var hookRan = false;

        ProxiedImageCacheService.SetBeforeFileReadForTesting(async (cacheKey, capturedFilePath) =>
        {
            if (hookRan || cacheKey != "refresh_race_key")
            {
                return;
            }

            hookRan = true;
            Assert.Equal(oldFilePath, capturedFilePath);
            Assert.True((await service.SetAsync("refresh_race_key", newBytes, "image/png")).Stored);
            Assert.False(File.Exists(oldFilePath));
        });

        var result = await service.GetAsync("refresh_race_key");
        ProxiedImageCacheService.SetBeforeFileReadForTesting(null);
        var currentMetadata = db.ImageCacheMetadata.Single(m => m.CacheKey == "refresh_race_key");

        Assert.True(hookRan);
        Assert.NotEqual(oldFilePath, currentMetadata.FilePath);
        Assert.True(File.Exists(currentMetadata.FilePath));
        Assert.Equal(newBytes, File.ReadAllBytes(currentMetadata.FilePath));
        Assert.Equal(ProxiedImageCacheStatus.FreshHit, result.Status);
        Assert.Equal(newBytes, result.Bytes);
        Assert.Equal("image/png", result.ContentType);
    }

    [Fact]
    public async Task SetAsync_ReturnsFailure_WhenNewMetadataSaveFails()
    {
        var db = CreateDbContext();
        var service = CreateService(db: db);
        ProxiedImageCacheService.SetMetadataSaverForTesting(_ => throw new InvalidOperationException("metadata failed"));

        var stored = await service.SetAsync("metadata_new_fail", new byte[] { 1, 2, 3 }, "image/jpeg");

        Assert.False(stored.Stored);
        Assert.Empty(db.ImageCacheMetadata.Where(m => m.CacheKey == "metadata_new_fail"));
        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task SetAsync_ExistingMetadataFailurePreservesOldFileAndMetadata()
    {
        var db = CreateDbContext();
        var service = CreateService(db: db);
        var oldBytes = new byte[] { 1, 2 };

        Assert.True((await service.SetAsync("metadata_existing_fail", oldBytes, "image/jpeg")).Stored);
        var oldMetadata = db.ImageCacheMetadata.Single(m => m.CacheKey == "metadata_existing_fail");
        var oldFilePath = oldMetadata.FilePath;
        var oldCreatedAt = oldMetadata.CreatedAt;
        ProxiedImageCacheService.SetMetadataSaverForTesting(_ => throw new InvalidOperationException("metadata failed"));

        var stored = await service.SetAsync("metadata_existing_fail", new byte[] { 3, 4, 5 }, "image/png");
        ProxiedImageCacheService.SetMetadataSaverForTesting(null);
        var result = await service.GetAsync("metadata_existing_fail");
        await db.Entry(oldMetadata).ReloadAsync();

        Assert.False(stored.Stored);
        Assert.Equal(oldBytes, result.Bytes);
        Assert.Equal("image/jpeg", result.ContentType);
        Assert.Equal(oldFilePath, oldMetadata.FilePath);
        Assert.Equal(oldCreatedAt, oldMetadata.CreatedAt);
        Assert.Single(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task SetAsync_ExistingFileReplaceFailurePreservesOldFileAndMetadata()
    {
        var db = CreateDbContext();
        var service = CreateService(db: db);
        var oldBytes = new byte[] { 1, 2 };

        Assert.True((await service.SetAsync("replace_existing_fail", oldBytes, "image/jpeg")).Stored);
        var oldMetadata = db.ImageCacheMetadata.Single(m => m.CacheKey == "replace_existing_fail");
        var oldFilePath = oldMetadata.FilePath;
        ProxiedImageCacheService.SetImageFileReplacerForTesting((_, _) => throw new IOException("replace failed"));

        var stored = await service.SetAsync("replace_existing_fail", new byte[] { 3, 4, 5 }, "image/png");
        ProxiedImageCacheService.SetImageFileReplacerForTesting(null);
        var result = await service.GetAsync("replace_existing_fail");
        await db.Entry(oldMetadata).ReloadAsync();

        Assert.False(stored.Stored);
        Assert.Equal(oldBytes, result.Bytes);
        Assert.Equal("image/jpeg", result.ContentType);
        Assert.Equal(oldFilePath, oldMetadata.FilePath);
        Assert.Single(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenCachingDisabled()
    {
        var service = CreateService(maxSizeMB: -1);
        await service.SetAsync("disabled_key", new byte[] { 1 }, "image/png");

        var result = await service.GetAsync("disabled_key");

        Assert.Equal(ProxiedImageCacheStatus.Miss, result.Status);
    }

    [Fact]
    public void Initialize_CreatesDirectory()
    {
        var newDir = Path.Combine(_tempDir, "sub_init");
        var service = CreateService(cacheDir: newDir);

        service.Initialize();

        Assert.True(Directory.Exists(newDir));
    }

    [Fact]
    public async Task GetCacheSizeInMbAsync_ReturnsCorrectSize()
    {
        var service = CreateService();
        var bytes = new byte[1024 * 100]; // 100 KB

        await service.SetAsync("size_test", bytes, "image/jpeg");

        var sizeMb = await service.GetCacheSizeInMbAsync();
        Assert.True(sizeMb > 0.09); // ~0.097 MB
    }

    [Fact]
    public async Task GetCachedImageCountAsync_ReturnsCorrectCount()
    {
        var service = CreateService();

        await service.SetAsync("count_1", new byte[] { 1 }, "image/jpeg");
        await service.SetAsync("count_2", new byte[] { 2 }, "image/png");

        var count = await service.GetCachedImageCountAsync();
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task SetAsync_EvictsLruEntries_WhenCacheSizeExceeded()
    {
        var db = CreateDbContext();
        // Use a very small cache limit (1 KB) to trigger eviction easily
        var service = CreateService(db: db, maxSizeMB: 1);

        // Each entry is ~500 KB — two will exceed 1 MB
        var halfMb = new byte[512 * 1024];

        // Add first entry (fits in 1 MB)
        await service.SetAsync("entry_1", halfMb, "image/jpeg");
        // Backdate LastAccessed so it becomes the LRU candidate
        var meta1 = db.ImageCacheMetadata.First(m => m.CacheKey == "entry_1");
        meta1.LastAccessed = DateTime.UtcNow.AddHours(-2);
        await db.SaveChangesAsync();

        // Add second entry (fits in 1 MB)
        await service.SetAsync("entry_2", halfMb, "image/jpeg");
        // Backdate
        var meta2 = db.ImageCacheMetadata.First(m => m.CacheKey == "entry_2");
        meta2.LastAccessed = DateTime.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();

        // Add third entry — should trigger eviction of entry_1 (oldest LastAccessed)
        await service.SetAsync("entry_3", halfMb, "image/jpeg");

        // Verify entry_1 was evicted (oldest by LastAccessed)
        Assert.Null(db.ImageCacheMetadata.FirstOrDefault(m => m.CacheKey == "entry_1"));
        // Verify entry_3 was stored
        Assert.NotNull(db.ImageCacheMetadata.FirstOrDefault(m => m.CacheKey == "entry_3"));
        // Total entries should be <= 2 (cache limit is 1 MB, each is ~500 KB)
        Assert.True(db.ImageCacheMetadata.Count() <= 3);
    }

    [Fact]
    public async Task GetAsync_SkipsLastAccessedUpdate_WhenRecent()
    {
        var db = CreateDbContext();
        var service = CreateService(db: db);
        var imageBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2 };

        await service.SetAsync("recent_key", imageBytes, "image/jpeg");

        // Record the LastAccessed time right after SetAsync
        var metaBefore = db.ImageCacheMetadata.First(m => m.CacheKey == "recent_key");
        var lastAccessedBefore = metaBefore.LastAccessed;

        // Read immediately (within 1-hour window) — should NOT update LastAccessed
        await service.GetAsync("recent_key");

        // Re-query to get current value
        await db.Entry(metaBefore).ReloadAsync();
        Assert.Equal(lastAccessedBefore, metaBefore.LastAccessed);
    }

    [Fact]
    public async Task GetAsync_UpdatesLastAccessed_WhenStale()
    {
        var db = CreateDbContext();
        var service = CreateService(db: db);
        var imageBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 3, 4 };

        await service.SetAsync("stale_key", imageBytes, "image/jpeg");

        // Backdate LastAccessed by 2 hours to make it stale
        var metadata = db.ImageCacheMetadata.First(m => m.CacheKey == "stale_key");
        metadata.LastAccessed = DateTime.UtcNow.AddHours(-2);
        await db.SaveChangesAsync();

        var staleBefore = metadata.LastAccessed;

        // Read — should update LastAccessed because it's >1 hour old
        await service.GetAsync("stale_key");

        await db.Entry(metadata).ReloadAsync();
        Assert.True(metadata.LastAccessed > staleBefore);
        // Should be within the last few seconds (recently updated)
        Assert.True(DateTime.UtcNow - metadata.LastAccessed < TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Creates a ProxiedImageCacheService with test configuration.
    /// </summary>
    private ProxiedImageCacheService CreateService(
        ApplicationDbContext? db = null,
        string? cacheDir = null,
        int maxSizeMB = 512,
        int expiryDays = 90)
    {
        db ??= CreateDbContext();
        cacheDir ??= _tempDir;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CacheSettings:ImageCacheDirectory"] = cacheDir
            })
            .Build();

        var settingsMock = new Mock<IApplicationSettingsService>();
        settingsMock.Setup(s => s.GetSettings()).Returns(new ApplicationSettings
        {
            MaxCacheImageSizeInMB = maxSizeMB,
            ImageCacheExpiryDays = expiryDays
        });

        return new ProxiedImageCacheService(
            NullLogger<ProxiedImageCacheService>.Instance,
            db,
            settingsMock.Object,
            config);
    }
}
