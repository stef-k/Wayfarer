using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Disposable current/legacy storage and metadata commit-point qualification.</summary>
[Collection(ImageProxyStaticStateTestCollection.Name)]
public sealed class ImageCacheStorageQualificationTests : TestBase
{
    /// <summary>Both legacy generations remain readable without promotion, whether fresh or stale.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LegacyHitsRetainReferenceAndMetadata(bool replacement, bool stale)
    {
        var db = CreateDbContext();
        var storage = Storage();
        var row = await SeedAsync(db, storage, "legacy", replacement, stale);
        var original = db.Entry(row).CurrentValues.Clone();
        var result = await Service(db, storage).GetAsync(row.CacheKey);
        Assert.Equal(stale ? ProxiedImageCacheStatus.StaleHit : ProxiedImageCacheStatus.FreshHit, result.Status);
        Assert.Equal(new byte[] { 1, 2 }, result.Bytes);
        Assert.Equal("image/jpeg", result.ContentType);
        Assert.Equal(row.FilePath, result.FilePath);
        Assert.Equal(original.Properties.Select(p => original[p]), db.Entry(row).CurrentValues.Properties.Select(p => db.Entry(row).CurrentValues[p]));
        Assert.False(Directory.Exists(storage.CurrentRoot));
    }

    /// <summary>A successful refresh promotes legacy and unsafe rows and rotates current generations at commit.</summary>
    [Theory]
    [InlineData("current")]
    [InlineData("legacy")]
    [InlineData("unsafe")]
    public async Task RefreshCommitsBeforeDeletingOldBytes(string kind)
    {
        var db = CreateDbContext();
        var storage = Storage();
        var row = await SeedAsync(db, storage, kind, replacement: true, stale: true);
        var oldReference = row.FilePath;
        var oldPath = storage.Resolve(row) ?? row.FilePath;
        var service = Service(db, storage);
        if (kind == "unsafe")
        {
            var miss = await service.GetAsync(row.CacheKey);
            Assert.Equal(ProxiedImageCacheStatus.DiskMissingOrError, miss.Status);
            Assert.Null(miss.FilePath);
            Assert.Equal(oldReference, (await db.ImageCacheMetadata.SingleAsync()).FilePath);
        }
        ProxiedImageCacheService.SetMetadataSaverForTesting(async context =>
        {
            Assert.True(File.Exists(oldPath));
            Assert.True(ImageCacheStorage.IsReference(row.FilePath, row.CacheKey));
            Assert.Equal(101, row.FilePath.Length);
            Assert.True(File.Exists(storage.Resolve(row)));
            return await context.SaveChangesAsync();
        });
        try { Assert.True((await service.SetAsync(row.CacheKey, [3, 4, 5], "image/png")).Stored); }
        finally { ProxiedImageCacheService.SetMetadataSaverForTesting(null); }
        Assert.NotEqual(oldReference, row.FilePath);
        Assert.Equal(kind == "unsafe", File.Exists(oldPath));
        Assert.Equal(new byte[] { 3, 4, 5 }, (await service.GetAsync(row.CacheKey)).Bytes);
        // A second refresh must retain exactly one generation component.
        Assert.True((await service.SetAsync(row.CacheKey, [6], "image/webp")).Stored);
        Assert.Equal(101, row.FilePath.Length);
        Assert.Single(Directory.GetFiles(storage.CurrentRoot));
    }

    /// <summary>Publish and metadata failures keep every old field and byte, cleaning the new generation.</summary>
    [Theory]
    [InlineData("legacy", false)]
    [InlineData("legacy", true)]
    [InlineData("current", false)]
    [InlineData("current", true)]
    public async Task RefreshFailurePreservesOldState(string kind, bool publishFailure)
    {
        var db = CreateDbContext();
        var storage = Storage();
        var row = await SeedAsync(db, storage, kind, replacement: true, stale: true);
        await AssertFailurePreservesAsync(db, storage, row, publishFailure);
    }

