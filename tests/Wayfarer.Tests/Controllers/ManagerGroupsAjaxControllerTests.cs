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
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Manager AJAX endpoints for groups.
/// </summary>
public class ManagerGroupsAjaxControllerTests : TestBase
{
    [Fact]
    public async Task InviteAjax_ReturnsOkOnSuccess()
    {
        var groupId = Guid.NewGuid();
        var db = CreateDbContext();
        var groupService = new Mock<IGroupService>();
        var inviteService = new Mock<IInvitationService>();
        inviteService.Setup(s => s.InviteUserAsync(groupId, "manager-ajax", "user-1", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GroupInvitation
            {
                Id = Guid.NewGuid(),
                InviteeUserId = "user-1",
                GroupId = groupId,
                InviterUserId = "manager-ajax",
                Token = "tk"
            });
        var sse = new RecordingSseService();
        var controller = BuildController(db, groupService, inviteService, sse);

        var result = await controller.InviteAjax(groupId, "user-1");

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True((bool)ok.Value!.GetType().GetProperty("success")!.GetValue(ok.Value)!);
        Assert.Contains(("group-notifications-user-1", SseService.InvitationStateHint), sse.Messages);
    }

    [Fact]
    public async Task InviteAjax_ForbiddenOnUnauthorized()
    {
        var groupId = Guid.NewGuid();
        var db = CreateDbContext();
        var groupService = new Mock<IGroupService>();
        var inviteService = new Mock<IInvitationService>();
        inviteService.Setup(s => s.InviteUserAsync(groupId, "manager-ajax", "user-2", null, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException());
        var sse = new RecordingSseService();
        var controller = BuildController(db, groupService, inviteService, sse);

        var result = await controller.InviteAjax(groupId, "user-2");

        Assert.IsType<ObjectResult>(result);
        var status = (ObjectResult)result;
        Assert.Equal(403, status.StatusCode);
        Assert.Empty(sse.Messages);
    }

    [Fact]
    public async Task RemoveMemberAjax_ReturnsBadRequestOnInvalidOperation()
    {
        var groupId = Guid.NewGuid();
        var db = CreateDbContext();
        var groupService = new Mock<IGroupService>();
        groupService.Setup(s => s.RemoveMemberAsync(groupId, "manager-ajax", "user-3", CancellationToken.None))
            .ThrowsAsync(new InvalidOperationException("cannot"));
        var inviteService = new Mock<IInvitationService>();
        var sse = new RecordingSseService();
        var controller = BuildController(db, groupService, inviteService, sse);

        var result = await controller.RemoveMemberAjax(groupId, "user-3");

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.False((bool)bad.Value!.GetType().GetProperty("success")!.GetValue(bad.Value)!);
        Assert.Empty(sse.Messages);
    }

    [Fact]
    public async Task RevokeInviteAjax_ReturnsOkOnSuccess()
    {
        var groupId = Guid.NewGuid();
        var inviteId = Guid.NewGuid();
        var db = CreateDbContext();
        var groupService = new Mock<IGroupService>();
        var inviteService = new Mock<IInvitationService>();
        inviteService.Setup(service => service.RevokeAsync(inviteId, "manager-ajax", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InvitationRevocation(groupId, null));
        var controller = BuildController(db, groupService, inviteService);

        var result = await controller.RevokeInviteAjax(groupId, inviteId);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True((bool)ok.Value!.GetType().GetProperty("success")!.GetValue(ok.Value)!);
    }

    private static GroupsController BuildController(ApplicationDbContext db, Mock<IGroupService> groupService,
        Mock<IInvitationService> inviteService, SseService? sse = null)
    {
        var controller = new GroupsController(
            NullLogger<BaseController>.Instance,
            db,
            groupService.Object,
            inviteService.Object,
            sse ?? new SseService());
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "manager-ajax"),
                new Claim(ClaimTypes.Name, "manager-ajax"),
                new Claim(ClaimTypes.Role, ApplicationRoles.Manager)
            }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
        return controller;
    }

    private sealed class RecordingSseService : SseService
    {
        public List<(string Channel, string Data)> Messages { get; } = [];
        public override Task BroadcastAsync(string channel, string data)
        {
            Messages.Add((channel, data));
            return Task.CompletedTask;
        }
    }
}
