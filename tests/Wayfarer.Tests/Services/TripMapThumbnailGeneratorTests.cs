using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
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
    public void BuildCaptureSettings_UsesAuthorizedHostWithLoopbackResolver()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AllowedHosts"] = "invalid host;wayfarer.example.com;other.example.com",
                ["Kestrel:Endpoints:Http:Url"] = "http://*:5500"
            })
            .Build();
        var generator = new TripMapThumbnailGenerator(_logger.Object, _env.Object, config);

        var settings = generator.BuildCaptureSettings(Guid.Empty, 1, 2, 3);

        Assert.NotNull(settings);
        Assert.StartsWith("http://wayfarer.example.com:5500/Public/Trips/", settings.Value.EmbedUrl);
        Assert.Equal("MAP wayfarer.example.com 127.0.0.1", settings.Value.HostResolverRule);
        Assert.DoesNotContain("other.example.com", settings.Value.EmbedUrl);
    }

    [Fact]
    public void BuildCaptureSettings_ReturnsNull_WhenAllowedHostIsNotPublic()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AllowedHosts"] = "wayfarer.test",
                ["Kestrel:Endpoints:Http:Url"] = "http://*:5500"
            })
            .Build();
        var generator = new TripMapThumbnailGenerator(_logger.Object, _env.Object, config);

        var settings = generator.BuildCaptureSettings(Guid.Empty, 1, 2, 3);

        Assert.Null(settings);
    }

    [Fact]
    public async Task CapturePageAsync_ReturnsNull_WhenNavigationIsNonSuccess()
    {
        var page = new Mock<IPage>();
        var response = new Mock<IResponse>();
        response.SetupGet(item => item.Status).Returns(400);
        response.SetupGet(item => item.Ok).Returns(false);
        page.Setup(item => item.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions>()))
            .ReturnsAsync(response.Object);

        var result = await TripMapThumbnailGenerator.CapturePageAsync(
            page.Object, "http://wayfarer.example.com:5500/Public/Trips/example", CancellationToken.None);

        Assert.Null(result);
        page.Verify(item => item.ScreenshotAsync(It.IsAny<PageScreenshotOptions>()), Times.Never);
    }

    [Fact]
    public async Task CapturePageAsync_ReturnsNull_WhenNavigationResponseIsNull()
    {
        var page = new Mock<IPage>();
        page.Setup(item => item.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions>()))
            .ReturnsAsync((IResponse?)null);

        var result = await TripMapThumbnailGenerator.CapturePageAsync(
            page.Object, "http://wayfarer.example.com:5500/Public/Trips/example", CancellationToken.None);

        Assert.Null(result);
        page.Verify(item => item.ScreenshotAsync(It.IsAny<PageScreenshotOptions>()), Times.Never);
    }

    [Fact]
    public async Task CapturePageAsync_ReturnsNull_WhenNavigationRedirectsToAnotherPage()
    {
        const string embedUrl = "http://wayfarer.example.com:5500/Public/Trips/example";
        var page = new Mock<IPage>();
        var response = new Mock<IResponse>();
        response.SetupGet(item => item.Ok).Returns(true);
        response.SetupGet(item => item.Url).Returns("http://wayfarer.example.com:5500/Identity/Account/Login");
        page.Setup(item => item.GotoAsync(embedUrl, It.IsAny<PageGotoOptions>()))
            .ReturnsAsync(response.Object);

        var result = await TripMapThumbnailGenerator.CapturePageAsync(
            page.Object, embedUrl, CancellationToken.None);

        Assert.Null(result);
        page.Verify(item => item.ScreenshotAsync(It.IsAny<PageScreenshotOptions>()), Times.Never);
    }

    [Fact]
    public async Task CapturePageAsync_ScreenshotsSuccessfulEmbedResponse()
    {
        const string embedUrl = "http://wayfarer.example.com:5500/Public/Trips/example";
        var expected = new byte[] { 1, 2, 3 };
        var page = new Mock<IPage>();
        var response = new Mock<IResponse>();
        response.SetupGet(item => item.Ok).Returns(true);
        response.SetupGet(item => item.Url).Returns(embedUrl);
        page.Setup(item => item.GotoAsync(embedUrl, It.IsAny<PageGotoOptions>()))
            .ReturnsAsync(response.Object);
        page.Setup(item => item.ScreenshotAsync(It.IsAny<PageScreenshotOptions>()))
            .ReturnsAsync(expected);

        var result = await TripMapThumbnailGenerator.CapturePageAsync(
            page.Object, embedUrl, CancellationToken.None);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task GetOrGenerateThumbnailAsync_PreservesExistingFile_WhenCaptureFails()
    {
        var tripId = Guid.NewGuid();
        var generator = new TripMapThumbnailGenerator(
            _logger.Object,
            _env.Object,
            _config,
            _ => Task.FromResult<byte[]?>(null));
        var path = Path.Combine(_root, "thumbs", "trips", $"{tripId}-800x450.jpg");
        var original = new byte[] { 1, 2, 3 };
        File.WriteAllBytes(path, original);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-1));

        var result = await generator.GetOrGenerateThumbnailAsync(
            tripId, 10, 20, 5, 800, 450, DateTime.UtcNow);

        Assert.Null(result);
        Assert.Equal(original, File.ReadAllBytes(path));
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