    /// <summary>Missing legacy generations retire metadata; invalid keys never create files or rows.</summary>
    [Fact]
    public async Task MissingLegacyAndInvalidKeysConvergeSafely()
    {
        var db = CreateDbContext();
        var storage = Storage();
        var row = await SeedAsync(db, storage, "legacy");
        File.Delete(row.FilePath);
        var service = Service(db, storage);
        Assert.Equal(ProxiedImageCacheStatus.DiskMissingOrError, (await service.GetAsync(row.CacheKey)).Status);
        Assert.Empty(db.ImageCacheMetadata);
        Assert.False((await service.SetAsync("../invalid", [1], "image/png")).Stored);
        Assert.Equal(ProxiedImageCacheStatus.Miss, (await service.GetAsync("../invalid")).Status);
        Assert.False(Directory.Exists(storage.CurrentRoot));
    }

    /// <summary>Initialization creates only the current write authority, never a configured absent legacy root.</summary>
    [Fact]
    public void InitializeDoesNotCreateLegacyRoot()
    {
        var storage = Storage();
        Service(CreateDbContext(), storage).Initialize();
        Assert.True(Directory.Exists(storage.CurrentRoot));
        Assert.False(Directory.Exists(storage.LegacyRoot));
    }

    /// <summary>LRU commits its ordered batch before touching files or accounting; failed commits preserve both.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LruRetirementIsDatabaseFirst(bool fail)
    {
        var db = CreateDbContext();
        var storage = Storage();
        var rows = new List<ImageCacheMetadata>();
        foreach (var kind in new[] { "current", "legacy", "unsafe" })
            rows.Add(await SeedAsync(db, storage, kind));
        // Fifty oldest rows form the existing batch; the newest row must survive.
        for (var i = 3; i < 51; i++) rows.Add(await SeedAsync(db, storage, "current"));
        for (var i = 0; i < rows.Count; i++) rows[i].LastAccessed = DateTime.UtcNow.AddMinutes(-100 + i);
        await db.SaveChangesAsync();
        var paths = rows.Select(row => storage.Resolve(row) ?? row.FilePath).ToArray();
        var sizeField = typeof(ProxiedImageCacheService).GetField("_currentCacheSize", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldSize = (long)sizeField.GetValue(null)!;
        const long initialSize = 1024 * 1024 + 98;
        sizeField.SetValue(null, initialSize);
        var service = Service(db, storage, maxSize: 1);
        var newKey = new string('f', 64);
        var saves = 0;
        ProxiedImageCacheService.SetMetadataSaverForTesting(async context =>
        {
            saves++;
            if (saves == 1)
            {
                Assert.Equal(50, context.ChangeTracker.Entries<ImageCacheMetadata>().Count(e => e.State == EntityState.Deleted));
                Assert.All(paths, path => Assert.True(File.Exists(path)));
                Assert.Equal(initialSize, (long)sizeField.GetValue(null)!);
                if (fail) throw new DbUpdateException("injected retirement failure");
                return await context.SaveChangesAsync();
            }
            return await context.SaveChangesAsync();
        });
        try
        {
            Assert.Equal(!fail, (await service.SetAsync(newKey, [9], "image/png")).Stored);
            if (fail)
            {
                Assert.Equal(initialSize, (long)sizeField.GetValue(null)!);
                Assert.Equal(51, await db.ImageCacheMetadata.CountAsync());
                Assert.All(paths, path => Assert.True(File.Exists(path)));
            }
            else
            {
                Assert.Equal(initialSize - 100 + 1, (long)sizeField.GetValue(null)!);
                Assert.Equal(2, await db.ImageCacheMetadata.CountAsync());
                Assert.True(File.Exists(paths[2])); // Unsafe external bytes never authorize deletion.
                Assert.True(File.Exists(paths[50]));
                Assert.False(File.Exists(paths[0]));
                Assert.False(File.Exists(paths[1]));
            }
        }
        finally
        {
            ProxiedImageCacheService.SetMetadataSaverForTesting(null);
            sizeField.SetValue(null, oldSize);
        }
    }

    /// <summary>Reuses the production failure hook for in-memory and PostgreSQL commit-point evidence.</summary>
    internal static async Task AssertFailurePreservesAsync(ApplicationDbContext db, ImageCacheStorage storage,
        ImageCacheMetadata row, bool publishFailure = false)
    {
        var original = db.Entry(row).CurrentValues.Clone();
        var oldPath = storage.Resolve(row)!;
        var service = Service(db, storage);
        if (publishFailure) ProxiedImageCacheService.SetImageFileReplacerForTesting((_, _) => throw new IOException("publish failed"));
        else ProxiedImageCacheService.SetMetadataSaverForTesting(_ => throw new DbUpdateException("save failed"));
        try { Assert.False((await service.SetAsync(row.CacheKey, [7, 8, 9], "image/png")).Stored); }
        finally
        {
            ProxiedImageCacheService.SetImageFileReplacerForTesting(null);
            ProxiedImageCacheService.SetMetadataSaverForTesting(null);
        }
        Assert.Equal(original.Properties.Select(p => original[p]), db.Entry(row).CurrentValues.Properties.Select(p => db.Entry(row).CurrentValues[p]));
        await db.Entry(row).ReloadAsync();
        Assert.Equal(original.Properties.Select(p => original[p]), db.Entry(row).CurrentValues.Properties.Select(p => db.Entry(row).CurrentValues[p]));
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(oldPath));
        Assert.Equal(oldPath.StartsWith(storage.CurrentRoot, StringComparison.Ordinal) ? 1 : 0,
            Directory.GetFiles(storage.CurrentRoot, row.CacheKey + "*").Length);
    }

