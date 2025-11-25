using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// File-system behaviors for the map thumbnail generator (Playwright-free paths).
/// </summary>
public class TripMapThumbnailGeneratorTests : IDisposable
{
    private readonly string _root;
    private readonly Mock<ILogger<TripMapThumbnailGenerator>> _logger = new();
    private readonly Mock<IWebHostEnvironment> _env = new();
    private readonly IConfiguration _config;

    public TripMapThumbnailGeneratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _env.SetupGet(e => e.WebRootPath).Returns(_root);
        _config = new ConfigurationBuilder().Build();
    }

    [Fact]
    public async Task GetOrGenerateThumbnailAsync_ReturnsNull_WhenCoordinatesInvalid()
    {
        var generator = new TripMapThumbnailGenerator(_logger.Object, _env.Object, _config);

        var result = await generator.GetOrGenerateThumbnailAsync(
            Guid.NewGuid(), 200, 10, 5, 200, 200, DateTime.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void DeleteThumbnails_RemovesFilesForTrip()
    {
        var tripId = Guid.NewGuid();
        var path = Path.Combine(_root, "thumbs", "trips");
        Directory.CreateDirectory(path);
        var mine = Path.Combine(path, $"{tripId}-800x450.jpg");
        var other = Path.Combine(path, $"{Guid.NewGuid()}-800x450.jpg");
        File.WriteAllBytes(mine, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(other, new byte[] { 4, 5, 6 });

        var generator = new TripMapThumbnailGenerator(_logger.Object, _env.Object, _config);

        generator.DeleteThumbnails(tripId);

        Assert.False(File.Exists(mine));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task CleanupOrphanedThumbnails_RemovesNonExistingTrips()
    {
        var keep = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        var path = Path.Combine(_root, "thumbs", "trips");
        var generator = new TripMapThumbnailGenerator(_logger.Object, _env.Object, _config);
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, $"{keep:N}-800x450.jpg"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(path, $"{orphan:N}-800x450.jpg"), new byte[] { 2 });

        var deleted = await generator.CleanupOrphanedThumbnailsAsync(new HashSet<Guid> { keep });

        Assert.Equal(1, deleted);
        Assert.True(File.Exists(Path.Combine(path, $"{keep:N}-800x450.jpg")));
        Assert.False(File.Exists(Path.Combine(path, $"{orphan:N}-800x450.jpg")));
    }

    [Fact]
    public void InvalidateThumbnails_RemovesTripFiles()
    {
        var tripId = Guid.NewGuid();
        var path = Path.Combine(_root, "thumbs", "trips");
        Directory.CreateDirectory(path);
        var file = Path.Combine(path, $"{tripId}-800x450.jpg");
        File.WriteAllBytes(file, new byte[] { 1, 2 });

        var generator = new TripMapThumbnailGenerator(_logger.Object, _env.Object, _config);

        generator.InvalidateThumbnails(tripId, DateTime.UtcNow);

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void GetLocalBaseUrl_ParsesKestrelHttpUrl_WithValidUri()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:Http:Url"] = "http://localhost:5500"
            })
            .Build();
        var generator = new TripMapThumbnailGenerator(_logger.Object, _env.Object, config);

        var result = InvokeGetLocalBaseUrl(generator);

        Assert.Equal("http://127.0.0.1:5500", result);
    }

    [Fact]
    public void GetLocalBaseUrl_ParsesKestrelHttpUrl_WithWildcard()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:Http:Url"] = "http://*:8080"
            })
            .Build();
        var generator = new TripMapThumbnailGenerator(_logger.Object, _env.Object, config);

        var result = InvokeGetLocalBaseUrl(generator);

        Assert.Equal("http://127.0.0.1:8080", result);
    }

    [Fact]
    public void GetLocalBaseUrl_ParsesKestrelHttpUrl_WithPlusSign()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:Http:Url"] = "http://+:3000"
            })
            .Build();
        var generator = new TripMapThumbnailGenerator(_logger.Object, _env.Object, config);

        var result = InvokeGetLocalBaseUrl(generator);

        Assert.Equal("http://127.0.0.1:3000", result);
    }

    [Fact]
    public void GetLocalBaseUrl_UsesAspNetCoreUrls_WhenKestrelNotSet()
    {
        var originalEnvVar = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://localhost:7000;https://localhost:7001");
            var config = new ConfigurationBuilder().Build();
            var generator = new TripMapThumbnailGenerator(_logger.Object, _env.Object, config);

            var result = InvokeGetLocalBaseUrl(generator);

            Assert.Equal("http://127.0.0.1:7000", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", originalEnvVar);
        }
    }

    [Fact]
    public void GetLocalBaseUrl_ReturnsFallback_WhenNoConfigFound()
    {
        var originalEnvVar = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", null);
            var config = new ConfigurationBuilder().Build();
            var generator = new TripMapThumbnailGenerator(_logger.Object, _env.Object, config);

            var result = InvokeGetLocalBaseUrl(generator);

            Assert.Equal("http://127.0.0.1:5000", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", originalEnvVar);
        }
    }

    [Fact]
    public void GetLocalBaseUrl_SkipsHttpsUrls_InAspNetCoreUrls()
    {
        var originalEnvVar = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "https://localhost:7001;http://localhost:6000");
            var config = new ConfigurationBuilder().Build();
            var generator = new TripMapThumbnailGenerator(_logger.Object, _env.Object, config);

            var result = InvokeGetLocalBaseUrl(generator);

            Assert.Equal("http://127.0.0.1:6000", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", originalEnvVar);
        }
    }

    private static string InvokeGetLocalBaseUrl(TripMapThumbnailGenerator generator)
    {
        var method = typeof(TripMapThumbnailGenerator).GetMethod("GetLocalBaseUrl",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (string)method!.Invoke(generator, null)!;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
