using AngleSharp.Dom;
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
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Views;

/// <summary>Verifies canonical itinerary collisions through the compiled normal and readable Razor markup.</summary>
public sealed class TripViewerItineraryRenderingTests
{
    [Fact]
    public async Task ViewerRendersEqualAndNullableOrdersIdenticallyAcrossNormalAndReadableMarkup()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), $"wayfarer-itinerary-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(webRoot);
        // Supply the production-only layout dependency without requiring frontend assets to be built by the test job.
        await File.WriteAllTextAsync(Path.Combine(webRoot, "frontend.manifest.json"), "{}");
        try
        {
            using var host = BuildRazorHost(webRoot);
            using var scope = host.Services.CreateScope();
            var trip = CollisionTrip();

            var html = await RenderViewerAsync(scope.ServiceProvider, trip);
            var document = await new HtmlParser().ParseDocumentAsync(html);

            var normalLabels = Labels(document.QuerySelectorAll("#regions-accordion .itinerary-region-label, #regions-accordion .itinerary-place-label"));
            var readableLabels = Labels(document.QuerySelectorAll("#readable-modal-body .itinerary-region-label, #readable-modal-body .itinerary-place-label"));
            Assert.Equal(
                ["0-Unassigned Places", "1-Shadow child", "1-Zulu region", "1-Zulu equal", "2-Alpha equal", "3-Ordered gap", "4-Zulu null", "5-Alpha null", "2-Alpha region"],
                normalLabels);
            Assert.Equal(normalLabels, readableLabels);
            Assert.Equal(["Unassigned Places", "Zulu region", "Alpha region"], document.QuerySelectorAll("#regions-accordion .accordion-item").Select(element => element.GetAttribute("data-region-name")));
            Assert.Equal(
                ["Shadow child", "Zulu equal", "Alpha equal", "Ordered gap", "Zulu null", "Alpha null"],
                document.QuerySelectorAll("#regions-accordion .place-list-item").Select(element => element.GetAttribute("data-place-name")));
            Assert.All(document.QuerySelectorAll("#readable-modal-body .places-list"), list => Assert.Equal("DIV", list.TagName));
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ViewerRendersOrderedWaypointJourneyAndFallbackRoute()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), $"wayfarer-waypoint-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(webRoot);
        await File.WriteAllTextAsync(Path.Combine(webRoot, "frontend.manifest.json"), "{}");
        try
        {
            using var host = BuildRazorHost(webRoot);
            using var scope = host.Services.CreateScope();

            var html = await RenderViewerAsync(scope.ServiceProvider, WaypointTrip());
            var document = await new HtmlParser().ParseDocumentAsync(html);
            var segment = Assert.Single(document.QuerySelectorAll(".segment-list-item"));

            Assert.Equal(["Start: A", "Via 1: B", "End: C"],
                segment.QuerySelectorAll(".segment-journey-role").Select(item => item.TextContent.Trim()));
            Assert.Equal("A → B → C", segment.QuerySelector(".segment-journey-trail")?.TextContent.Trim());
            Assert.Contains("1 1,2 2,3 3", segment.GetAttribute("data-route-wkt"));
            Assert.Contains("A → B → C", document.QuerySelector("#readable-modal-body")?.TextContent);
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    private static IHost BuildRazorHost(string webRoot) =>
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
                    services.AddSingleton<IApplicationSettingsService, EmptyApplicationSettingsService>();
                    services.AddSingleton<ITempDataProvider, EmptyTempDataProvider>();
                })
                .Configure(_ => { }))
            .Build();

    private static async Task<string> RenderViewerAsync(IServiceProvider services, Trip trip)
    {
        var httpContext = new DefaultHttpContext { RequestServices = services };
        var routeData = new RouteData();
        routeData.Routers.Add(new RouteCollection());
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());
        var viewResult = services.GetRequiredService<ICompositeViewEngine>()
            .GetView(null, "/Views/Trip/Viewer.cshtml", isMainPage: true);
        Assert.True(viewResult.Success, string.Join(Environment.NewLine, viewResult.SearchedLocations ?? []));
        var view = Assert.IsAssignableFrom<IView>(viewResult.View);
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

    private sealed class EmptyApplicationSettingsService : IApplicationSettingsService
    {
        /// <inheritdoc />
        public ApplicationSettings GetSettings() => new();

        /// <inheritdoc />
        public string GetUploadsDirectoryPath() => Path.GetTempPath();

        /// <inheritdoc />
        public void RefreshSettings()
        {
        }
    }

    private static Trip CollisionTrip()
    {
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "owner", Name = "Collision trip", UpdatedAt = DateTime.UtcNow };
        var shadowRegion = Region("00000000-0000-0000-0000-000000000010", "Unassigned Places", 0, trip);
        var alphaRegion = Region("00000000-0000-0000-0000-000000000012", "Alpha region", 7, trip);
        var zuluRegion = Region("00000000-0000-0000-0000-000000000011", "Zulu region", 7, trip);
        shadowRegion.Places.Add(Place("00000000-0000-0000-0000-000000000030", "Shadow child", 1, shadowRegion));
        // Names intentionally reverse ID order so alphabetical tie-breakers cannot satisfy the expected labels.
        zuluRegion.Places.Add(Place("00000000-0000-0000-0000-000000000025", "Alpha null", null, zuluRegion));
        zuluRegion.Places.Add(Place("00000000-0000-0000-0000-000000000023", "Ordered gap", 20, zuluRegion));
        zuluRegion.Places.Add(Place("00000000-0000-0000-0000-000000000022", "Alpha equal", 9, zuluRegion));
        zuluRegion.Places.Add(Place("00000000-0000-0000-0000-000000000024", "Zulu null", null, zuluRegion));
        zuluRegion.Places.Add(Place("00000000-0000-0000-0000-000000000021", "Zulu equal", 9, zuluRegion));
        trip.Regions.Add(alphaRegion);
        trip.Regions.Add(shadowRegion);
        trip.Regions.Add(zuluRegion);
        return trip;
    }

    /// <summary>Builds the smallest semantic A to B to C viewer fixture.</summary>
    private static Trip WaypointTrip()
    {
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "owner", Name = "Waypoint trip", UpdatedAt = DateTime.UtcNow };
        var region = Region("00000000-0000-0000-0000-000000000110", "Region", 1, trip);
        var start = Place("00000000-0000-0000-0000-000000000111", "A", 1, region);
        var via = Place("00000000-0000-0000-0000-000000000112", "B", 2, region);
        var end = Place("00000000-0000-0000-0000-000000000113", "C", 3, region);
        start.Location = new Point(1, 1) { SRID = 4326 };
        via.Location = new Point(2, 2) { SRID = 4326 };
        end.Location = new Point(3, 3) { SRID = 4326 };
        region.Places.Add(start);
        region.Places.Add(via);
        region.Places.Add(end);
        trip.Regions.Add(region);
        var segment = new Segment
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = trip.UserId,
            Mode = "walk", FromPlace = start, FromPlaceId = start.Id, ToPlace = end, ToPlaceId = end.Id
        };
        segment.Waypoints.Add(new SegmentWaypoint
        {
            Segment = segment, SegmentId = segment.Id, Place = via, PlaceId = via.Id, Position = 0
        });
        trip.Segments.Add(segment);
        return trip;
    }

    private static Region Region(string id, string name, int displayOrder, Trip trip) =>
        new() { Id = Guid.Parse(id), Trip = trip, TripId = trip.Id, UserId = trip.UserId, Name = name, DisplayOrder = displayOrder };

    private static Place Place(string id, string name, int? displayOrder, Region region) =>
        new() { Id = Guid.Parse(id), Region = region, RegionId = region.Id, UserId = region.UserId, Name = name, DisplayOrder = displayOrder };

    private static string[] Labels(IHtmlCollection<IElement> elements) =>
        elements.Select(element => element.TextContent.Trim()).ToArray();

    private sealed class EmptyTempDataProvider : ITempDataProvider
    {
        /// <inheritdoc />
        public IDictionary<string, object> LoadTempData(Microsoft.AspNetCore.Http.HttpContext context) => new Dictionary<string, object>();

        /// <inheritdoc />
        public void SaveTempData(Microsoft.AspNetCore.Http.HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
