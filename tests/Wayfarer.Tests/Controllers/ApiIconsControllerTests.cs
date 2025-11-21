using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Icons API behaviors for layouts and discovery.
/// </summary>
public class ApiIconsControllerTests : TestBase
{
    [Fact]
    public void GetIcons_ReturnsBadRequest_ForInvalidLayout()
    {
        var controller = BuildControllerWithRoot(CreateTempRoot());

        var result = controller.GetIcons("invalid");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetIcons_ReturnsList_WhenDirectoryExists()
    {
        var root = CreateTempRoot();
        var dir = Path.Combine(root, "icons", "wayfarer-map-icons", "dist", "marker");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "pin.svg"), "<svg></svg>");
        var controller = BuildControllerWithRoot(root);

        var result = controller.GetIcons("marker");

        var ok = Assert.IsType<OkObjectResult>(result);
        var names = Assert.IsAssignableFrom<IEnumerable<string>>(ok.Value);
        Assert.Contains("pin", names);
    }

    [Fact]
    public void GetAvailableColors_ReturnsNotFound_WhenCssMissing()
    {
        var controller = BuildControllerWithRoot(CreateTempRoot());

        var result = controller.GetAvailableColors();

        Assert.IsType<NotFoundObjectResult>(result);
    }

    private string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private IconsController BuildControllerWithRoot(string root)
    {
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(root);
        var controller = new IconsController(CreateDbContext(), NullLogger<IconsController>.Instance, env.Object);
        return controller;
    }
}
