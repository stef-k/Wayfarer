using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// API icons listing.
/// </summary>
public class ApiIconsControllerTests : TestBase
{
    [Fact]
    public void GetIcons_ReturnsBadRequest_ForInvalidLayout()
    {
        var controller = BuildController(CreateTempWebRoot());

        var result = controller.GetIcons("triangle");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetIcons_ReturnsNotFound_WhenDirectoryMissing()
    {
        var env = CreateTempWebRoot();
        var controller = BuildController(env);

        var result = controller.GetIcons("marker");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void GetIcons_ReturnsIcons_WhenExists()
    {
        var env = CreateTempWebRoot();
        var layoutDir = Path.Combine(env.WebRootPath, "icons", "wayfarer-map-icons", "dist", "marker");
        Directory.CreateDirectory(layoutDir);
        File.WriteAllText(Path.Combine(layoutDir, "one.svg"), "<svg></svg>");
        var controller = BuildController(env);

        var result = controller.GetIcons("marker");

        var ok = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value!);
        Assert.Single(list);
    }

    private IconsController BuildController(IWebHostEnvironment env)
    {
        return new IconsController(CreateDbContext(), NullLogger<IconsController>.Instance, env);
    }

    private static IWebHostEnvironment CreateTempWebRoot()
    {
        var env = new Mock<IWebHostEnvironment>();
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        env.SetupGet(e => e.WebRootPath).Returns(root);
        return env.Object;
    }
}
