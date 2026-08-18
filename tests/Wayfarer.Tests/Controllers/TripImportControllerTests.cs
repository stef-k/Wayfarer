using System.IO;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
