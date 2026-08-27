using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
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
        Assert.Contains(sse.Messages, message => message.Channel == $"group-{group.Id}");
    }

    /// <summary>Revoke routes only from durable invitation authority and skips email-only private hints.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokeUsesAuthoritativeGroupAndOptionalInvitee(bool hasInvitee)
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "manager");
        var invitee = TestDataFixtures.CreateUser(id: "invitee");
        db.Users.AddRange(owner, invitee);
        var groupService = new GroupService(db);
        var authoritativeGroup = await groupService.CreateGroupAsync(owner.Id, "Authoritative", null);
        var callerGroup = await groupService.CreateGroupAsync(owner.Id, "Caller", null);
        var invitationService = new InvitationService(db);
        var invitation = await invitationService.InviteUserAsync(
            authoritativeGroup.Id, owner.Id, hasInvitee ? invitee.Id : null,
            hasInvitee ? null : "email-only@example.test", null);
        var sse = new RecordingSseService();

        var result = await BuildController(db, owner.Id, groupService, invitationService, sse)
            .RevokeInviteAjax(callerGroup.Id, invitation.Id);

        Assert.IsType<OkObjectResult>(result);
        Assert.Contains(sse.Messages, message => message.Channel == $"group-{authoritativeGroup.Id}");
        Assert.DoesNotContain(sse.Messages, message => message.Channel == $"group-{callerGroup.Id}");
        Assert.Equal(hasInvitee ? 1 : 0,
            sse.Messages.Count(message => message.Channel.StartsWith("group-notifications-", StringComparison.Ordinal)));
        Assert.Equal(GroupInvitation.InvitationStatuses.Revoked,
            (await db.GroupInvitations.SingleAsync(item => item.Id == invitation.Id)).Status);
    }

    /// <summary>Failed mutation publishes nothing; post-commit transport failures retain truthful success.</summary>
    [Fact]
    public async Task RevokePublicationRespectsCommitBoundary()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "manager");
        var invitee = TestDataFixtures.CreateUser(id: "invitee");
        db.Users.AddRange(owner, invitee);
        var groupService = new GroupService(db);
        var group = await groupService.CreateGroupAsync(owner.Id, "Group", null);
        var invitationService = new InvitationService(db);
        var invitation = await invitationService.InviteUserAsync(group.Id, owner.Id, invitee.Id, null, null);
        await invitationService.RevokeAsync(invitation.Id, owner.Id);
        var failedSse = new RecordingSseService();

        var failed = await BuildController(db, owner.Id, groupService, invitationService, failedSse)
            .RevokeInviteAjax(group.Id, invitation.Id);

        Assert.IsType<BadRequestObjectResult>(failed);
        Assert.Empty(failedSse.Messages);

        var committed = await invitationService.InviteUserAsync(group.Id, owner.Id, invitee.Id, null, null);
        var throwingSse = new RecordingSseService(throwOnEveryBroadcast: true);
        var successful = await BuildController(db, owner.Id, groupService, invitationService, throwingSse)
            .RevokeInviteAjax(group.Id, committed.Id);

        Assert.IsType<OkObjectResult>(successful);
        Assert.Equal(2, throwingSse.Attempts);
        Assert.Equal(GroupInvitation.InvitationStatuses.Revoked,
            (await db.GroupInvitations.SingleAsync(item => item.Id == committed.Id)).Status);
        Assert.True((bool)Assert.IsType<OkObjectResult>(successful).Value!.GetType()
            .GetProperty("success")!.GetValue(((OkObjectResult)successful).Value)!);
    }

    /// <summary>Form revoke failures expose only the bounded alert contract.</summary>
    [Fact]
    public async Task RevokeFailureUsesBoundedAlert()
    {
        var invitationService = new Mock<IInvitationService>();
        invitationService.Setup(service => service.RevokeAsync(It.IsAny<Guid>(), "manager", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("sensitive persistence detail"));
        var controller = BuildController(CreateDbContext(), "manager", Mock.Of<IGroupService>(),
            invitationService.Object, new RecordingSseService());

        await controller.RevokeInvite(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal("Unable to revoke invitation.", controller.TempData["AlertMessage"]);
        Assert.Equal("danger", controller.TempData["AlertType"]);
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
        private readonly bool _throwOnEveryBroadcast;
        public RecordingSseService(bool throwOnEveryBroadcast = false) => _throwOnEveryBroadcast = throwOnEveryBroadcast;
        public List<(string Channel, string Data)> Messages { get; } = [];
        public int Attempts { get; private set; }
        public override Task BroadcastAsync(string channel, string data)
        {
            Attempts++;
            if (_throwOnEveryBroadcast) throw new InvalidOperationException("transport diagnostic");
            Messages.Add((channel, data));
            return Task.CompletedTask;
        }
    }
}
