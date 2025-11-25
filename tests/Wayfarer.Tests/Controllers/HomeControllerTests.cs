using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
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
