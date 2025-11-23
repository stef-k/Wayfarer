using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Manager.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Manager groups controller basics.
/// </summary>
public class ManagerGroupsControllerTests : TestBase
{
    [Fact]
    public async Task Index_ReturnsUnauthorized_WhenNoUser()
    {
        var db = CreateDbContext();
        var controller = new GroupsController(NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await controller.Index();

        Assert.IsType<UnauthorizedResult>(result);
    }
}
