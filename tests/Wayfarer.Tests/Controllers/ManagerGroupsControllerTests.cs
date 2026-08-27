using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Http;
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

/// <summary>
/// Manager groups controller basics.
/// </summary>
public class ManagerGroupsControllerTests : TestBase
{
    [Fact]
    public async Task Index_ReturnsDistinctManagedAndOwnedGroups()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "manager");
        db.Users.Add(user);
        var owned = new Group { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = "Owned" };
        var managed = new Group { Id = Guid.NewGuid(), OwnerUserId = "other", Name = "Managed" };
        db.Groups.AddRange(owned, managed);
        db.GroupMembers.AddRange(
            new GroupMember
            {
                GroupId = owned.Id,
                UserId = user.Id,
                Role = GroupMember.Roles.Manager,
                Status = GroupMember.MembershipStatuses.Active
            },
            new GroupMember
            {
                GroupId = managed.Id,
                UserId = user.Id,
                Role = GroupMember.Roles.Manager,
                Status = GroupMember.MembershipStatuses.Active
            });
        await db.SaveChangesAsync();

        var controller = new GroupsController(NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(user.Id) };

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<IEnumerable<Group>>(view.Model!);
        Assert.Equal(2, model.Count());
        var counts = Assert.IsAssignableFrom<Dictionary<Guid, int>>(view.ViewData["MemberCounts"]);
        Assert.True(counts.ContainsKey(owned.Id));
    }

    [Fact]
    public async Task Index_ReturnsUnauthorized_WhenNoUser()
    {
        var db = CreateDbContext();
        var controller = new GroupsController(NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await controller.Index();

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Members_ReturnsNotFound_WhenGroupMissing()
    {
        var db = CreateDbContext();
        var controller = new GroupsController(NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser("u1") };

        var result = await controller.Members(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Members_ReturnsView_WhenOwner()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        var group = new Group { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = "G" };
        db.Users.Add(user);
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember
        {
            GroupId = group.Id,
            UserId = user.Id,
            Status = GroupMember.MembershipStatuses.Active,
            Role = GroupMember.Roles.Owner
        });
        await db.SaveChangesAsync();

        var controller = new GroupsController(NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(user.Id) };

        var result = await controller.Members(group.Id);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal(group, view.ViewData["Group"]);
        Assert.Equal(user.Id, view.ViewData["CurrentUserId"]);
    }

    [Fact]
    public async Task Members_ReturnsForbid_WhenUserNotOwnerOrManager()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        var group = new Group { Id = Guid.NewGuid(), OwnerUserId = "other", Name = "G" };
        var membership = new GroupMember
        {
            GroupId = group.Id,
            UserId = user.Id,
            Status = GroupMember.MembershipStatuses.Active,
            Role = GroupMember.Roles.Member
        };
        db.Users.Add(user);
        db.Groups.Add(group);
        db.GroupMembers.Add(membership);
        await db.SaveChangesAsync();

        var controller = new GroupsController(NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(user.Id) };

        var result = await controller.Members(group.Id);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Invite_ReturnsUnauthorized_WhenNoUser()
    {
        var db = CreateDbContext();
        var controller = new GroupsController(NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await controller.Invite(Guid.NewGuid(), "u2");

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task RemoveMember_ReturnsUnauthorized_WhenNoUser()
    {
        var db = CreateDbContext();
        var controller = new GroupsController(NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await controller.RemoveMember(Guid.NewGuid(), "u2");

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task RevokeInvite_ReturnsUnauthorized_WhenNoUser()
    {
        var db = CreateDbContext();
        var controller = new GroupsController(NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db), new SseService());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await controller.RevokeInvite(Guid.NewGuid(), Guid.NewGuid());

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Map_ReturnsForbid_WhenNotOwnerOrManager()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        var group = new Group { Id = Guid.NewGuid(), OwnerUserId = "other", Name = "G" };
        var membership = new GroupMember
        {
            GroupId = group.Id,
            UserId = user.Id,
            Status = GroupMember.MembershipStatuses.Active,
            Role = GroupMember.Roles.Member
        };
        db.Users.Add(user);
        db.Groups.Add(group);
        db.GroupMembers.Add(membership);
        await db.SaveChangesAsync();

        var controller = new GroupsController(NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db), new SseService());
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(user.Id) };

        var result = await controller.Map(group.Id);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task ConfirmDelete_ReturnsNotFound_WhenGroupMissing()
    {
        var db = CreateDbContext();
        var groupService = new Mock<IGroupService>();
        groupService.Setup(s => s.DeleteGroupAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());
        var controller = new GroupsController(NullLogger<BaseController>.Instance, db, groupService.Object, new InvitationService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser("u1") };

        var result = await controller.ConfirmDelete(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Edit_Get_ReturnsForbid_WhenNotOwner()
    {
        var db = CreateDbContext();
        var group = new Group { Id = Guid.NewGuid(), OwnerUserId = "other", Name = "Team" };
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        var controller = new GroupsController(NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser("u1") };

        var result = await controller.Edit(group.Id);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Edit_Post_ReturnsView_WhenNameMissing()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "owner");
        var group = new Group { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = "Team" };
        db.Users.Add(user);
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        var controller = new GroupsController(NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(user.Id) };

        var result = await controller.Edit(group.Id, "   ", "desc", "Family");

        var view = Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Equal(group.Id, Assert.IsType<Group>(view.Model!).Id);
    }

    [Fact]
    public void Create_Get_ReturnsView()
    {
        var db = CreateDbContext();
        var controller = new GroupsController(NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db));

        var result = controller.Create();

        Assert.IsType<ViewResult>(result);
    }

    [Fact]
    public async Task Create_Post_ReturnsUnauthorized_WhenNoUser()
    {
        var db = CreateDbContext();
        var controller = new GroupsController(NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await controller.Create("Team", "Description", "Family");

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Create_Post_ReturnsView_WhenNameMissing()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db, user.Id);

        var result = await controller.Create("", "Description", "Family");

        var view = Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
    }

    [Fact]
    public async Task Create_Post_ReturnsView_WhenGroupTypeMissing()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db, user.Id);

        var result = await controller.Create("Team", "Description", "");

        var view = Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
    }

    [Fact]
    public async Task Create_Post_ReturnsView_WhenGroupTypeInvalid()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db, user.Id);

        var result = await controller.Create("Team", "Description", "InvalidType");

        var view = Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
    }

    [Fact]
    public async Task Create_Post_CreatesGroup_WhenValid()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db, user.Id);

        var result = await controller.Create("Team Alpha", "A great team", "Organization");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Name == "Team Alpha");
        Assert.NotNull(group);
        Assert.Equal("Organization", group.GroupType);
        Assert.Equal("A great team", group.Description);
    }

    [Fact]
    public async Task Edit_Get_ReturnsView_WhenOwner()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "owner");
        var group = new Group { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = "Team" };
        db.Users.Add(user);
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        var controller = new GroupsController(NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(user.Id) };

        var result = await controller.Edit(group.Id);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal(group, view.Model);
    }

    [Fact]
    public async Task Edit_Post_UpdatesGroup_WhenValid()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "owner");
        var group = new Group { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = "Old Name", GroupType = "Family" };
        db.Users.Add(user);
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember
        {
            GroupId = group.Id,
            UserId = user.Id,
            Role = GroupMember.Roles.Owner,
            Status = GroupMember.MembershipStatuses.Active
        });
        await db.SaveChangesAsync();
        var controller = BuildController(db, user.Id);

        var result = await controller.Edit(group.Id, "New Name", "New description", "Friends");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        var updated = await db.Groups.FindAsync(group.Id);
        Assert.Equal("New Name", updated!.Name);
        Assert.Equal("New description", updated.Description);
        Assert.Equal("Friends", updated.GroupType);
    }

    [Fact]
    public async Task Edit_Post_ReturnsView_WhenGroupTypeMissing()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "owner");
        var group = new Group { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = "Team" };
        db.Users.Add(user);
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        var controller = new GroupsController(NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(user.Id) };

        var result = await controller.Edit(group.Id, "Name", "desc", "");

        var view = Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
    }

    [Fact]
    public async Task Edit_Post_ReturnsView_WhenGroupTypeInvalid()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "owner");
        var group = new Group { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = "Team" };
        db.Users.Add(user);
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        var controller = new GroupsController(NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(user.Id) };

        var result = await controller.Edit(group.Id, "Name", "desc", "InvalidType");

        var view = Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
    }

    [Fact]
    public async Task Invite_RedirectsWithAlert_WhenInviteeUserIdMissing()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        var group = new Group { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = "Team" };
        db.Users.Add(user);
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        var controller = BuildController(db, user.Id);

        var result = await controller.Invite(group.Id, "");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Members", redirect.ActionName);
    }

    [Fact]
    public async Task Invite_SendsInvitation_WhenValid()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner", username: "owner");
        var invitee = TestDataFixtures.CreateUser(id: "invitee", username: "invitee");
        var group = new Group { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = "Team" };
        db.Users.AddRange(owner, invitee);
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember
        {
            GroupId = group.Id,
            UserId = owner.Id,
            Role = GroupMember.Roles.Owner,
            Status = GroupMember.MembershipStatuses.Active
        });
        await db.SaveChangesAsync();
        var controller = BuildController(db, owner.Id);

        var result = await controller.Invite(group.Id, invitee.Id);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        var invite = await db.GroupInvitations.FirstOrDefaultAsync(i => i.GroupId == group.Id && i.InviteeUserId == invitee.Id);
        Assert.NotNull(invite);
    }

    [Fact]
    public async Task RemoveMember_RemovesMember_WhenValid()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var member = TestDataFixtures.CreateUser(id: "member");
        var group = new Group { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = "Team" };
        db.Users.AddRange(owner, member);
        db.Groups.Add(group);
        db.GroupMembers.AddRange(
            new GroupMember
            {
                GroupId = group.Id,
                UserId = owner.Id,
                Role = GroupMember.Roles.Owner,
                Status = GroupMember.MembershipStatuses.Active
            },
            new GroupMember
            {
                GroupId = group.Id,
                UserId = member.Id,
                Role = GroupMember.Roles.Member,
                Status = GroupMember.MembershipStatuses.Active
            });
        await db.SaveChangesAsync();
        var controller = BuildController(db, owner.Id);

        var result = await controller.RemoveMember(group.Id, member.Id);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        var membership = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == group.Id && m.UserId == member.Id);
        Assert.Equal(GroupMember.MembershipStatuses.Removed, membership!.Status);
    }

    [Fact]
    public async Task RevokeInvite_RevokesInvite_WhenValid()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var invitee = TestDataFixtures.CreateUser(id: "invitee");
        var group = new Group { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = "Team" };
        var invite = new GroupInvitation
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            InviterUserId = owner.Id,
            InviteeUserId = invitee.Id,
            Token = Guid.NewGuid().ToString(),
            Status = GroupInvitation.InvitationStatuses.Pending,
            CreatedAt = DateTime.UtcNow
        };
        db.Users.AddRange(owner, invitee);
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember
        {
            GroupId = group.Id,
            UserId = owner.Id,
            Role = GroupMember.Roles.Owner,
            Status = GroupMember.MembershipStatuses.Active
        });
        db.GroupInvitations.Add(invite);
        await db.SaveChangesAsync();
        var controller = BuildController(db, owner.Id);

        var result = await controller.RevokeInvite(group.Id, invite.Id);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        var revoked = await db.GroupInvitations.FindAsync(invite.Id);
        Assert.Equal(GroupInvitation.InvitationStatuses.Revoked, revoked!.Status);
    }

    [Fact]
    public async Task ConfirmDelete_DeletesGroup_WhenOwner()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var group = new Group { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = "Team" };
        db.Users.Add(owner);
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember
        {
            GroupId = group.Id,
            UserId = owner.Id,
            Role = GroupMember.Roles.Owner,
            Status = GroupMember.MembershipStatuses.Active
        });
        await db.SaveChangesAsync();
        var controller = BuildController(db, owner.Id);

        var result = await controller.ConfirmDelete(group.Id);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        var deleted = await db.Groups.FindAsync(group.Id);
        Assert.Null(deleted);
    }

    [Fact]
    public async Task InviteAjax_ReturnsOk_WhenValid()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var invitee = TestDataFixtures.CreateUser(id: "invitee");
        var group = new Group { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = "Team" };
        db.Users.AddRange(owner, invitee);
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember
        {
            GroupId = group.Id,
            UserId = owner.Id,
            Role = GroupMember.Roles.Owner,
            Status = GroupMember.MembershipStatuses.Active
        });
        await db.SaveChangesAsync();
        var controller = BuildController(db, owner.Id);

        var result = await controller.InviteAjax(group.Id, invitee.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var success = (bool)payload.GetType().GetProperty("success")!.GetValue(payload)!;
        Assert.True(success);
    }

    [Fact]
    public async Task InviteAjax_ReturnsUnauthorized_WhenNoUser()
    {
        var db = CreateDbContext();
        var controller = new GroupsController(NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await controller.InviteAjax(Guid.NewGuid(), "u2");

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task RemoveMemberAjax_ReturnsOk_WhenValid()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var member = TestDataFixtures.CreateUser(id: "member");
        var group = new Group { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = "Team" };
        db.Users.AddRange(owner, member);
        db.Groups.Add(group);
        db.GroupMembers.AddRange(
            new GroupMember
            {
                GroupId = group.Id,
                UserId = owner.Id,
                Role = GroupMember.Roles.Owner,
                Status = GroupMember.MembershipStatuses.Active
            },
            new GroupMember
            {
                GroupId = group.Id,
                UserId = member.Id,
                Role = GroupMember.Roles.Member,
                Status = GroupMember.MembershipStatuses.Active
            });
        await db.SaveChangesAsync();
        var controller = BuildController(db, owner.Id);

        var result = await controller.RemoveMemberAjax(group.Id, member.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var success = (bool)payload.GetType().GetProperty("success")!.GetValue(payload)!;
        Assert.True(success);
    }

    [Fact]
    public async Task RevokeInviteAjax_ReturnsOk_WhenValid()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var invitee = TestDataFixtures.CreateUser(id: "invitee");
        var group = new Group { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = "Team" };
        var invite = new GroupInvitation
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            InviterUserId = owner.Id,
            InviteeUserId = invitee.Id,
            Token = Guid.NewGuid().ToString(),
            Status = GroupInvitation.InvitationStatuses.Pending,
            CreatedAt = DateTime.UtcNow
        };
        db.Users.AddRange(owner, invitee);
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember
        {
            GroupId = group.Id,
            UserId = owner.Id,
            Role = GroupMember.Roles.Owner,
            Status = GroupMember.MembershipStatuses.Active
        });
        db.GroupInvitations.Add(invite);
        await db.SaveChangesAsync();
        var controller = BuildController(db, owner.Id);

        var result = await controller.RevokeInviteAjax(group.Id, invite.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var success = (bool)payload.GetType().GetProperty("success")!.GetValue(payload)!;
        Assert.True(success);
    }

    [Fact]
    public async Task Map_ReturnsView_WhenOwner()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var group = new Group { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = "Team" };
        db.Users.Add(owner);
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember
        {
            GroupId = group.Id,
            UserId = owner.Id,
            Role = GroupMember.Roles.Owner,
            Status = GroupMember.MembershipStatuses.Active
        });
        await db.SaveChangesAsync();
        var controller = new GroupsController(NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(owner.Id) };

        var result = await controller.Map(group.Id);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal(group, view.ViewData["Group"]);
    }

    private GroupsController BuildController(ApplicationDbContext db, string userId)
    {
        var controller = new GroupsController(NullLogger<BaseController>.Instance, db, new GroupService(db), new InvitationService(db));
        var httpContext = BuildHttpContextWithUser(userId);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());
        return controller;
    }
}
