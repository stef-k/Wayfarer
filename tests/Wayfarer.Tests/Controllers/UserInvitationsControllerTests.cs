using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// User invitations controller basics.
/// </summary>
public class UserInvitationsControllerTests : TestBase
{
    [Fact]
    public void Index_ReturnsView_WithPageTitle()
    {
        var controller = new InvitationsController(
            NullLogger<BaseController>.Instance,
            CreateDbContext());

        var result = controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Invitations", controller.ViewData["Title"]);
    }
}
