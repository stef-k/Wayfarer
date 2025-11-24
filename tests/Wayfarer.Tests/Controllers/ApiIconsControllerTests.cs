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

    [Fact]
    public void GetIcons_IsCaseInsensitive_ForLayout()
    {
        var env = CreateTempWebRoot();
        var layoutDir = Path.Combine(env.WebRootPath, "icons", "wayfarer-map-icons", "dist", "circle");
        Directory.CreateDirectory(layoutDir);
        File.WriteAllText(Path.Combine(layoutDir, "icon1.svg"), "<svg></svg>");
        var controller = BuildController(env);

        var result = controller.GetIcons("CIRCLE");

        var ok = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value!);
        Assert.Single(list);
    }

    [Fact]
    public void GetIcons_ReturnsSortedList()
    {
        var env = CreateTempWebRoot();
        var layoutDir = Path.Combine(env.WebRootPath, "icons", "wayfarer-map-icons", "dist", "marker");
        Directory.CreateDirectory(layoutDir);
        File.WriteAllText(Path.Combine(layoutDir, "zebra.svg"), "<svg></svg>");
        File.WriteAllText(Path.Combine(layoutDir, "apple.svg"), "<svg></svg>");
        File.WriteAllText(Path.Combine(layoutDir, "monkey.svg"), "<svg></svg>");
        var controller = BuildController(env);

        var result = controller.GetIcons("marker");

        var ok = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsAssignableFrom<List<string>>(ok.Value!);
        Assert.Equal(3, list.Count);
        Assert.Equal("apple", list[0]);
        Assert.Equal("monkey", list[1]);
        Assert.Equal("zebra", list[2]);
    }

    [Fact]
    public void GetAvailableColors_ReturnsNotFound_WhenCssFileMissing()
    {
        var env = CreateTempWebRoot();
        var controller = BuildController(env);

        var result = controller.GetAvailableColors();

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void GetAvailableColors_ParsesCssClasses_WhenFileExists()
    {
        var env = CreateTempWebRoot();
        var cssDir = Path.Combine(env.WebRootPath, "icons", "wayfarer-map-icons", "dist");
        Directory.CreateDirectory(cssDir);
        var cssContent = @"
.bg-blue { background: blue; }
.bg-red { background: red; }
.color-white { color: white; }
.color-black { color: black; }
";
        File.WriteAllText(Path.Combine(cssDir, "wayfarer-map-icons.css"), cssContent);
        var controller = BuildController(env);

        var result = controller.GetAvailableColors();

        var ok = Assert.IsType<OkObjectResult>(result);
        dynamic value = ok.Value!;
        var bgList = value.GetType().GetProperty("backgrounds")?.GetValue(value) as List<string>;
        var glyphList = value.GetType().GetProperty("glyphs")?.GetValue(value) as List<string>;

        Assert.NotNull(bgList);
        Assert.NotNull(glyphList);
        Assert.Equal(2, bgList.Count);
        Assert.Contains("bg-blue", bgList);
        Assert.Contains("bg-red", bgList);
        Assert.Equal(2, glyphList.Count);
        Assert.Contains("color-white", glyphList);
        Assert.Contains("color-black", glyphList);
    }

    [Fact]
    public void GetIconsWithPreviews_ReturnsBadRequest_ForInvalidLayout()
    {
        var env = CreateTempWebRoot();
        var controller = BuildController(env);

        var result = controller.GetIconsWithPreviews("square");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetIconsWithPreviews_ReturnsNotFound_WhenDirectoryMissing()
    {
        var env = CreateTempWebRoot();
        var controller = BuildController(env);

        var result = controller.GetIconsWithPreviews("marker");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void GetIconsWithPreviews_ReturnsPreviewList_WhenExists()
    {
        var env = CreateTempWebRoot();
        var pngDir = Path.Combine(env.WebRootPath, "icons", "wayfarer-map-icons", "dist", "png", "marker");
        var blueDir = Path.Combine(pngDir, "blue");
        var redDir = Path.Combine(pngDir, "red");
        Directory.CreateDirectory(blueDir);
        Directory.CreateDirectory(redDir);
        File.WriteAllText(Path.Combine(blueDir, "restaurant.png"), "fake-png");
        File.WriteAllText(Path.Combine(blueDir, "hotel.png"), "fake-png");
        File.WriteAllText(Path.Combine(redDir, "restaurant.png"), "fake-png");
        var controller = BuildController(env);

        var result = controller.GetIconsWithPreviews("marker");

        var ok = Assert.IsType<OkObjectResult>(result);
        var list = ok.Value as IEnumerable<object>;
        Assert.NotNull(list);
        Assert.Equal(3, list.Count());
    }

    [Fact]
    public void GetIconsWithPreviews_IsCaseInsensitive_ForLayout()
    {
        var env = CreateTempWebRoot();
        var pngDir = Path.Combine(env.WebRootPath, "icons", "wayfarer-map-icons", "dist", "png", "circle");
        var colorDir = Path.Combine(pngDir, "green");
        Directory.CreateDirectory(colorDir);
        File.WriteAllText(Path.Combine(colorDir, "icon.png"), "fake-png");
        var controller = BuildController(env);

        var result = controller.GetIconsWithPreviews("CIRCLE");

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
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
