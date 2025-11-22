using System.Net;
using System.Net.Http;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tile cache behaviors: storing, retrieving, and purging cached tiles.
/// </summary>
public class TileCacheServiceTests : TestBase
{
    [Fact]
    public async Task CacheTileAsync_StoresFileAndMetadata_ForZoomNine()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var service = CreateService(db, dir.Path);

        await service.CacheTileAsync("http://tiles/9/1/2.png", "9", "1", "2");

        var filePath = Path.Combine(dir.Path, "9_1_2.png");
        Assert.True(File.Exists(filePath));
        var meta = Assert.Single(db.TileCacheMetadata);
        Assert.Equal(9, meta.Zoom);
        Assert.Equal(filePath, meta.TileFilePath);
    }

    [Fact]
    public async Task RetrieveTileAsync_UpdatesLastAccessed()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var service = CreateService(db, dir.Path);
        await service.CacheTileAsync("http://tiles/9/3/4.png", "9", "3", "4");
        var meta = db.TileCacheMetadata.Single();
        var old = DateTime.UtcNow.AddMinutes(-10);
        meta.LastAccessed = old;
        db.SaveChanges();

        var bytes = await service.RetrieveTileAsync("9", "3", "4");

        Assert.NotNull(bytes);
        Assert.True(db.TileCacheMetadata.Single().LastAccessed > old);
    }

    [Fact]
    public async Task PurgeAllCacheAsync_RemovesFilesAndMetadata()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var service = CreateService(db, dir.Path);
        await service.CacheTileAsync("http://tiles/9/5/6.png", "9", "5", "6");
        await service.CacheTileAsync("http://tiles/9/7/8.png", "9", "7", "8");
        Assert.True(Directory.GetFiles(dir.Path).Length >= 2);
        Assert.Equal(2, db.TileCacheMetadata.Count());

        await service.PurgeAllCacheAsync();

        Assert.Empty(Directory.GetFiles(dir.Path));
        Assert.Empty(db.TileCacheMetadata);
    }

    [Fact]
    public async Task CacheTileAsync_EvictsLru_WhenCacheOverLimit()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var handler = new SizedTileHandler(600_000); // ~0.57 MB tiles
        var service = CreateService(db, dir.Path, handler, maxCacheMb: 1);

        await service.CacheTileAsync("http://tiles/9/1/1.png", "9", "1", "1"); // fits
        await service.CacheTileAsync("http://tiles/9/1/2.png", "9", "1", "2"); // triggers eviction of oldest

        Assert.Equal(1, db.TileCacheMetadata.Count());
        var meta = db.TileCacheMetadata.Single();
        Assert.Equal(1, meta.X);
        Assert.Equal(2, meta.Y);
    }

    [Fact]
    public async Task RetrieveTileAsync_UpdatesLastAccessed_ForExistingTile()
    {
        using var dir = new TempDir();
        var db = CreateDbContext();
        var service = CreateService(db, dir.Path);
        await service.CacheTileAsync("http://tiles/9/3/4.png", "9", "3", "4");
        var meta = db.TileCacheMetadata.Single();
        meta.LastAccessed = DateTime.UtcNow.AddMinutes(-5);
        db.SaveChanges();
        var old = meta.LastAccessed;

        await Task.Delay(5);
        await service.RetrieveTileAsync("9", "3", "4");

        Assert.True(db.TileCacheMetadata.Single().LastAccessed > old);
    }

    private TileCacheService CreateService(ApplicationDbContext db, string cacheDir, HttpMessageHandler? handler = null, int maxCacheMb = 10)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CacheSettings:TileCacheDirectory"] = cacheDir
            }).Build();
        var httpClient = new HttpClient(handler ?? new StubTileHandler());
        var appSettings = new StubSettingsService(maxCacheMb);
        var scopeFactory = new SingleScopeFactory(db);
        return new TileCacheService(
            NullLogger<TileCacheService>.Instance,
            config,
            httpClient,
            db,
            appSettings,
            scopeFactory);
    }

    private sealed class StubTileHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
            });
        }
    }

    private sealed class SizedTileHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;
        public SizedTileHandler(int sizeBytes) => _payload = Enumerable.Repeat((byte)5, sizeBytes).ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload)
            });
        }
    }

    private sealed class StubSettingsService : IApplicationSettingsService
    {
        private readonly int _maxCache;
        public StubSettingsService(int maxCacheMb = 10) => _maxCache = maxCacheMb;

        public ApplicationSettings GetSettings() => new ApplicationSettings
        {
            Id = 1,
            MaxCacheTileSizeInMB = _maxCache,
            UploadSizeLimitMB = 5,
            IsRegistrationOpen = true
        };

        public string GetUploadsDirectoryPath() => Path.Combine(Path.GetTempPath(), "uploads");
        public void RefreshSettings() { }
    }

    private sealed class SingleScopeFactory : IServiceScopeFactory
    {
        private readonly ApplicationDbContext _db;
        public SingleScopeFactory(ApplicationDbContext db) => _db = db;

        public IServiceScope CreateScope()
        {
            var provider = new ServiceCollection()
                .AddSingleton(_db)
                .AddSingleton<ApplicationDbContext>(_db)
                .BuildServiceProvider();
            return new SimpleScope(provider);
        }

        private sealed class SimpleScope : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; }
            public SimpleScope(IServiceProvider provider) => ServiceProvider = provider;
            public void Dispose() { }
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tiles-{Guid.NewGuid():N}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
