using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Provides shared setup helpers for Trip Editor place controller tests.
/// </summary>
public abstract class TripEditorPlaceControllerTestBase : TestBase
{
    /// <summary>
    /// Seeds a trip graph with regular regions, a shadow region, places, and related segments.
    /// </summary>
    protected static Trip SeedTripGraph(ApplicationDbContext db, string userId)
    {
        var trip = new Trip { Id = Guid.NewGuid(), UserId = userId, Name = "Trip", UpdatedAt = DateTime.UtcNow };
        var shadow = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = "Unassigned Places", DisplayOrder = 0 };
        var athens = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = "Athens", DisplayOrder = 1 };
        var thessaloniki = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = "Thessaloniki", DisplayOrder = 2 };
        var first = new Place { Id = Guid.NewGuid(), UserId = userId, Region = athens, RegionId = athens.Id, Name = "Acropolis", DisplayOrder = 1, Location = new Point(23, 37) { SRID = 4326 } };
        var second = new Place { Id = Guid.NewGuid(), UserId = userId, Region = thessaloniki, RegionId = thessaloniki.Id, Name = "Tower", DisplayOrder = 1, Location = new Point(22, 40) { SRID = 4326 } };
        athens.Places.Add(first);
        thessaloniki.Places.Add(second);
        trip.Regions.Add(shadow);
        trip.Regions.Add(athens);
        trip.Regions.Add(thessaloniki);
        trip.Segments.Add(new Segment { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, FromPlaceId = first.Id, ToPlaceId = second.Id, DisplayOrder = 1 });
        trip.Segments.Add(new Segment { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, FromPlaceId = second.Id, ToPlaceId = second.Id, DisplayOrder = 2 });
        db.Trips.Add(trip);
        db.SaveChanges();
        return trip;
    }

    /// <summary>
    /// Builds a valid create-place request body with optional reverse-geocode inputs.
    /// </summary>
    protected static string ValidCreateBody(string name, bool reverseGeocode = false, double? latitude = 10, double? longitude = 20) =>
        $$"""
        {
          "name": "{{name}}",
          "notesHtml": "<p>Notes</p>",
          "address": "Manual address",
          "location": {{LocationJson(latitude, longitude)}},
          "iconName": "marker",
          "markerColor": "bg-blue",
          "reverseGeocode": {{reverseGeocode.ToString().ToLowerInvariant()}}
        }
        """;

    /// <summary>
    /// Builds a valid update-place request body for the supplied target region.
    /// </summary>
    protected static string ValidUpdateBody(Guid regionId, string name, double? latitude = 10, double? longitude = 20) =>
        $$"""
        {
          "regionId": "{{regionId}}",
          "name": "{{name}}",
          "notesHtml": "<p>Notes</p>",
          "address": "Manual address",
          "location": {{LocationJson(latitude, longitude)}},
          "iconName": "marker",
          "markerColor": "bg-blue",
          "reverseGeocode": false
        }
        """;

    /// <summary>
    /// Assigns a test user principal and role to a controller.
    /// </summary>
    protected static void ConfigureControllerWithUserRole(ControllerBase controller, string userId, string role = "User")
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = BuildHttpContextWithUser(userId, role)
        };
    }

    /// <summary>
    /// Builds the Trip Editor controller with place mutation dependencies.
    /// </summary>
    protected static TripEditorController BuildController(ApplicationDbContext db, ReverseGeocodingService? reverseGeocodingService = null)
    {
        var environment = BuildEnvironment();
        var iconColorProvider = new IconColorProvider(environment);
        return new TripEditorController(
            db,
            environment,
            iconColorProvider,
            Mock.Of<ITripMapThumbnailGenerator>(),
            Mock.Of<ICacheWarmupScheduler>(),
            new TripEditorRegionMutationService(db),
            new TripEditorPlaceMutationService(db, environment, iconColorProvider, reverseGeocodingService ?? new ReverseGeocodingService(new HttpClient(), Mock.Of<ILogger<BaseApiController>>())),
            Mock.Of<ILogger<TripEditorController>>());
    }

    /// <summary>
    /// Sends JSON request content to a controller action.
    /// </summary>
    protected static async Task<IActionResult> SendJson(
        TripEditorController controller,
        Func<TripEditorController, Task<IActionResult>> action,
        string requestBody)
    {
        var httpContext = controller.ControllerContext.HttpContext ?? new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        var body = Encoding.UTF8.GetBytes(requestBody);
        httpContext.Request.Body = new MemoryStream(body);
        httpContext.Request.ContentLength = body.Length;
        httpContext.Request.ContentType = "application/json";
        return await action(controller);
    }

    /// <summary>
    /// Asserts that an action returned a validation problem response.
    /// </summary>
    protected static ValidationProblemDetails AssertValidationProblem(IActionResult result)
    {
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("application/problem+json", badRequest.ContentTypes);
        return Assert.IsType<ValidationProblemDetails>(badRequest.Value);
    }

    /// <summary>
    /// Asserts that an action returned a successful editor mutation envelope.
    /// </summary>
    protected static EditorMutationResult<T> AssertMutation<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<EditorMutationResult<T>>(ok.Value);
    }

    private static string LocationJson(double? latitude, double? longitude) =>
        latitude.HasValue && longitude.HasValue
            ? $$"""{ "latitude": {{latitude.Value}}, "longitude": {{longitude.Value}} }"""
            : "null";

    private static IWebHostEnvironment BuildEnvironment()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), "wayfarer-trip-editor-place-tests", Guid.NewGuid().ToString("N"));
        var markerDir = Path.Combine(webRoot, "icons", "wayfarer-map-icons", "dist", "marker");
        Directory.CreateDirectory(markerDir);
        File.WriteAllText(Path.Combine(markerDir, "marker.svg"), "<svg></svg>");
        File.WriteAllText(Path.Combine(webRoot, "icons", "wayfarer-map-icons", "dist", "wayfarer-map-icons.css"), ".bg-blue{} .color-white{}");
        var mock = new Mock<IWebHostEnvironment>();
        mock.SetupGet(e => e.WebRootPath).Returns(webRoot);
        return mock.Object;
    }

    protected sealed class ThrowingReverseGeocodeHandler : HttpMessageHandler
    {
        /// <summary>
        /// Simulates provider/network failure after the editor has found a Mapbox token.
        /// </summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Reverse geocoding provider unavailable.");
    }

    protected sealed class CallerCanceledReverseGeocodeHandler : HttpMessageHandler
    {
        private readonly CancellationTokenSource _requestCancellation;

        /// <summary>
        /// Gets whether the outbound handler token observed the editor request cancellation.
        /// </summary>
        public bool RequestCancellationReachedOutboundHandler { get; private set; }

        /// <summary>
        /// Initializes a handler that cancels the caller token before surfacing provider cancellation.
        /// </summary>
        public CallerCanceledReverseGeocodeHandler(CancellationTokenSource requestCancellation)
        {
            _requestCancellation = requestCancellation;
        }

        /// <summary>
        /// Simulates cancellation that must propagate instead of becoming a geocoding warning.
        /// </summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requestCancellation.Cancel();
            RequestCancellationReachedOutboundHandler = cancellationToken.IsCancellationRequested;
            throw new TaskCanceledException("Request cancellation reached reverse geocoding.");
        }
    }

    protected sealed class ProviderTimeoutReverseGeocodeHandler : HttpMessageHandler
    {
        /// <summary>
        /// Simulates an HTTP timeout while the caller request remains active.
        /// </summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("Reverse geocoding provider timed out.");
    }
}
