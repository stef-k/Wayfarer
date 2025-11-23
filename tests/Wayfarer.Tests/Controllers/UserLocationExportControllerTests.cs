using Microsoft.AspNetCore.Mvc;
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
    public async Task GeoJson_ReturnsFile_ForAuthenticatedUser()
    {
        var controller = new LocationExportController(CreateDbContext());
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser("u1") };

        var result = await controller.GeoJson();

        Assert.IsType<FileStreamResult>(result);
    }
}
