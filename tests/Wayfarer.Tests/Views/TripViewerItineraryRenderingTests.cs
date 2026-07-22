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
        using var host = BuildRazorHost();
        using var scope = host.Services.CreateScope();
        var trip = CollisionTrip();

        var html = await RenderViewerAsync(scope.ServiceProvider, trip);
        var document = await new HtmlParser().ParseDocumentAsync(html);

        var normalLabels = Labels(document.QuerySelectorAll("#regions-accordion .itinerary-region-label, #regions-accordion .itinerary-place-label"));
        var readableLabels = Labels(document.QuerySelectorAll("#readable-modal-body .itinerary-region-label, #readable-modal-body .itinerary-place-label"));
        Assert.Equal(
            ["1-Earlier region", "1-Earlier equal", "2-Later equal", "3-Ordered gap", "4-Earlier null", "5-Later null", "2-Later region"],
            normalLabels);
        Assert.Equal(normalLabels, readableLabels);
        Assert.Equal(["Earlier region", "Later region"], document.QuerySelectorAll("#regions-accordion .accordion-item").Select(element => element.GetAttribute("data-region-name")));
        Assert.Equal(
            ["Earlier equal", "Later equal", "Ordered gap", "Earlier null", "Later null"],
            document.QuerySelectorAll("#regions-accordion .place-list-item").Select(element => element.GetAttribute("data-place-name")));
        Assert.All(document.QuerySelectorAll("#readable-modal-body .places-list"), list => Assert.Equal("DIV", list.TagName));
    }

    private static IHost BuildRazorHost() =>
        Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webHost => webHost
                .UseContentRoot(Directory.GetCurrentDirectory())
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
        var laterRegion = Region("00000000-0000-0000-0000-000000000012", "Later region", 7, trip);
        var earlierRegion = Region("00000000-0000-0000-0000-000000000011", "Earlier region", 7, trip);
        earlierRegion.Places.Add(Place("00000000-0000-0000-0000-000000000025", "Later null", null, earlierRegion));
        earlierRegion.Places.Add(Place("00000000-0000-0000-0000-000000000023", "Ordered gap", 20, earlierRegion));
        earlierRegion.Places.Add(Place("00000000-0000-0000-0000-000000000022", "Later equal", 9, earlierRegion));
        earlierRegion.Places.Add(Place("00000000-0000-0000-0000-000000000024", "Earlier null", null, earlierRegion));
        earlierRegion.Places.Add(Place("00000000-0000-0000-0000-000000000021", "Earlier equal", 9, earlierRegion));
        trip.Regions.Add(laterRegion);
        trip.Regions.Add(earlierRegion);
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
