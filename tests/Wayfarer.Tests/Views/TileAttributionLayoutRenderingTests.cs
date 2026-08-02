using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using MvcFrontendKit.Extensions;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Services;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Views;

/// <summary>
/// Verifies that the compiled shared layout serializes only final provider-safe attribution.
/// </summary>
[Collection(PlaywrightEnvironmentTestCollection.Name)]
[PlaywrightEnvironmentIsolation]
public sealed class TileAttributionLayoutRenderingTests
{
    /// <summary>Verifies every process-wide Playwright environment user is serialized.</summary>
    [Fact]
    public void PlaywrightEnvironmentUsers_ShareTheNonParallelCollection()
    {
        var affectedTypes = new[]
        {
            typeof(TripExportServiceTests),
            typeof(TripMapThumbnailGeneratorTests),
            typeof(TileAttributionLayoutRenderingTests)
        };

        Assert.All(affectedTypes, type =>
            Assert.Equal(
                PlaywrightEnvironmentTestCollection.Name,
                type.GetCustomAttributesData()
                    .Where(attribute => attribute.AttributeType == typeof(CollectionAttribute))
                    .Single()
                    .ConstructorArguments
                    .Single()
                    .Value));

        var definition = typeof(PlaywrightEnvironmentTestCollection)
            .GetCustomAttributes(typeof(CollectionDefinitionAttribute), inherit: false)
            .Cast<CollectionDefinitionAttribute>()
            .Single();
        Assert.True(definition.DisableParallelization);
    }

