using System.Globalization;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Views;

/// <summary>Exercises Create's controller validation response through its compiled production Razor view.</summary>
public sealed class LocationCreateRenderingTests : TestBase
{
    /// <summary>ModelState wins on invalid POST; a fresh GET still delegates local time to the browser.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimestampAndNotesSurviveValidationRedisplay(bool invalidPost)
    {
        using var host = BuildRazorHost(CreateTestDirectory());
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        var http = BuildHttpContextWithUser("owner");
        http.RequestServices = services;
        var route = new RouteData();
        route.Routers.Add(new RouteCollection());
        route.Values["area"] = "User";
        route.Values["controller"] = "Location";
        route.Values["action"] = "Create";
        var action = new ActionContext(http, route, new ControllerActionDescriptor(), new ModelStateDictionary());
        using var client = new HttpClient();
        var controller = new Wayfarer.Areas.User.Controllers.LocationController(
            NullLogger<BaseController>.Instance, CreateDbContext(),
            new ReverseGeocodingService(client, NullLogger<Wayfarer.Areas.Api.Controllers.BaseApiController>.Instance),
            Mock.Of<SseService>(), Mock.Of<IPlaceVisitDetectionService>())
        {
            ControllerContext = new ControllerContext(action),
            ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), action.ModelState),
            TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>())
        };
        const string timestamp = "2026-10-01T12:34";
        const string notes = "<p>Attempted Notes</p>";
        ViewResult response;
        if (invalidPost)
        {
            var intent = Assert.IsType<AddLocationViewModel>(Assert.IsType<ViewResult>(await controller.Create()).Model);
            // These are the attempted values MVC retains after latitude binding fails.
            controller.ModelState.SetModelValue("LocalTimestamp", timestamp, timestamp);
            controller.ModelState.SetModelValue("Latitude", "invalid", "invalid");
            controller.ModelState.SetModelValue("Longitude", "23.7275", "23.7275");
            controller.ModelState.AddModelError("Latitude", "Invalid coordinate");
            response = Assert.IsType<ViewResult>(await controller.Create(new AddLocationViewModel
            {
                CreateKey = intent.CreateKey, CreateIntent = intent.CreateIntent,
                LocalTimestamp = DateTime.ParseExact(timestamp, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
                Longitude = 23.7275, Notes = notes
            }));
        }
        else response = Assert.IsType<ViewResult>(await controller.Create());

        var model = Assert.IsType<AddLocationViewModel>(response.Model);
        var viewResult = services.GetRequiredService<ICompositeViewEngine>()
            .GetView(null, "/Areas/User/Views/Location/Create.cshtml", isMainPage: false);
        Assert.True(viewResult.Success, string.Join(Environment.NewLine, viewResult.SearchedLocations ?? []));
        var view = Assert.IsAssignableFrom<IView>(viewResult.View);
        await using var writer = new StringWriter();
        await view.RenderAsync(new ViewContext(action, view, response.ViewData, controller.TempData, writer, new HtmlHelperOptions()));
        var document = await new HtmlParser().ParseDocumentAsync(writer.ToString());
        var input = document.QuerySelector("#LocalTimestamp")!;
        Assert.Equal(invalidPost ? "false" : "true", input.GetAttribute("data-initialize-local-time"));
        Assert.Equal(invalidPost ? timestamp : model.LocalTimestamp.ToString("yyyy-MM-ddTHH:mm"), input.GetAttribute("value"));
        Assert.Equal(invalidPost ? "invalid" : null, document.QuerySelector("#Latitude")!.GetAttribute("value"));
        Assert.Equal(invalidPost ? "23.7275" : null, document.QuerySelector("#Longitude")!.GetAttribute("value"));
        Assert.Equal(invalidPost ? notes : null, document.QuerySelector("#Notes")!.GetAttribute("value"));
        Assert.Equal(invalidPost ? notes : "", document.QuerySelector("#notes")!.GetAttribute("data-notes-content"));
        if (invalidPost) Assert.Contains("Invalid coordinate", document.QuerySelector("[data-valmsg-for=Latitude]")!.TextContent);
    }

    /// <summary>Uses the established compiled-Razor host pattern without a listener or relational fixture.</summary>
    private static IHost BuildRazorHost(string webRoot) => Host.CreateDefaultBuilder()
        .ConfigureWebHostDefaults(webHost => webHost
            .UseContentRoot(Directory.GetCurrentDirectory())
            .UseWebRoot(webRoot)
            .UseSetting(WebHostDefaults.ApplicationKey, typeof(Trip).Assembly.GetName().Name)
            .ConfigureServices(services => services.AddControllersWithViews().AddApplicationPart(typeof(Trip).Assembly))
            .Configure(_ => { }))
        .Build();
}
