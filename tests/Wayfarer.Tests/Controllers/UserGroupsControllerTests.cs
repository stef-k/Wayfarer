using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Coverage for User-area GroupsController (member/invite management).
/// </summary>
public class UserGroupsControllerTests : TestBase
{
    [Fact]
    public async Task InviteAjax_AsOwner_CreatesInvitation()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var invitee = TestDataFixtures.CreateUser(id: "invitee");
        db.Users.AddRange(owner, invitee);
        var group = await SeedGroupWithOwnerAsync(db, owner);
        var sse = new FakeSseService();
        var controller = BuildController(db, owner, sse);

        var result = await controller.InviteAjax(group.Id, invitee.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True((bool)ok.Value!.GetType().GetProperty("success")!.GetValue(ok.Value)!);
        Assert.Single(db.GroupInvitations);
        Assert.NotEmpty(sse.Channels); // broadcast invoked
    }

    [Fact]
    public async Task InviteAjax_ForNonOwner_ReturnsForbidden()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var actor = TestDataFixtures.CreateUser(id: "actor");
        db.Users.AddRange(owner, actor);
        var group = await SeedGroupWithOwnerAsync(db, owner);
        var controller = BuildController(db, actor, new FakeSseService());

        var result = await controller.InviteAjax(group.Id, actor.Id);

        var forbid = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbid.StatusCode);
    }

    [Fact]
    public async Task RemoveMemberAjax_RemovesMember_WhenOwnerCalls()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var member = TestDataFixtures.CreateUser(id: "member");
        db.Users.AddRange(owner, member);
        var group = await SeedGroupWithOwnerAsync(db, owner);
        db.GroupMembers.Add(new GroupMember
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            UserId = member.Id,
            Role = GroupMember.Roles.Member,
            Status = GroupMember.MembershipStatuses.Active
        });
        await db.SaveChangesAsync();
        var controller = BuildController(db, owner, new FakeSseService());

        var result = await controller.RemoveMemberAjax(group.Id, member.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True((bool)ok.Value!.GetType().GetProperty("success")!.GetValue(ok.Value)!);
        var membership = await db.GroupMembers.SingleAsync(m => m.UserId == member.Id && m.GroupId == group.Id);
        Assert.NotEqual(GroupMember.MembershipStatuses.Active, membership.Status);
    }

    [Fact]
    public async Task RevokeInviteAjax_AsOwner_RevokesPending()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var invitee = TestDataFixtures.CreateUser(id: "invitee");
        db.Users.AddRange(owner, invitee);
        var group = await SeedGroupWithOwnerAsync(db, owner);
        var invService = new InvitationService(db);
        var invite = await invService.InviteUserAsync(group.Id, owner.Id, invitee.Id, null, null);
        var controller = BuildController(db, owner, new FakeSseService());

        var result = await controller.RevokeInviteAjax(group.Id, invite.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True((bool)ok.Value!.GetType().GetProperty("success")!.GetValue(ok.Value)!);
        var pending = await db.GroupInvitations.SingleAsync(i => i.Id == invite.Id);
        Assert.Equal(GroupInvitation.InvitationStatuses.Revoked, pending.Status);
    }

    [Fact]
    public async Task Members_ForNonOwner_ReturnsForbid()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var actor = TestDataFixtures.CreateUser(id: "actor");
        db.Users.AddRange(owner, actor);
        var group = await SeedGroupWithOwnerAsync(db, owner);
        var controller = BuildController(db, actor, new FakeSseService());

        var result = await controller.Members(group.Id);

        Assert.IsType<ForbidResult>(result);
    }

    private static async Task<Group> SeedGroupWithOwnerAsync(ApplicationDbContext db, ApplicationUser owner)
    {
        var group = new Group { Id = Guid.NewGuid(), Name = "Test", OwnerUserId = owner.Id };
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            UserId = owner.Id,
            Role = GroupMember.Roles.Owner,
            Status = GroupMember.MembershipStatuses.Active
        });
        await db.SaveChangesAsync();
        return group;
    }

    private static Wayfarer.Areas.User.Controllers.GroupsController BuildController(
        ApplicationDbContext db,
        ApplicationUser user,
        FakeSseService sse)
    {
        var controller = new Wayfarer.Areas.User.Controllers.GroupsController(
            NullLogger<BaseController>.Instance,
            db,
            new GroupService(db),
            new InvitationService(db),
            sse);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id),
                new Claim(ClaimTypes.Name, user.UserName ?? "user")
            }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private sealed class FakeSseService : Wayfarer.Parsers.SseService
    {
        public List<string> Channels { get; } = new();
        public override Task BroadcastAsync(string channel, string data)
        {
            Channels.Add(channel);
            return Task.CompletedTask;
        }
    }
}
