using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Manager.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Restricted Manager views require active Group roles before loading protected models.</summary>
public sealed class ManagerGroupAuthorizationTests : TestBase
{
    /// <summary>Ownership metadata cannot expose rosters or any Edit validation fallback after departure.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(GroupMember.MembershipStatuses.Removed)]
    [InlineData(GroupMember.MembershipStatuses.Left)]
    public async Task InactiveMetadataOwnerCannotInspectOrEdit(string? status)
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(owner);
        var group = await new GroupService(db).CreateGroupAsync(owner.Id, "Protected", "Private description");
        var membership = await db.GroupMembers.SingleAsync();
        if (status == null) db.GroupMembers.Remove(membership);
        else membership.Status = status;
        await db.SaveChangesAsync();
        var controller = BuildController(db, owner.Id);

        var listing = Assert.IsType<ViewResult>(await controller.Index());
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<Group>>(listing.Model));
        Assert.Empty(Assert.IsType<Dictionary<Guid, int>>(listing.ViewData["MemberCounts"]));
        Assert.IsType<ForbidResult>(await controller.Members(group.Id));
        Assert.IsType<ForbidResult>(await controller.Edit(group.Id));
        Assert.IsType<ForbidResult>(await controller.Edit(group.Id, "Changed", null, "Friends"));
        Assert.IsType<ForbidResult>(await controller.Edit(group.Id, "", null, "Friends"));
        Assert.IsType<ForbidResult>(await controller.Edit(group.Id, "Changed", null, ""));
        Assert.IsType<ForbidResult>(await controller.Edit(group.Id, "Changed", null, "Invalid"));
        Assert.Equal("Protected", group.Name);
        Assert.Null(controller.ViewData["Group"]);
        Assert.Null(controller.ViewData["Members"]);
        Assert.Null(controller.ViewData["Invites"]);
    }

    /// <summary>Active delegated management roles retain rosters; editing stays Owner-only.</summary>
    [Theory]
    [InlineData(GroupMember.Roles.Owner)]
    [InlineData(GroupMember.Roles.Manager)]
    [InlineData(GroupMember.Roles.Member)]
    public async Task ActiveMembershipRoleControlsManagementViews(string role)
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var actor = TestDataFixtures.CreateUser(id: "actor");
        db.Users.AddRange(owner, actor);
        var groups = new GroupService(db);
        var group = await groups.CreateGroupAsync(owner.Id, "Group", null);
        await groups.AddMemberAsync(group.Id, owner.Id, actor.Id, role);
        var controller = BuildController(db, actor.Id);

        var listing = Assert.IsType<ViewResult>(await controller.Index());
        var managed = Assert.IsAssignableFrom<IEnumerable<Group>>(listing.Model);
        if (role == GroupMember.Roles.Member)
        {
            Assert.Empty(managed);
            Assert.IsType<ForbidResult>(await controller.Members(group.Id));
        }
        else
        {
            Assert.Equal(group.Id, Assert.Single(managed).Id);
            Assert.IsType<ViewResult>(await controller.Members(group.Id));
        }
        if (role == GroupMember.Roles.Owner)
        {
            Assert.IsType<ViewResult>(await controller.Edit(group.Id));
            Assert.IsType<ViewResult>(await controller.Edit(group.Id, "", null, "Friends"));
        }
        else
        {
            Assert.IsType<ForbidResult>(await controller.Edit(group.Id));
            Assert.IsType<ForbidResult>(await controller.Edit(group.Id, "", null, "Friends"));
        }
    }

    /// <summary>An archived Group is unavailable even to its active Owner and on invalid Edit submissions.</summary>
    [Fact]
    public async Task ArchivedGroupIsUnavailableToActiveOwner()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(owner);
        var group = await new GroupService(db).CreateGroupAsync(owner.Id, "Archived", null);
        group.IsArchived = true;
        await db.SaveChangesAsync();
        var controller = BuildController(db, owner.Id);

        var listing = Assert.IsType<ViewResult>(await controller.Index());
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<Group>>(listing.Model));
        Assert.Empty(Assert.IsType<Dictionary<Guid, int>>(listing.ViewData["MemberCounts"]));
        Assert.IsType<NotFoundResult>(await controller.Members(group.Id));
        Assert.IsType<NotFoundResult>(await controller.Edit(group.Id));
        Assert.IsType<NotFoundResult>(await controller.Edit(group.Id, "Changed", null, "Friends"));
        Assert.IsType<NotFoundResult>(await controller.Edit(group.Id, "", null, ""));
    }

    /// <summary>Use the production services and authenticated Manager account without granting a Group role.</summary>
    private static GroupsController BuildController(ApplicationDbContext db, string userId)
    {
        var controller = new GroupsController(NullLogger<BaseController>.Instance, db,
            new GroupService(db), new InvitationService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(userId, "Manager") };
        return controller;
    }
}
