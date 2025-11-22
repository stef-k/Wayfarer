using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Rendering helper that turns Razor views into HTML strings.
/// </summary>
public class RazorViewRendererTests
{
    [Fact]
    public async Task RenderViewToStringAsync_RendersModelContent()
    {
        var viewMock = new Mock<IView>();
        viewMock.Setup(v => v.RenderAsync(It.IsAny<ViewContext>()))
            .Returns((ViewContext ctx) =>
            {
                ctx.Writer.Write($"Hello {ctx.ViewData.Model}");
                return Task.CompletedTask;
            });

        var engine = new Mock<ICompositeViewEngine>();
        engine.Setup(e => e.GetView(null, "/Views/Test.cshtml", true))
            .Returns(ViewEngineResult.Found("test", viewMock.Object));

        var provider = new ServiceCollection().BuildServiceProvider();
        var temp = new Mock<ITempDataProvider>();
        var renderer = new RazorViewRenderer(engine.Object, provider, temp.Object);

        var html = await renderer.RenderViewToStringAsync("/Views/Test.cshtml", "World");

        Assert.Equal("Hello World", html);
    }

    [Fact]
    public async Task RenderViewToStringAsync_ThrowsWhenViewMissing()
    {
        var engine = new Mock<ICompositeViewEngine>();
        engine.Setup(e => e.GetView(null, "/Views/Missing.cshtml", true))
            .Returns(ViewEngineResult.NotFound("/Views/Missing.cshtml", Array.Empty<string>()));

        var provider = new ServiceCollection().BuildServiceProvider();
        var temp = new Mock<ITempDataProvider>();
        var renderer = new RazorViewRenderer(engine.Object, provider, temp.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            renderer.RenderViewToStringAsync("/Views/Missing.cshtml", new object()));
    }
}
