using System.IO;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// User trip import flow (Wayfarer KML) happy and error paths.
/// </summary>
public class TripImportControllerTests : TestBase
{
    private const string GenericRouteReminder =
        "Imported KML routes do not contain reliable transport information. Select a transport mode for each route where needed to enable automatic duration estimates.";

    [Fact]
    public async Task Import_ReturnsBadRequest_WhenFileMissing()
    {
        var controller = BuildController();
        ConfigureControllerWithUser(controller, "u1");

        var result = await controller.Import(null!);

        var json = Assert.IsType<JsonResult>(result);
        Assert.Equal(400, json.StatusCode);
        Assert.Equal("application/json", json.ContentType);
        Assert.Equal("invalid_file", json.Value?.GetType().GetProperty("code")?.GetValue(json.Value));
    }

    [Theory]
    [InlineData(TripImportMode.Auto)]
    [InlineData(TripImportMode.CreateNew)]
    public async Task Import_ReturnsBoundedCanonicalSuccessJson(TripImportMode mode)
    {
        var importSvc = new Mock<ITripImportService>();
        importSvc.Setup(s => s.ImportWayfarerKmlAsync(
                It.IsAny<Stream>(), "u1", mode, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TripImportResult(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                [new("generic_route_simplified", "Route", 1500, 400, 1d, 0.5d)]));
        var controller = BuildController(importSvc.Object);
        ConfigureControllerWithUser(controller, "u1");
        var file = CreateFormFile("content");

        var result = await controller.Import(file, mode);

        var json = Assert.IsType<JsonResult>(result);
        Assert.Equal("success", Property<string>(json.Value, "status"));
        Assert.Equal(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), Property<Guid>(json.Value, "tripId"));
        Assert.Equal("/User/Trip/Edit/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", Property<string>(json.Value, "redirectUrl"));
        Assert.Single(Property<IReadOnlyList<TripImportNotice>>(json.Value, "notices"));
    }

    /// <summary>Successful generic route imports install one informational editor reminder.</summary>
    [Fact]
    public async Task Import_GenericRoute_InstallsOneTimeInformationAndReturnsEditorRedirect()
    {
        var db = CreateDbContext();
        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var controller = BuildController(service);
        ConfigureControllerWithUser(controller, "u1");
        controller.TempData = new TempDataDictionary(controller.HttpContext, Mock.Of<ITempDataProvider>());
        var file = CreateFormFile("""
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document><name>Generic</name>
            <Placemark><name>Ella to Kandy by TRAIN</name><LineString><coordinates>80,7 81,7</coordinates></LineString></Placemark>
            </Document></kml>
            """);

        var result = await controller.Import(file, TripImportMode.CreateNew);

        var json = Assert.IsType<JsonResult>(result);
        Assert.Equal("success", Property<string>(json.Value, "status"));
        var tripId = Property<Guid>(json.Value, "tripId");
        Assert.Equal($"/User/Trip/Edit/{tripId:D}", Property<string>(json.Value, "redirectUrl"));
        Assert.Equal("info", controller.TempData["AlertType"]);
        Assert.Equal(GenericRouteReminder, controller.TempData["AlertMessage"]);
        Assert.Equal(2, controller.TempData.Count);
    }

