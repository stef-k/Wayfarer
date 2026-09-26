using Wayfarer.Tests.Infrastructure;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Bounds legacy disk reads and preserves the cache commit point on cancelled publication.</summary>
public partial class ProxiedImageCacheServiceTests
{
    /// <summary>Actual file size, rather than a forged small metadata size, limits cache allocation.</summary>
    [Fact]
    public async Task GetAsync_RejectsOversizedActualLegacyFile()
    {
        var db = CreateDbContext();
        var service = CreateService(db: db);
        var key = Key("oversized-legacy");
        await service.SetAsync(key, ImageProxyTestFactory.Raster(), "image/jpeg");
        var path = (await service.GetAsync(key)).FilePath!;
        await using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
            file.SetLength(51L * 1024 * 1024);
        Assert.True(db.ImageCacheMetadata.Single().Size < 1024);
        Assert.False((await service.GetAsync(key)).HasBytes);
        Assert.Single(db.ImageCacheMetadata);
    }

    /// <summary>Cancelling metadata publication leaves the previous generation and removes uncommitted bytes.</summary>
    [Fact]
    public async Task SetAsync_CancelledPublicationPreservesPreviousGeneration()
    {
        var db = CreateDbContext();
        var service = CreateService(db: db);
        var key = Key("cancelled-publication");
        var original = ImageProxyTestFactory.Raster();
        await service.SetAsync(key, original, "image/jpeg");
        var originalPath = db.ImageCacheMetadata.Single().FilePath;
        using var cancellation = new CancellationTokenSource();
        ProxiedImageCacheService.SetMetadataSaverForTesting(_ =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<int>(cancellation.Token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SetAsync(key, ImageProxyTestFactory.Raster("png"), "image/png", cancellation.Token));
        ProxiedImageCacheService.SetMetadataSaverForTesting(null);
        Assert.Equal(originalPath, db.ImageCacheMetadata.Single().FilePath);
        Assert.Equal(original, (await service.GetAsync(key)).Bytes);
        Assert.Single(Directory.GetFiles(_tempDir));
    }
}
