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

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("File missing", bad.Value);
    }

    [Fact]
    public async Task Import_RedirectsToTripEdit_OnSuccess()
    {
        var importSvc = new Mock<ITripImportService>();
        importSvc.Setup(s => s.ImportWayfarerKmlAsync(It.IsAny<Stream>(), "u1", TripImportMode.Auto))
            .ReturnsAsync(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var controller = BuildController(importSvc.Object);
        ConfigureControllerWithUser(controller, "u1");
        var file = CreateFormFile("content");

        var result = await controller.Import(file);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Edit", redirect.ActionName);
        Assert.Equal("Trip", redirect.ControllerName);
        Assert.Equal(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), redirect.RouteValues?["id"]);
    }

    [Fact]
    public async Task Import_ReturnsDuplicateJson_WhenDuplicateDetected()
    {
        var importSvc = new Mock<ITripImportService>();
        importSvc.Setup(s => s.ImportWayfarerKmlAsync(It.IsAny<Stream>(), "u1", TripImportMode.Auto))
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
}
