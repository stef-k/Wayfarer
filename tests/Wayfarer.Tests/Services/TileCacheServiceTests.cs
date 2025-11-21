using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Wayfarer.Parsers;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tile cache service disk hits/misses without touching production paths.
/// </summary>
public class TileCacheServiceTests : TestBase
{
    [Fact]
    public async Task RetrieveTileAsync_ReturnsFromDisk_AndUpdatesMetadata()
    {
        var cacheDir = CreateTempDir();
        var db = CreateDbContext();
        var appSettings = BuildAppSettings();
        var filePath = Path.Combine(cacheDir, "10_2_3.png");
        await File.WriteAllBytesAsync(filePath, new byte[] { 1, 2, 3 });
        var old = DateTime.UtcNow.AddDays(-1);
        db.TileCacheMetadata.Add(new TileCacheMetadata
        {
            Zoom = 10,
            X = 2,
            Y = 3,
            TileFilePath = filePath,
            Size = 3,
            TileLocation = new Point(0, 0) { SRID = 4326 },
            LastAccessed = old
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, cacheDir, appSettings.Object);

        var bytes = await service.RetrieveTileAsync("10", "2", "3");

        try
        {
            Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
            var meta = db.TileCacheMetadata.Single();
            Assert.True(meta.LastAccessed > old);
        }
        finally
        {
            Directory.Delete(cacheDir, true);
        }
    }

    [Fact]
    public async Task RetrieveTileAsync_ReturnsNull_WhenMissingAndNoUrl()
    {
        var cacheDir = CreateTempDir();
        var db = CreateDbContext();
        var appSettings = BuildAppSettings();
        var service = CreateService(db, cacheDir, appSettings.Object);

        var bytes = await service.RetrieveTileAsync("5", "1", "1");

        try
        {
            Assert.Null(bytes);
        }
        finally
        {
            Directory.Delete(cacheDir, true);
        }
    }

    private static Mock<IApplicationSettingsService> BuildAppSettings()
    {
        var appSettings = new Mock<IApplicationSettingsService>();
        appSettings.Setup(s => s.GetSettings()).Returns(new ApplicationSettings
        {
            MaxCacheTileSizeInMB = 128
        });
        return appSettings;
    }

    private TileCacheService CreateService(ApplicationDbContext db, string cacheDir, IApplicationSettingsService appSettings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CacheSettings:TileCacheDirectory"] = cacheDir
            })
            .Build();

        var handler = new FakeHandler();
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://example.com")
        };

        return new TileCacheService(
            NullLogger<TileCacheService>.Instance,
            config,
            httpClient,
            db,
            appSettings,
            Mock.Of<IServiceScopeFactory>());
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 9, 9 })
            });
        }
    }
}
