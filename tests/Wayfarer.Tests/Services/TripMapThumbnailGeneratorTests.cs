using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Moq;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// File-system behaviors for the map thumbnail generator (Playwright-free paths).
/// </summary>
[Collection(PlaywrightEnvironmentTestCollection.Name)]
[PlaywrightEnvironmentIsolation]
public class TripMapThumbnailGeneratorTests : IDisposable
{
    private readonly string _root;
    private readonly Mock<ILogger<TripMapThumbnailGenerator>> _logger = new();
    private readonly TripThumbnailStorage _storage;
    private readonly IConfiguration _config;

    public TripMapThumbnailGeneratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wayfarer-browser-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _storage = new TripThumbnailStorage(TestDirectory.Storage(_root));
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CacheSettings:ChromeCacheDirectory"] = Path.Combine(_root, "browser")
        }).Build();
    }

    [Fact]
    public async Task GetOrGenerateThumbnailAsync_ReturnsNull_WhenCoordinatesInvalid()
    {
        var generator = new TripMapThumbnailGenerator(_logger.Object, _storage, _config);

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
                ["CacheSettings:ChromeCacheDirectory"] = Path.Combine(_root, "browser"),
                ["AllowedHosts"] = "invalid host;wayfarer.example.com;other.example.com",
                ["Kestrel:Endpoints:Http:Url"] = "http://*:5500"
            })
            .Build();
        var generator = new TripMapThumbnailGenerator(_logger.Object, _storage, config);

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
                ["CacheSettings:ChromeCacheDirectory"] = Path.Combine(_root, "browser"),
                ["AllowedHosts"] = "wayfarer.test",
                ["Kestrel:Endpoints:Http:Url"] = "http://*:5500"
            })
            .Build();
        var generator = new TripMapThumbnailGenerator(_logger.Object, _storage, config);

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

    /// <summary>Capture must await escape suppression before taking the screen-media screenshot.</summary>
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
        var styled = new TaskCompletionSource<IElementHandle>();
        page.Setup(item => item.AddStyleTagAsync(It.Is<PageAddStyleTagOptions>(options =>
                options.Content == ".wayfarer-embed-full-view { display: none !important; }")))
            .Returns(styled.Task);
        page.Setup(item => item.ScreenshotAsync(It.Is<PageScreenshotOptions>(options =>
                options.Type == ScreenshotType.Jpeg && options.Quality == 85 && options.FullPage == false)))
            .ReturnsAsync(expected);

        var capture = TripMapThumbnailGenerator.CapturePageAsync(
            page.Object, embedUrl, CancellationToken.None);

        page.Verify(item => item.AddStyleTagAsync(It.IsAny<PageAddStyleTagOptions>()), Times.Once);
        page.Verify(item => item.ScreenshotAsync(It.IsAny<PageScreenshotOptions>()), Times.Never);
        styled.SetResult(Mock.Of<IElementHandle>());
        Assert.Equal(expected, await capture);
        page.Verify(item => item.EmulateMediaAsync(It.IsAny<PageEmulateMediaOptions>()), Times.Never);
    }

    /// <summary>Real screen-media capture hides the production escape while retaining the map and attribution.</summary>
    [Fact]
    [Trait("Category", "RequiresPlaywright")]
    public async Task CapturePageAsync_OmitsMountedEscapeInScreenMedia()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Wayfarer.csproj"))) root = root.Parent!;
        var leaflet = await File.ReadAllTextAsync(Path.Combine(root.FullName, "wwwroot/lib/leaflet/leaflet-1.9.4.js"));
        var styles = await File.ReadAllTextAsync(Path.Combine(root.FullName, "wwwroot/lib/leaflet/leaflet-1.9.4.css"));
        var embed = await File.ReadAllTextAsync(Path.Combine(root.FullName, "wwwroot/js/embeddedMap.js"));
        var html = "<style>body{margin:0}#map{width:800px;height:450px}" + styles + "</style>" +
            "<div id='map'></div><script>" + leaflet + "</script><script type='module'>" + embed +
            ";window.map=L.map('map').setView([10,20],4);installEmbeddedMap(map,'/full');</script>";
        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 800, Height = 450 }
        });
        const string url = "http://wayfarer.example.com/capture";
        await page.RouteAsync(url, route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "text/html", Body = html
        }));
        await page.GotoAsync(url);
        var escape = page.GetByRole(AriaRole.Link, new() { Name = "Open full view", Exact = true });
        await escape.WaitForAsync();
        Assert.True(await escape.IsVisibleAsync());
        var bounds = await page.Locator("#map").BoundingBoxAsync();

        var bytes = await TripMapThumbnailGenerator.CapturePageAsync(page, url, CancellationToken.None);

        Assert.NotEmpty(bytes!);
        Assert.True(await page.EvaluateAsync<bool>("matchMedia('screen').matches"));
        Assert.True(await escape.IsHiddenAsync());
        Assert.True(await page.Locator(".leaflet-control-attribution").IsVisibleAsync());
        Assert.True(await page.Locator(".leaflet-control-zoom").IsVisibleAsync());
        Assert.Equal(bounds!.Width, (await page.Locator("#map").BoundingBoxAsync())!.Width);
        Assert.Equal(bounds.Height, (await page.Locator("#map").BoundingBoxAsync())!.Height);
        Assert.Equal(4, await page.EvaluateAsync<int>("map.getZoom()"));
        Assert.Equal(new[] { 10d, 20d }, await page.EvaluateAsync<double[]>("[map.getCenter().lat,map.getCenter().lng]"));
    }

    [Fact]
    public async Task GetOrGenerateThumbnailAsync_PreservesExistingFile_WhenCaptureFails()
    {
        var tripId = Guid.NewGuid();
        var generator = new TripMapThumbnailGenerator(
            _logger.Object,
            _storage,
            _config,
            _ => Task.FromResult<byte[]?>(null));
        var path = _storage.Resolve($"{tripId}-800x450.jpg");
        var original = new byte[] { 1, 2, 3 };
        File.WriteAllBytes(path, original);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-1));

        var result = await generator.GetOrGenerateThumbnailAsync(
            tripId, 10, 20, 5, 800, 450, DateTime.UtcNow);

        Assert.Null(result);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task GetOrGenerateThumbnailAsync_PreservesExistingFile_WhenAtomicPublishFails()
    {
        var tripId = Guid.NewGuid();
        var replacement = new byte[] { 9, 8, 7 };
        var generator = new TripMapThumbnailGenerator(
            _logger.Object,
            _storage,
            _config,
            _ => Task.FromResult<byte[]?>(replacement));
        var directory = _storage.Root;
        var path = Path.Combine(directory, $"{tripId}-800x450.jpg");
        var original = new byte[] { 1, 2, 3 };
        File.WriteAllBytes(path, original);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-1));
        var originalTimestamp = File.GetLastWriteTimeUtc(path);

        TripMapThumbnailGenerator.SetThumbnailFileReplacerForTesting(
            (_, _) => throw new IOException("publish failed"));
        try
        {
            var result = await generator.GetOrGenerateThumbnailAsync(
                tripId, 10, 20, 5, 800, 450, DateTime.UtcNow);

            Assert.Null(result);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(originalTimestamp, File.GetLastWriteTimeUtc(path));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            Assert.Empty(Directory.GetFiles(directory, "*.bak"));
        }
        finally
        {
            TripMapThumbnailGenerator.SetThumbnailFileReplacerForTesting(null);
        }
    }

    [Fact]
    public async Task CaptureEmbedViewAsync_DisposesCreatedResourcesOnce_WhenCancelledAfterPageCreation()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CacheSettings:ChromeCacheDirectory"] = Path.Combine(_root, "browser"),
                ["AllowedHosts"] = "wayfarer.example.com",
                ["Kestrel:Endpoints:Http:Url"] = "http://*:5500"
            })
            .Build();
        var generator = new TripMapThumbnailGenerator(_logger.Object, _storage, config);
        var playwright = new Mock<IPlaywright>();
        var browserType = new Mock<IBrowserType>();
        var browser = new Mock<IBrowser>();
        var page = new Mock<IPage>();
        using var cancellation = new CancellationTokenSource();
        playwright.SetupGet(item => item.Chromium).Returns(browserType.Object);
        browserType.Setup(item => item.LaunchAsync(It.IsAny<BrowserTypeLaunchOptions>()))
            .ReturnsAsync(browser.Object);
        browser.Setup(item => item.NewPageAsync(It.IsAny<BrowserNewPageOptions>()))
            .ReturnsAsync(() =>
            {
                cancellation.Cancel();
                return page.Object;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generator.CaptureEmbedViewAsync(
            Guid.NewGuid(), 10, 20, 5, 800, 450, cancellation.Token,
            () => Task.FromResult(playwright.Object)));

        page.Verify(item => item.CloseAsync(It.IsAny<PageCloseOptions>()), Times.Once);
        browser.Verify(item => item.CloseAsync(It.IsAny<BrowserCloseOptions>()), Times.Once);
        playwright.Verify(item => item.Dispose(), Times.Once);
    }

    [Fact]
    public void DeleteThumbnails_RemovesFilesForTrip()
    {
        var tripId = Guid.NewGuid();
        var path = _storage.Root;
        Directory.CreateDirectory(path);
        var mine = Path.Combine(path, $"{tripId}-800x450.jpg");
        var other = Path.Combine(path, $"{Guid.NewGuid()}-800x450.jpg");
        File.WriteAllBytes(mine, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(other, new byte[] { 4, 5, 6 });

        var generator = new TripMapThumbnailGenerator(_logger.Object, _storage, _config);

        generator.DeleteThumbnails(tripId);

        Assert.False(File.Exists(mine));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task CleanupOrphanedThumbnails_RemovesNonExistingTrips()
    {
        var keep = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        var path = _storage.Root;
        var generator = new TripMapThumbnailGenerator(_logger.Object, _storage, _config);
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, $"{keep:D}-800x450.jpg"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(path, $"{orphan:D}-800x450.jpg"), new byte[] { 2 });

        File.WriteAllText(Path.Combine(path, "unknown.jpg"), "keep");
        var deleted = await generator.CleanupOrphanedThumbnailsAsync(new HashSet<Guid> { keep });

        Assert.Equal(1, deleted);
        Assert.True(File.Exists(Path.Combine(path, "unknown.jpg")));
        Assert.True(File.Exists(Path.Combine(path, $"{keep:D}-800x450.jpg")));
        Assert.False(File.Exists(Path.Combine(path, $"{orphan:D}-800x450.jpg")));
    }

    [Fact]
    public void InvalidateThumbnails_RemovesTripFiles()
    {
        var tripId = Guid.NewGuid();
        var path = _storage.Root;
        Directory.CreateDirectory(path);
        var file = Path.Combine(path, $"{tripId}-800x450.jpg");
        File.WriteAllBytes(file, new byte[] { 1, 2 });

        var generator = new TripMapThumbnailGenerator(_logger.Object, _storage, _config);

        generator.InvalidateThumbnails(tripId, DateTime.UtcNow);

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void GetLocalBaseUrl_ParsesKestrelHttpUrl_WithValidUri()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CacheSettings:ChromeCacheDirectory"] = Path.Combine(_root, "browser"),
                ["Kestrel:Endpoints:Http:Url"] = "http://localhost:5500"
            })
            .Build();
        var generator = new TripMapThumbnailGenerator(_logger.Object, _storage, config);

        var result = InvokeGetLocalBaseUrl(generator);

        Assert.Equal("http://127.0.0.1:5500", result);
    }

    [Fact]
    public void GetLocalBaseUrl_ParsesKestrelHttpUrl_WithWildcard()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CacheSettings:ChromeCacheDirectory"] = Path.Combine(_root, "browser"),
                ["Kestrel:Endpoints:Http:Url"] = "http://*:8080"
            })
            .Build();
        var generator = new TripMapThumbnailGenerator(_logger.Object, _storage, config);

        var result = InvokeGetLocalBaseUrl(generator);

        Assert.Equal("http://127.0.0.1:8080", result);
    }

    [Fact]
    public void GetLocalBaseUrl_ParsesKestrelHttpUrl_WithPlusSign()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CacheSettings:ChromeCacheDirectory"] = Path.Combine(_root, "browser"),
                ["Kestrel:Endpoints:Http:Url"] = "http://+:3000"
            })
            .Build();
        var generator = new TripMapThumbnailGenerator(_logger.Object, _storage, config);

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
            var generator = new TripMapThumbnailGenerator(_logger.Object, _storage, config);

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
            var generator = new TripMapThumbnailGenerator(_logger.Object, _storage, config);

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
            var generator = new TripMapThumbnailGenerator(_logger.Object, _storage, config);

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
