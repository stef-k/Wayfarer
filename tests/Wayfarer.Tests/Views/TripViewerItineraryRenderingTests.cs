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
using Wayfarer.Models.ViewModels;
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
                segment.QuerySelectorAll(".segment-journey-role").Select(item => NormalizeText(item.TextContent)));
            Assert.Equal("A → B → C", segment.QuerySelector(".segment-journey-trail")?.TextContent.Trim());
            Assert.Contains("1 1, 2 2, 3 3", segment.GetAttribute("data-route-wkt"));
            Assert.True(segment.ClassList.Contains("segment-list-item-view"));
            Assert.Equal("Click to view the route", segment.GetAttribute("title"));
            Assert.Single(segment.QuerySelectorAll(".segment-toggle"));
            Assert.Equal(3, segment.QuerySelectorAll(".segment-journey-place").Length);
            Assert.Contains("A → B → C", document.QuerySelector("#readable-modal-body")?.TextContent);

            var customTrip = WaypointTrip();
            Assert.Single(customTrip.Segments).RouteGeometry = new LineString([
                new Coordinate(1, 1), new Coordinate(1.5, 1.5), new Coordinate(2, 2), new Coordinate(3, 3)
            ]) { SRID = 4326 };
            Assert.Single(Assert.Single(customTrip.Segments).Waypoints).RouteVertexIndex = 2;
            var customHtml = await RenderViewerAsync(scope.ServiceProvider, customTrip);
            var customDocument = await new HtmlParser().ParseDocumentAsync(customHtml);
            var customSegment = Assert.Single(customDocument.QuerySelectorAll(".segment-list-item"));
            Assert.True(customSegment.ClassList.Contains("segment-list-item-view"));
            Assert.Contains("1.5 1.5", customSegment.GetAttribute("data-route-wkt"));
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PublicViewerFailsClosedForForeignWaypointAndUnavailableRouteInteraction()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), $"wayfarer-foreign-waypoint-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(webRoot);
        await File.WriteAllTextAsync(Path.Combine(webRoot, "frontend.manifest.json"), "{}");
        try
        {
            using var host = BuildRazorHost(webRoot);
            using var scope = host.Services.CreateScope();
            var trip = WaypointTrip();
            var foreignRegion = new Region
            {
                Id = Guid.NewGuid(), TripId = Guid.NewGuid(), UserId = "foreign", Name = "Foreign private region"
            };
            var foreign = new Place
            {
                Id = Guid.NewGuid(), Region = foreignRegion, RegionId = foreignRegion.Id, UserId = "foreign",
                Name = "Foreign private waypoint", Location = new Point(9, 9) { SRID = 4326 }
            };
            var waypoint = Assert.Single(trip.Segments).Waypoints.Single();
            waypoint.Place = foreign;
            waypoint.PlaceId = foreign.Id;

            var html = await RenderViewerAsync(scope.ServiceProvider, trip);
            var document = await new HtmlParser().ParseDocumentAsync(html);
            var segment = Assert.Single(document.QuerySelectorAll(".segment-list-item"));

            Assert.DoesNotContain(foreign.Id.ToString(), html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(foreign.Name, html, StringComparison.Ordinal);
            Assert.False(segment.ClassList.Contains("segment-list-item-view"));
            Assert.Null(segment.GetAttribute("data-route-wkt"));
            Assert.Null(segment.GetAttribute("title"));
            Assert.Empty(segment.QuerySelectorAll(".segment-toggle:not([disabled])"));
            Assert.Empty(segment.QuerySelectorAll($".segment-journey-place[data-place-id='{foreign.Id}']"));
            Assert.Contains("Route line is unavailable", segment.TextContent);
            Assert.Contains("Start: A", NormalizeText(segment.TextContent));
            Assert.Contains("Via 1: Unavailable intermediate place", NormalizeText(segment.TextContent));
            Assert.Contains("End: C", NormalizeText(segment.TextContent));
            Assert.Equal(2, segment.QuerySelectorAll(".segment-journey-place").Length);
            Assert.Contains("Unavailable intermediate place", document.QuerySelector("#readable-modal-body")?.TextContent);
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PdfRendersTheSameOrderedWaypointJourney()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), $"wayfarer-waypoint-pdf-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(webRoot);
        await File.WriteAllTextAsync(Path.Combine(webRoot, "frontend.manifest.json"), "{}");
        try
        {
            using var host = BuildRazorHost(webRoot);
            using var scope = host.Services.CreateScope();
            var trip = WaypointTrip();
            trip.User = new ApplicationUser { DisplayName = "Fixture owner" };
            var model = new TripPrintViewModel
            {
                Trip = trip,
                Regions = trip.Regions.ToList(),
                Places = trip.Regions.SelectMany(region => region.Places).ToList(),
                Segments = trip.Segments.ToList()
            };

            var html = await RenderViewAsync(scope.ServiceProvider, "/Views/Trip/Print.cshtml", model);
            var document = await new HtmlParser().ParseDocumentAsync(html);

            Assert.Contains("A → B → C", document.QuerySelector("#segments_all")?.TextContent);
            Assert.Equal(["Start: A", "Via 1: B", "End: C"],
                document.QuerySelectorAll("#segments_all .segment-journey-role").Select(item => NormalizeText(item.TextContent)));
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RichNotesUseSharedPresentationAndSuppressOnlyTerminalArtifacts()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), $"wayfarer-rich-notes-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(webRoot);
        await File.WriteAllTextAsync(Path.Combine(webRoot, "frontend.manifest.json"), "{}");
        try
        {
            using var host = BuildRazorHost(webRoot);
            using var scope = host.Services.CreateScope();
            var trip = WaypointTrip();
            trip.User = new ApplicationUser { DisplayName = "Fixture owner" };
            trip.Notes = "<p>Before</p><ol><li data-list=\"ordered\">Item</li><li data-list=\"ordered\"><br></li></ol>";

            var viewer = await new HtmlParser().ParseDocumentAsync(await RenderViewerAsync(scope.ServiceProvider, trip));
            var normalNotes = Assert.Single(viewer.QuerySelectorAll("#sidebar-primary .trip-notes.rich-notes-content"));
            var readableNotes = Assert.Single(viewer.QuerySelectorAll("#readable-modal-body .trip-notes-readable.rich-notes-content"));
            Assert.Equal("BeforeItem", NormalizeText(normalNotes.TextContent).Replace(" ", string.Empty));
            Assert.Single(normalNotes.QuerySelectorAll("li"));
            Assert.Single(readableNotes.QuerySelectorAll("li"));

            var model = new TripPrintViewModel { Trip = trip, Regions = trip.Regions.ToList(), Places = trip.Regions.SelectMany(region => region.Places).ToList(), Segments = trip.Segments.ToList() };
            var pdf = await new HtmlParser().ParseDocumentAsync(await RenderViewAsync(scope.ServiceProvider, "/Views/Trip/Print.cshtml", model));
            var richNotesStylesheet = Assert.Single(pdf.QuerySelectorAll("link[rel=stylesheet][href^='/css/rich-notes.css']"));
            Assert.Contains("rich-notes.css", richNotesStylesheet.GetAttribute("href"));
            var pdfNotes = Assert.Single(pdf.QuerySelectorAll(".notes.rich-notes-content"));
            Assert.Single(pdfNotes.QuerySelectorAll("li"));

            var blankTrip = WaypointTrip();
            blankTrip.User = new ApplicationUser { DisplayName = "Fixture owner" };
            blankTrip.Notes = "<ol><li data-list=\"ordered\"><br></li></ol>";
            foreach (var region in blankTrip.Regions) { region.Notes = blankTrip.Notes; foreach (var place in region.Places) place.Notes = blankTrip.Notes; }
            foreach (var segment in blankTrip.Segments) segment.Notes = blankTrip.Notes;
            var blankViewer = await new HtmlParser().ParseDocumentAsync(await RenderViewerAsync(scope.ServiceProvider, blankTrip));
            Assert.Empty(blankViewer.QuerySelectorAll(".rich-notes-content"));
            Assert.Empty(blankViewer.QuerySelectorAll(".trip-notes-readable"));
            var blankModel = new TripPrintViewModel { Trip = blankTrip, Regions = blankTrip.Regions.ToList(), Places = blankTrip.Regions.SelectMany(region => region.Places).ToList(), Segments = blankTrip.Segments.ToList() };
            var blankPdf = await new HtmlParser().ParseDocumentAsync(await RenderViewAsync(scope.ServiceProvider, "/Views/Trip/Print.cshtml", blankModel));
            Assert.Empty(blankPdf.QuerySelectorAll(".notes.rich-notes-content"));
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PdfDoesNotExposeForeignWaypoint()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), $"wayfarer-foreign-waypoint-pdf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(webRoot);
        await File.WriteAllTextAsync(Path.Combine(webRoot, "frontend.manifest.json"), "{}");
        try
        {
            using var host = BuildRazorHost(webRoot);
            using var scope = host.Services.CreateScope();
            var trip = WaypointTrip();
            trip.User = new ApplicationUser { DisplayName = "Fixture owner" };
            var foreignRegion = new Region
            {
                Id = Guid.NewGuid(), TripId = Guid.NewGuid(), UserId = "foreign", Name = "Foreign PDF region"
            };
            var foreign = new Place
            {
                Id = Guid.NewGuid(), Region = foreignRegion, RegionId = foreignRegion.Id, UserId = "foreign",
                Name = "Foreign PDF waypoint", Location = new Point(9, 9) { SRID = 4326 }
            };
            var waypoint = Assert.Single(trip.Segments).Waypoints.Single();
            waypoint.Place = foreign;
            waypoint.PlaceId = foreign.Id;
            var model = new TripPrintViewModel
            {
                Trip = trip, Regions = trip.Regions.ToList(),
                Places = trip.Regions.SelectMany(region => region.Places).ToList(), Segments = trip.Segments.ToList()
            };

            var html = await RenderViewAsync(scope.ServiceProvider, "/Views/Trip/Print.cshtml", model);

            Assert.DoesNotContain(foreign.Id.ToString(), html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(foreign.Name, html, StringComparison.Ordinal);
            Assert.Contains("Unavailable intermediate place", html);
            Assert.Contains("Route line is unavailable", html);
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

    /// <summary>Renders a production Razor view without introducing a separate browser runner.</summary>
    private static async Task<string> RenderViewAsync(IServiceProvider services, string path, object model)
    {
        var httpContext = new DefaultHttpContext { RequestServices = services };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var viewResult = services.GetRequiredService<ICompositeViewEngine>().GetView(null, path, isMainPage: true);
        Assert.True(viewResult.Success, string.Join(Environment.NewLine, viewResult.SearchedLocations ?? []));
        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };
        var tempData = new TempDataDictionary(httpContext, services.GetRequiredService<ITempDataProvider>());
        await using var writer = new StringWriter();
        var viewContext = new ViewContext(actionContext, Assert.IsAssignableFrom<IView>(viewResult.View), viewData, tempData, writer, new HtmlHelperOptions());
        await viewContext.View.RenderAsync(viewContext);
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

    /// <summary>Normalizes Razor formatting whitespace while retaining visible wording.</summary>
    private static string NormalizeText(string value) => string.Join(" ",
        value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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
