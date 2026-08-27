using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Manager.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Protected per-user notification coverage for Manager form mutations.</summary>
public sealed class ManagerGroupNotificationTests : TestBase
{
    /// <summary>Non-AJAX invitation creation emits the content-free affected-user hint.</summary>
    [Fact]
    public async Task InvitePublishesProtectedInvitationHintAfterSuccess()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "manager");
        var invitee = TestDataFixtures.CreateUser(id: "invitee");
        db.Users.AddRange(owner, invitee);
        var group = await new GroupService(db).CreateGroupAsync(owner.Id, "Group", null);
        var sse = new RecordingSseService();
        var controller = BuildController(db, owner.Id, new GroupService(db), sse);

        await controller.Invite(group.Id, invitee.Id);

        Assert.Contains(($"group-notifications-{invitee.Id}", SseService.InvitationStateHint), sse.Messages);
    }

    /// <summary>Non-AJAX removal emits no hint until its durable mutation succeeds.</summary>
    [Fact]
    public async Task RemoveMemberPublishesOnlyAfterSuccessfulMutation()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "manager");
        var member = TestDataFixtures.CreateUser(id: "member");
        db.Users.AddRange(owner, member);
        var groupService = new GroupService(db);
        var group = await groupService.CreateGroupAsync(owner.Id, "Group", null);
        db.GroupMembers.Add(new GroupMember
        {
            GroupId = group.Id, UserId = member.Id, Role = GroupMember.Roles.Member,
            Status = GroupMember.MembershipStatuses.Active
        });
        await db.SaveChangesAsync();
        var sse = new RecordingSseService();

        await BuildController(db, owner.Id, groupService, sse).RemoveMember(group.Id, member.Id);

        Assert.Contains(($"group-notifications-{member.Id}", SseService.MembershipStateHint), sse.Messages);
        sse.Messages.Clear();
        var failingService = new Mock<IGroupService>();
        failingService.Setup(service => service.RemoveMemberAsync(group.Id, owner.Id, member.Id, CancellationToken.None))
            .ThrowsAsync(new InvalidOperationException("failed"));
        await BuildController(db, owner.Id, failingService.Object, sse).RemoveMember(group.Id, member.Id);
        Assert.Empty(sse.Messages);
    }

    /// <summary>Each Manager revoke owner emits one private hint after durable revocation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokePublishesOneProtectedInvitationHint(bool ajax)
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "manager");
        var invitee = TestDataFixtures.CreateUser(id: "invitee");
        db.Users.AddRange(owner, invitee);
        var groupService = new GroupService(db);
        var group = await groupService.CreateGroupAsync(owner.Id, "Group", null);
        var invitationService = new InvitationService(db);
        var invitation = await invitationService.InviteUserAsync(group.Id, owner.Id, invitee.Id, null, null);
        var sse = new RecordingSseService();
        var controller = BuildController(db, owner.Id, groupService, invitationService, sse);

        if (ajax) await controller.RevokeInviteAjax(group.Id, invitation.Id);
        else await controller.RevokeInvite(group.Id, invitation.Id);

        var notification = Assert.Single(sse.Messages, message => message.Channel.StartsWith("group-notifications-"));
        Assert.Equal(($"group-notifications-{invitee.Id}", SseService.InvitationStateHint), notification);
    }

    private static GroupsController BuildController(
        ApplicationDbContext db, string userId, IGroupService groupService, RecordingSseService sse)
        => BuildController(db, userId, groupService, new InvitationService(db), sse);

    private static GroupsController BuildController(
        ApplicationDbContext db, string userId, IGroupService groupService,
        IInvitationService invitationService, RecordingSseService sse)
    {
        var controller = new GroupsController(
            NullLogger<BaseController>.Instance, db, groupService, invitationService, sse);
        var http = BuildHttpContextWithUser(userId);
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
