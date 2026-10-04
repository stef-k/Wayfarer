using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Admin.Controllers;
using Wayfarer.Controllers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
///     Smoke tests for HomeController basic pages.
/// </summary>
public class HomeControllerTests : TestBase
{
    [Fact]
    public void Index_ReturnsView()
    {
        var controller = BuildController();

        var result = controller.Index();

        Assert.IsType<ViewResult>(result);
    }

    [Fact]
    public void Privacy_ReturnsView()
    {
        var controller = BuildController();

        var result = controller.Privacy();

        Assert.IsType<ViewResult>(result);
    }

    [Fact]
    public void RegistrationClosed_ReturnsView()
    {
        var controller = BuildController();

        var result = controller.RegistrationClosed();

        Assert.IsType<ViewResult>(result);
    }

    /// <summary>Legacy documentation bookmarks redirect to Pages through normal MVC routing.</summary>
    [Theory]
    [InlineData("GET", "/docs")]
    [InlineData("GET", "/docs/")]
    [InlineData("HEAD", "/docs")]
    [InlineData("HEAD", "/docs/")]
    public async Task Docs_RedirectsPermanentlyToGitHubPages(string method, string path)
    {
        await using var app = await IdentityRouteHost.StartAsync(CreateDbContext(), CreateTestDirectory());
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("https://stef-k.github.io/Wayfarer/", response.Headers.Location?.AbsoluteUri);
    }

    private HomeController BuildController()
    {
        var http = new DefaultHttpContext();
        var controller = new HomeController(CreateDbContext(), NullLogger<UsersController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>())
        };
        return controller;
    }
}
