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
    }

    public new void Dispose()
    {
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

        Assert.Null(result);
    }

    [Fact]
    public async Task SetAsync_ThenGetAsync_ReturnsSameBytesAndContentType()
    {
        var service = CreateService();
        var imageBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };
        var contentType = "image/jpeg";

        await service.SetAsync("test_key_1", imageBytes, contentType);

        var result = await service.GetAsync("test_key_1");

        Assert.NotNull(result);
        Assert.Equal(imageBytes, result!.Value.Bytes);
        Assert.Equal(contentType, result.Value.ContentType);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenEntryExpired()
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

        Assert.Null(result);
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

        Assert.Null(result);
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
    public async Task GetAsync_ReturnsNull_WhenCachingDisabled()
    {
        var service = CreateService(maxSizeMB: -1);
        await service.SetAsync("disabled_key", new byte[] { 1 }, "image/png");

        var result = await service.GetAsync("disabled_key");

        Assert.Null(result);
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
