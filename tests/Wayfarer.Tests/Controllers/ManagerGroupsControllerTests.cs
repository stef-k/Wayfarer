using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
}
