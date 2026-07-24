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
using MvcFrontendKit.Extensions;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Views;

/// <summary>
/// Verifies that the compiled shared layout serializes only final provider-safe attribution.
/// </summary>
public sealed class TileAttributionLayoutRenderingTests
{
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
            UpdatedAt = DateTime.UtcNow
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