    [Fact]
    public async Task Import_ReturnsDuplicateJson_WhenDuplicateDetected()
    {
        var importSvc = new Mock<ITripImportService>();
        importSvc.Setup(s => s.ImportWayfarerKmlAsync(
                It.IsAny<Stream>(), "u1", TripImportMode.Auto, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TripDuplicateException(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")));
        var controller = BuildController(importSvc.Object);
        ConfigureControllerWithUser(controller, "u1");
        var file = CreateFormFile("dup");

        var result = await controller.Import(file);

        var json = Assert.IsType<JsonResult>(result);
        var status = json.Value?.GetType().GetProperty("status")?.GetValue(json.Value)?.ToString();
        var tripId = json.Value?.GetType().GetProperty("tripId")?.GetValue(json.Value) as Guid?;
        Assert.Equal("duplicate", status);
        Assert.Equal(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), tripId);
    }

    [Theory]
    [InlineData(typeof(TripImportValidationException), 422, "validation_failed")]
    [InlineData(typeof(FormatException), 400, "invalid_kml")]
    public async Task Import_ReturnsSafeJson_ForExpectedFailures(Type exceptionType, int expectedStatus, string expectedCode)
    {
        var importSvc = new Mock<ITripImportService>();
        importSvc.Setup(s => s.ImportWayfarerKmlAsync(
                It.IsAny<Stream>(), "u1", TripImportMode.Auto, It.IsAny<CancellationToken>()))
            .ThrowsAsync((Exception)Activator.CreateInstance(exceptionType, "sensitive detail")!);
        var controller = BuildController(importSvc.Object);
        ConfigureControllerWithUser(controller, "u1");

        var result = await controller.Import(CreateFormFile("bad"));

        var json = Assert.IsType<JsonResult>(result);
        Assert.Equal(expectedStatus, json.StatusCode);
        Assert.Equal("application/json", json.ContentType);
        Assert.Equal("error", json.Value?.GetType().GetProperty("status")?.GetValue(json.Value));
        Assert.Equal(expectedCode, json.Value?.GetType().GetProperty("code")?.GetValue(json.Value));
        Assert.DoesNotContain("sensitive", json.Value?.GetType().GetProperty("message")?.GetValue(json.Value)?.ToString());
    }

    [Fact]
    public async Task Import_ReturnsGenericSafeJson_ForUnexpectedFailures()
    {
        var importSvc = new Mock<ITripImportService>();
        importSvc.Setup(s => s.ImportWayfarerKmlAsync(
                It.IsAny<Stream>(), "u1", TripImportMode.Auto, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("postgres connection details"));
        var controller = BuildController(importSvc.Object);
        ConfigureControllerWithUser(controller, "u1");

        var result = await controller.Import(CreateFormFile("bad"));

        var json = Assert.IsType<JsonResult>(result);
        Assert.Equal(500, json.StatusCode);
        Assert.Equal("import_failed", json.Value?.GetType().GetProperty("code")?.GetValue(json.Value));
        Assert.DoesNotContain("postgres", json.Value?.GetType().GetProperty("message")?.GetValue(json.Value)?.ToString());
    }

    /// <summary>Proves stable geometry budget codes and messages cross the controller unchanged and bounded.</summary>
    [Fact]
    public async Task Import_ReturnsStableGeometryBudgetFailure()
    {
        var importSvc = new Mock<ITripImportService>();
        importSvc.Setup(service => service.ImportWayfarerKmlAsync(
                It.IsAny<Stream>(), "u1", TripImportMode.Auto, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RouteGeometryBudgetException(
                "generic_kml_processing_limit", "The route geometry is too complex to process safely."));
        var controller = BuildController(importSvc.Object);
        ConfigureControllerWithUser(controller, "u1");

        var result = await controller.Import(CreateFormFile("complex"));

        var json = Assert.IsType<JsonResult>(result);
        Assert.Equal(422, json.StatusCode);
        Assert.Equal("generic_kml_processing_limit", Property<string>(json.Value, "code"));
        Assert.Equal("The route geometry is too complex to process safely.", Property<string>(json.Value, "message"));
    }

    /// <summary>Proves cancellation receives the approved stable response without internal detail.</summary>
    [Fact]
    public async Task Import_ReturnsStableCancellationFailure()
    {
        var importSvc = new Mock<ITripImportService>();
        importSvc.Setup(service => service.ImportWayfarerKmlAsync(
                It.IsAny<Stream>(), "u1", TripImportMode.Auto, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("internal cancellation detail"));
        var controller = BuildController(importSvc.Object);
        ConfigureControllerWithUser(controller, "u1");

        var result = await controller.Import(CreateFormFile("cancel"));

        var json = Assert.IsType<JsonResult>(result);
        Assert.Equal(499, json.StatusCode);
        Assert.Equal("import_cancelled", Property<string>(json.Value, "code"));
        Assert.Equal("The import was cancelled.", Property<string>(json.Value, "message"));
    }

    private static FormFile CreateFormFile(string content)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return new FormFile(stream, 0, stream.Length, "file", "trip.kml");
    }

    private TripImportController BuildController(ITripImportService? service = null)
    {
        service ??= Mock.Of<ITripImportService>();
        return new TripImportController(
            NullLogger<BaseController>.Instance,
            CreateDbContext(),
            service);
    }

    private static T Property<T>(object? owner, string name) =>
        Assert.IsAssignableFrom<T>(owner?.GetType().GetProperty(name)?.GetValue(owner));
}
