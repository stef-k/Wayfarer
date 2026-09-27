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
        }).Build();
    }

    /// <summary>Successful generation reports dimensions and trip identity without the persisted path.</summary>
    [Fact]
    public async Task GeneratedThumbnail_DiagnosticsOmitAbsolutePath()
    {
        using var logs = new TestLogProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var generator = new TripMapThumbnailGenerator(factory.CreateLogger<TripMapThumbnailGenerator>(), _storage, _config,
            _ => Task.FromResult<byte[]?>([1, 2, 3]));
        var tripId = Guid.NewGuid();
        Assert.NotNull(await generator.GetOrGenerateThumbnailAsync(tripId, 11.663, 22.663, 5, 800, 450, DateTime.UtcNow));
        var entry = Assert.Single(logs.Entries, e => e.Message.StartsWith("Generated thumbnail"));
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(_root, entry.Message + string.Join(",", entry.Fields.Values));
        Assert.Equal(tripId, entry.Fields["TripId"]);
    }

    /// <summary>Routine removals retain trip identity without exposing external storage paths.</summary>
    [Theory]
    [InlineData("delete")]
    [InlineData("invalidate")]
    [InlineData("orphan")]
    public async Task ThumbnailRemoval_DiagnosticsOmitStorageRoot(string operation)
    {
        using var logs = new TestLogProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var generator = new TripMapThumbnailGenerator(factory.CreateLogger<TripMapThumbnailGenerator>(), _storage, _config);
        var tripId = Guid.NewGuid();
        var path = _storage.Resolve($"{tripId}-800x450.jpg");
        await File.WriteAllBytesAsync(path, [1]);

        // Storage initialization diagnostics are outside the routine removal boundary.
        var initialLogCount = logs.Entries.Count;
        switch (operation)
        {
            case "delete":
                generator.DeleteThumbnails(tripId);
                break;
            case "invalidate":
                generator.InvalidateThumbnails(tripId, DateTime.UtcNow);
                break;
            case "orphan":
                Assert.Equal(1, await generator.CleanupOrphanedThumbnailsAsync(new HashSet<Guid>()));
                break;
        }

        Assert.False(File.Exists(path));
        var entry = Assert.Single(logs.Entries.Skip(initialLogCount));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(_root, entry.Message + string.Join(",", entry.Fields.Values));
        Assert.Equal(tripId, entry.Fields["TripId"]);
    }

    /// <summary>Invalid geographic input is diagnosed by trip identity, never by the supplied coordinate.</summary>
    [Fact]
    public async Task InvalidCoordinates_DiagnosticsOmitPrivateValues()
    {
        using var logs = new TestLogProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var generator = new TripMapThumbnailGenerator(factory.CreateLogger<TripMapThumbnailGenerator>(), _storage, _config);
        var tripId = Guid.NewGuid();
        Assert.Null(await generator.GetOrGenerateThumbnailAsync(tripId, 211.663, 22.663, 5, 800, 450, DateTime.UtcNow));
        var entry = Assert.Single(logs.Entries, e => e.Message.StartsWith("Invalid coordinates"));
        Assert.Null(entry.Exception);
        Assert.DoesNotContain("211.663", entry.Message + string.Join(",", entry.Fields.Values));
        Assert.DoesNotContain("22.663", entry.Message + string.Join(",", entry.Fields.Values));
        Assert.Equal(tripId, entry.Fields["TripId"]);
    }

    [Fact]
    public async Task GetOrGenerateThumbnailAsync_ReturnsNull_WhenCoordinatesInvalid()
    {
        var generator = new TripMapThumbnailGenerator(_logger.Object, _storage, _config);

        var result = await generator.GetOrGenerateThumbnailAsync(
            Guid.NewGuid(), 200, 10, 5, 800, 450, DateTime.UtcNow);

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
        page.SetupGet(item => item.Url).Returns(embedUrl);
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
        var context = new Mock<IBrowserContext>();
        browser.Setup(item => item.NewContextAsync(It.IsAny<BrowserNewContextOptions>())).ReturnsAsync(context.Object);
        context.Setup(item => item.NewPageAsync())
            .ReturnsAsync(() =>
            {
                cancellation.Cancel();
                return page.Object;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generator.CaptureEmbedViewAsync(
            Guid.NewGuid(), 10, 20, 5, 800, 450, cancellation.Token,
            () => Task.FromResult(playwright.Object)));

        context.Verify(item => item.CloseAsync(It.IsAny<BrowserContextCloseOptions>()), Times.Once);
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
        var unknown = Path.Combine(path, $"{tripId}-not-generated.jpg");
        File.WriteAllText(unknown, "keep");
        File.WriteAllBytes(mine, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(other, new byte[] { 4, 5, 6 });

        var generator = new TripMapThumbnailGenerator(_logger.Object, _storage, _config);

        generator.DeleteThumbnails(tripId);

        Assert.False(File.Exists(mine));
        Assert.True(File.Exists(unknown));
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

    /// <summary>Fresh files bypass full admission; stale work is rejected without capture or replacement.</summary>
    [Fact]
    public async Task CacheHitBypassesAdmissionAndSaturatedMissPreservesStaleFile()
    {
        var calls = 0;
        var generator = new TripMapThumbnailGenerator(_logger.Object, _storage, _config,
            _ => { calls++; return Task.FromResult<byte[]?>([9]); });
        var id = Guid.NewGuid();
        var updated = DateTime.UtcNow.AddMinutes(-1);
        var path = _storage.Resolve($"{id}-800x450.jpg");
        await File.WriteAllBytesAsync(path, [1, 2]);
        File.SetLastWriteTimeUtc(path, updated.AddSeconds(1));
        using var first = BrowserAdmission.Shared.TryAcquire();
        using var second = BrowserAdmission.Shared.TryAcquire();
        Assert.NotNull(await generator.GetOrGenerateThumbnailAsync(id, 1, 2, 3, 800, 450, updated));
        Assert.Null(await generator.GetOrGenerateThumbnailAsync(id, 1, 2, 3, 800, 450, updated.AddMinutes(2)));
        Assert.Equal(0, calls);
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(path));
    }

    /// <summary>Unsupported dimensions cannot create cache files even for direct internal callers.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(800, 451)]
    [InlineData(99999, 99999)]
    public async Task InvalidDimensionsNeverCaptureOrPublish(int width, int height)
    {
        var generator = new TripMapThumbnailGenerator(_logger.Object, _storage, _config,
            _ => throw new InvalidOperationException("Must not capture"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => generator.GetOrGenerateThumbnailAsync(
            Guid.NewGuid(), 1, 2, 3, width, height, DateTime.UtcNow));
        Assert.Empty(Directory.GetFiles(_storage.Root));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