    /// <summary>Builds a service without touching host storage configuration.</summary>
    internal static ProxiedImageCacheService Service(ApplicationDbContext db, ImageCacheStorage storage, int maxSize = 512)
    {
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(x => x.GetSettings()).Returns(new ApplicationSettings { MaxCacheImageSizeInMB = maxSize, ImageCacheExpiryDays = 1 });
        return new ProxiedImageCacheService(NullLogger<ProxiedImageCacheService>.Instance, db, settings.Object, storage);
    }

    /// <summary>Seeds real bytes and an exact current, restored native, or unsafe external reference.</summary>
    internal static async Task<ImageCacheMetadata> SeedAsync(ApplicationDbContext db, ImageCacheStorage storage,
        string kind, bool replacement = false, bool stale = false)
    {
        var key = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray()));
        var reference = ImageCacheStorage.CreateReference(key, replacement ? Guid.NewGuid() : null);
        var path = kind switch
        {
            "current" => storage.CurrentPath(reference, key),
            "legacy" => Path.Combine(storage.LegacyRoot, reference),
            _ => Path.Combine(Path.GetDirectoryName(storage.CurrentRoot)!, "outside", reference)
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [1, 2]);
        var row = new ImageCacheMetadata
        {
            CacheKey = key, FilePath = kind == "current" ? reference : path, ContentType = "image/jpeg", Size = 2,
            CreatedAt = DateTime.UtcNow.AddDays(stale ? -2 : 0), LastAccessed = DateTime.UtcNow
        };
        db.ImageCacheMetadata.Add(row);
        await db.SaveChangesAsync();
        await db.Entry(row).ReloadAsync();
        return row;
    }

    /// <summary>Allocates separate current and legacy authorities under this test's owned disposable tree.</summary>
    private ImageCacheStorage Storage()
    {
        var root = CreateTestDirectory();
        return new ImageCacheStorage(Path.Combine(root, "current"), Path.Combine(root, "legacy"));
    }
}
