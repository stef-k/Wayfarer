using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// User location export controller quick checks.
/// </summary>
public class UserLocationExportControllerTests : TestBase
{
    [Fact]
    public void Index_ReturnsView()
    {
        var controller = new LocationExportController(NullLogger<LocationExportController>.Instance, CreateDbContext());
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser("u1") };

        var result = controller.Index();

        Assert.IsType<ViewResult>(result);
    }
}