    /// <summary>Verifies the isolation hook restores set and unset browser-path states.</summary>
    [Fact]
    public void EnvironmentIsolation_RestoresExactOriginalBrowserPath()
    {
        var previousPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        try
        {
            AssertEnvironmentRestoration("issue-415-original-path");
            AssertEnvironmentRestoration(null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", previousPath);
        }
    }

    /// <summary>Exercises the same guaranteed after-test hook used when an assertion fails.</summary>
    private static void AssertEnvironmentRestoration(string? originalPath)
    {
        var isolation = new PlaywrightEnvironmentIsolationAttribute();
        var testMethod = typeof(TileAttributionLayoutRenderingTests)
            .GetMethod(nameof(EnvironmentIsolation_RestoresExactOriginalBrowserPath))!;
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", originalPath);
        isolation.Before(testMethod);
        try
        {
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", "mutated-by-production-service");
            throw new InvalidOperationException("Simulated test failure.");
        }
        catch (InvalidOperationException)
        {
            // xUnit invokes the after-test hook while unwinding a failed test.
        }
        finally
        {
            isolation.After(testMethod);
        }

        Assert.Equal(originalPath, Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH"));
    }

    [Fact]
    public async Task LayoutReflectsProviderChangesAndSanitizesStoredAttributionOnEveryRender()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), $"wayfarer-attribution-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(webRoot);
        await File.WriteAllTextAsync(Path.Combine(webRoot, "frontend.manifest.json"), "{}");
        try
        {
            var settingsService = new MutableApplicationSettingsService();
            using var host = BuildRazorHost(webRoot, settingsService);
            using var scope = host.Services.CreateScope();

            settingsService.Settings = new ApplicationSettings
            {
                TileProviderKey = ApplicationSettings.DefaultTileProviderKey,
                TileProviderAttribution =
                    "<script>alert(1)</script>&copy; openstreetmap contributors"
                    + "<a href=\"javascript:alert(2)\" onclick=\"evil()\">Unsafe</a>"
            };
            var osmHtml = await RenderViewerAsync(scope.ServiceProvider);
            var osmAttribution = ExtractAttribution(osmHtml);

            Assert.Contains("https://www.openstreetmap.org/copyright", osmAttribution);
            Assert.Contains(">OpenStreetMap</a> contributors", osmAttribution);
            Assert.DoesNotContain("script", osmAttribution, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("onclick", osmAttribution, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("javascript:", osmAttribution, StringComparison.OrdinalIgnoreCase);

            settingsService.Settings = new ApplicationSettings
            {
                TileProviderKey = TileProviderCatalog.CustomProviderKey,
                TileProviderAttribution =
                    "&copy; <a href=\"https://tiles.example.com/terms\">Example Maps</a>",
                TileProviderApiKey = "phase-2b-secret-must-not-render"
            };
            var customHtml = await RenderViewerAsync(scope.ServiceProvider);
            var customAttribution = ExtractAttribution(customHtml);

            Assert.Contains("https://tiles.example.com/terms", customAttribution);
            Assert.Contains("Example Maps", customAttribution);
            Assert.DoesNotContain("OpenStreetMap", customAttribution, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("phase-2b-secret-must-not-render", customHtml);
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    /// <summary>Proves compiled viewer and print output using real package-compatible Chromium.</summary>
    [Fact]
    [Trait("Category", "RequiresPlaywright")]
    public async Task SnapshotBackedViewerAndPrintOutputRenderResolvedProviderAttribution()
    {
        var browserPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        Assert.False(string.IsNullOrWhiteSpace(browserPath));
        Assert.True(Path.IsPathFullyQualified(browserPath));

        var webRoot = Path.Combine(Path.GetTempPath(), $"wayfarer-attribution-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(webRoot);
        await File.WriteAllTextAsync(Path.Combine(webRoot, "frontend.manifest.json"), "{}");
        try
        {
            var settingsService = new MutableApplicationSettingsService();
            using var host = BuildRazorHost(webRoot, settingsService);
            using var scope = host.Services.CreateScope();

            settingsService.Settings = new ApplicationSettings
            {
                TileProviderKey = ApplicationSettings.DefaultTileProviderKey,
                TileProviderAttribution = ApplicationSettings.DefaultTileProviderAttribution
            };
            AssertSnapshotAttribution(
                await RenderViewerAsync(scope.ServiceProvider),
                TileProviderAttribution.OpenStreetMapCopyrightUrl,
                "OpenStreetMap");
            var osmPrintHtml = await RenderPrintAsync(scope.ServiceProvider);
            AssertSnapshotAttribution(
                osmPrintHtml,
                TileProviderAttribution.OpenStreetMapCopyrightUrl,
                "OpenStreetMap");
            await AssertActualPdfArtifactAsync(osmPrintHtml);

            settingsService.Settings = new ApplicationSettings
            {
                TileProviderKey = TileProviderCatalog.CustomProviderKey,
                TileProviderAttribution =
                    "&copy; <a href=\"https://tiles.example.com/terms\">Example Maps</a>"
            };
            AssertSnapshotAttribution(
                await RenderViewerAsync(scope.ServiceProvider),
                "https://tiles.example.com/terms",
                "Example Maps");
            AssertSnapshotAttribution(
                await RenderPrintAsync(scope.ServiceProvider),
                "https://tiles.example.com/terms",
                "Example Maps");

            settingsService.Settings = new ApplicationSettings
            {
                TileProviderKey = TileProviderCatalog.CustomProviderKey,
                TileProviderAttribution =
                    "<a href=\"https://tiles.example.com/terms\">Example Maps using OpenStreetMap data</a>"
            };
            AssertSnapshotAttributionLinks(
                await RenderViewerAsync(scope.ServiceProvider),
                "https://tiles.example.com/terms",
                TileProviderAttribution.OpenStreetMapCopyrightUrl);
            AssertSnapshotAttributionLinks(
                await RenderPrintAsync(scope.ServiceProvider),
                "https://tiles.example.com/terms",
                TileProviderAttribution.OpenStreetMapCopyrightUrl);

            settingsService.Settings = new ApplicationSettings
            {
                TileProviderKey = TileProviderCatalog.CustomProviderKey,
                TileProviderAttribution = string.Empty
            };
            AssertSnapshotAttributionIsEmpty(await RenderViewerAsync(scope.ServiceProvider));
            AssertSnapshotAttributionIsEmpty(await RenderPrintAsync(scope.ServiceProvider));
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    /// <summary>
    /// Creates an isolated compiled-Razor host without requiring production frontend assets.
    /// </summary>
    private static IHost BuildRazorHost(string webRoot, IApplicationSettingsService settingsService) =>
        Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webHost => webHost
                .UseContentRoot(Directory.GetCurrentDirectory())
                .UseWebRoot(webRoot)
                .UseSetting(WebHostDefaults.ApplicationKey, typeof(Trip).Assembly.GetName().Name)
                .ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddHttpContextAccessor();
                    services.AddControllersWithViews().AddApplicationPart(typeof(Trip).Assembly);
                    services.AddMvcFrontendKit();
                    services.AddSingleton<IAppVersionProvider, AppVersionProvider>();
                    services.AddSingleton(settingsService);
                    services.AddSingleton<ITempDataProvider, EmptyTempDataProvider>();
                })
                .Configure(_ => { }))
            .Build();

    /// <summary>
    /// Renders the canonical Viewer through the production shared layout.
    /// </summary>
    private static async Task<string> RenderViewerAsync(IServiceProvider services)
    {
        var httpContext = new DefaultHttpContext { RequestServices = services };
        var routeData = new RouteData();
        routeData.Routers.Add(new RouteCollection());
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());
        var viewResult = services.GetRequiredService<ICompositeViewEngine>()
            .GetView(null, "/Views/Trip/Viewer.cshtml", isMainPage: true);
        Assert.True(viewResult.Success, string.Join(Environment.NewLine, viewResult.SearchedLocations ?? []));
        var view = Assert.IsAssignableFrom<IView>(viewResult.View);
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            UserId = "owner",
            Name = "Attribution fixture",
            UpdatedAt = DateTime.UtcNow,
            CenterLat = 40,
            CenterLon = 25
        };
        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = trip,
            ["IsEmbed"] = true,
            ["IsOwner"] = false,
            ["ShareProgressEnabled"] = false,
            ["PlaceVisitCounts"] = new Dictionary<Guid, int>(),
            ["VisitEvents"] = new List<PlaceVisitEvent>()
        };
        var tempData = new TempDataDictionary(httpContext, services.GetRequiredService<ITempDataProvider>());
        await using var writer = new StringWriter();
        var viewContext = new ViewContext(actionContext, view, viewData, tempData, writer, new HtmlHelperOptions());
        await view.RenderAsync(viewContext);
        return writer.ToString();
    }

    /// <summary>
    /// Renders the production PDF template with a real snapshot-backed map slot.
    /// </summary>
    private static async Task<string> RenderPrintAsync(IServiceProvider services)
    {
        var httpContext = new DefaultHttpContext { RequestServices = services };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var viewResult = services.GetRequiredService<ICompositeViewEngine>()
            .GetView(null, "/Views/Trip/Print.cshtml", isMainPage: true);
        Assert.True(viewResult.Success, string.Join(Environment.NewLine, viewResult.SearchedLocations ?? []));
        var view = Assert.IsAssignableFrom<IView>(viewResult.View);
        const string mapFixture = """
            <svg xmlns="http://www.w3.org/2000/svg" width="800" height="450">
              <rect width="800" height="450" fill="#dbeff5"/>
              <path d="M0 90 L800 350 M80 450 L420 0 M0 310 L800 120"
                    stroke="#ffffff" stroke-width="28" fill="none"/>
              <path d="M90 360 C250 80 520 390 710 100"
                    stroke="#2878b5" stroke-width="10" fill="none"/>
              <circle cx="90" cy="360" r="16" fill="#d33"/>
              <circle cx="710" cy="100" r="16" fill="#d33"/>
              <text x="24" y="40" font-family="Arial" font-size="24">Local map snapshot fixture</text>
            </svg>
            """;
        var model = new TripPrintViewModel
        {
            Trip = new Trip
            {
                Id = Guid.NewGuid(),
                UserId = "owner",
                User = new ApplicationUser { DisplayName = "Fixture owner" },
                Name = "Attribution PDF fixture",
                UpdatedAt = DateTime.UtcNow
            },
            Snap = new Dictionary<string, string>
            {
                // The local fixture proves map-image output without requesting provider tiles.
                ["trip"] = "data:image/svg+xml;base64," + Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(mapFixture))
            }
        };
        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = model
        };
        var tempData = new TempDataDictionary(httpContext, services.GetRequiredService<ITempDataProvider>());
        await using var writer = new StringWriter();
        var viewContext = new ViewContext(
            actionContext,
            view,
            viewData,
            tempData,
            writer,
            new HtmlHelperOptions());
        await view.RenderAsync(viewContext);
        return writer.ToString();
    }

