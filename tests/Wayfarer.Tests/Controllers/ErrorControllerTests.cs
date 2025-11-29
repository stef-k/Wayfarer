using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Controllers;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Covers ErrorController responses for API vs MVC paths.
/// </summary>
public class ErrorControllerTests
{
    [Fact]
    public void Index_ForApiPath_ReturnsJsonWithStatus()
    {
        var controller = new ErrorController(NullLogger<ErrorController>.Instance);
        var context = new DefaultHttpContext();
        context.Features.Set<IStatusCodeReExecuteFeature>(new StatusCodeReExecuteFeature
        {
            OriginalPath = "/api/test"
        });
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        var result = controller.Index(404);

        var json = Assert.IsType<JsonResult>(result);
        Assert.Equal(404, json.StatusCode);
        var status = json.Value!.GetType().GetProperty("status")!.GetValue(json.Value);
        Assert.Equal(404, status);
    }

    [Fact]
    public void Index_For404Mvc_ReturnsNotFoundView()
    {
        var controller = new ErrorController(NullLogger<ErrorController>.Instance);
        var context = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        var result = controller.Index(404);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("~/Views/Shared/404.cshtml", view.ViewName);
        Assert.Equal(404, controller.Response.StatusCode);
    }

    [Fact]
    public void Index_For500Mvc_ReturnsErrorView()
    {
        var controller = new ErrorController(NullLogger<ErrorController>.Instance);
        var context = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        var result = controller.Index(500);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("~/Views/Shared/Error.cshtml", view.ViewName);
        Assert.Equal(500, controller.Response.StatusCode);
    }

    private sealed class StatusCodeReExecuteFeature : IStatusCodeReExecuteFeature
    {
        public string OriginalPath { get; set; } = string.Empty;
        public string OriginalPathBase { get; set; } = string.Empty;
        public string? OriginalQueryString { get; set; }
    }
}
