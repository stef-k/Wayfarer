using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Manager.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// High-ROI manager group controller behaviors.
/// </summary>
public class ManagerGroupsControllerTests : TestBase
{
    [Fact]
    public async Task RemoveMember_CallsService_AndRedirects()
    {
        var groupId = Guid.NewGuid();
        var db = CreateDbContext();
        var groupService = new Mock<IGroupService>();
        var invitationService = new Mock<IInvitationService>();
        var controller = BuildController(db, groupService, invitationService, managerId: "manager-1");

        var result = await controller.RemoveMember(groupId, "user-2");

        groupService.Verify(s => s.RemoveMemberAsync(groupId, "manager-1", "user-2", CancellationToken.None), Times.Once);
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Members", redirect.ActionName);
    }

    [Fact]
    public async Task RemoveMember_WhenUnauthorized_ReturnsForbid()
    {
        var groupId = Guid.NewGuid();
        var db = CreateDbContext();
        var groupService = new Mock<IGroupService>();
        groupService.Setup(s => s.RemoveMemberAsync(groupId, "manager-2", "user-3", CancellationToken.None))
            .ThrowsAsync(new UnauthorizedAccessException());
        var invitationService = new Mock<IInvitationService>();
        var controller = BuildController(db, groupService, invitationService, managerId: "manager-2");

        var result = await controller.RemoveMember(groupId, "user-3");

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Create_InvalidGroupType_ReturnsViewWithError()
    {
        var db = CreateDbContext();
        var controller = BuildController(db, new Mock<IGroupService>(), new Mock<IInvitationService>(), managerId: "manager-3");

        var result = await controller.Create("Test Group", null, "InvalidType");

        var view = Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey("groupType"));
    }

    private static GroupsController BuildController(
        ApplicationDbContext db,
        Mock<IGroupService> groupService,
        Mock<IInvitationService> invitationService,
        string managerId)
    {
        var controller = new GroupsController(
            NullLogger<BaseController>.Instance,
            db,
            groupService.Object,
            invitationService.Object);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, managerId),
                new Claim(ClaimTypes.Name, managerId),
                new Claim(ClaimTypes.Role, ApplicationRoles.Manager)
            }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
        return controller;
    }
}