    /// <summary>
    /// Verifies a snapshot caption retains the expected provider link and visible text.
    /// </summary>
    private static void AssertSnapshotAttribution(string html, string href, string visibleText)
    {
        var document = new HtmlParser().ParseDocument(html);
        var captions = document.QuerySelectorAll(".map-snapshot-attribution");
        Assert.NotEmpty(captions);
        Assert.All(captions, caption =>
        {
            Assert.Contains(visibleText, caption.TextContent);
            Assert.Equal(href, caption.QuerySelector("a")?.GetAttribute("href"));
        });
    }

    /// <summary>
    /// Verifies an explicitly blank provider attribution produces no snapshot caption.
    /// </summary>
    private static void AssertSnapshotAttributionIsEmpty(string html)
    {
        var document = new HtmlParser().ParseDocument(html);
        Assert.Empty(document.QuerySelectorAll(".map-snapshot-attribution"));
    }

    /// <summary>
    /// Verifies every snapshot caption preserves both provider and OSM destinations.
    /// </summary>
    private static void AssertSnapshotAttributionLinks(
        string html,
        string providerHref,
        string osmHref)
    {
        var document = new HtmlParser().ParseDocument(html);
        var captions = document.QuerySelectorAll(".map-snapshot-attribution");
        Assert.NotEmpty(captions);
        Assert.All(captions, caption =>
        {
            var hrefs = caption.QuerySelectorAll("a")
                .Select(link => link.GetAttribute("href"))
                .ToArray();
            Assert.Contains(providerHref, hrefs);
            Assert.Contains(osmHref, hrefs);
        });
    }

    /// <summary>
    /// Generates the production print HTML as a real Chromium PDF and verifies its linked caption.
    /// </summary>
    private static async Task AssertActualPdfArtifactAsync(string html)
    {
        var configuredArtifactDirectory =
            Environment.GetEnvironmentVariable("WAYFARER_TEST_ARTIFACT_DIRECTORY");
        var artifactDirectory = configuredArtifactDirectory ??
            Path.Combine(Path.GetTempPath(), $"wayfarer-attribution-pdf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactDirectory);
        var pdfPath = Path.Combine(artifactDirectory, "phase2b-map-attribution.pdf");
        var screenshotPath = Path.Combine(artifactDirectory, "phase2b-map-attribution-print.png");
        try
        {
            using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();
            await page.SetContentAsync(
                html,
                new PageSetContentOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

            var caption = page.Locator(".map-snapshot-attribution").First;
            Assert.True(await caption.IsVisibleAsync());
            Assert.Equal(
                TileProviderAttribution.OpenStreetMapCopyrightUrl,
                await caption.Locator("a").GetAttributeAsync("href"));
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = screenshotPath,
                FullPage = true
            });
            await page.PdfAsync(new PagePdfOptions { Path = pdfPath, PrintBackground = true });

            var pdfBytes = await File.ReadAllBytesAsync(pdfPath);
            Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdfBytes, 0, 4));
            Assert.Contains(
                TileProviderAttribution.OpenStreetMapCopyrightUrl,
                Encoding.Latin1.GetString(pdfBytes));
        }
        finally
        {
            if (configuredArtifactDirectory == null)
            {
                Directory.Delete(artifactDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Reads the serialized configuration emitted by the compiled layout.
    /// </summary>
    private static string ExtractAttribution(string html)
    {
        var document = new HtmlParser().ParseDocument(html);
        var script = document.Scripts.Single(element =>
            element.TextContent.Contains("window.wayfarerTileConfig", StringComparison.Ordinal));
        var match = Regex.Match(
            script.TextContent,
            @"window\.wayfarerTileConfig\s*=\s*(\{.+\});",
            RegexOptions.Singleline);
        Assert.True(match.Success, "The layout should emit serialized shared tile configuration.");
        using var json = JsonDocument.Parse(match.Groups[1].Value);
        return json.RootElement.GetProperty("attribution").GetString() ?? string.Empty;
    }

    private sealed class MutableApplicationSettingsService : IApplicationSettingsService
    {
        /// <summary>Gets or sets the settings returned to the shared layout.</summary>
        public ApplicationSettings Settings { get; set; } = new();

        /// <inheritdoc />
        public ApplicationSettings GetSettings() => Settings;

        /// <inheritdoc />
        public string GetUploadsDirectoryPath() => Path.GetTempPath();

        /// <inheritdoc />
        public void RefreshSettings()
        {
        }
    }

    private sealed class EmptyTempDataProvider : ITempDataProvider
    {
        /// <inheritdoc />
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        /// <inheritdoc />
        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
