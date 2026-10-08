using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Regression coverage for membership authority independent of ownership metadata.</summary>
public sealed class GroupAuthorizationTests : TestBase
{
    /// <summary>A departed or missing owner membership cannot restore itself or manage the Group.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(GroupMember.MembershipStatuses.Removed)]
    [InlineData(GroupMember.MembershipStatuses.Left)]
    public async Task InactiveMetadataOwnerCannotManageGroup(string? status)
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var peer = TestDataFixtures.CreateUser(id: "peer");
        db.Users.AddRange(owner, peer, TestDataFixtures.CreateUser(id: "invitee"));
        var groups = new GroupService(db);
        var invitations = new InvitationService(db);
        var group = await groups.CreateGroupAsync(owner.Id, "Group", null);
        await groups.AddMemberAsync(group.Id, owner.Id, peer.Id, GroupMember.Roles.Member);
        var pending = await invitations.InviteUserAsync(group.Id, owner.Id, "invitee", null);
        var membership = await db.GroupMembers.SingleAsync(m => m.GroupId == group.Id && m.UserId == owner.Id);
        if (status == null) db.GroupMembers.Remove(membership);
        else membership.Status = status;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            invitations.InviteUserAsync(group.Id, owner.Id, owner.Id, null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => invitations.RevokeAsync(pending.Id, owner.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            groups.AddMemberAsync(group.Id, owner.Id, "new-manager", GroupMember.Roles.Manager));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => groups.UpdateGroupAsync(group.Id, owner.Id, "Changed", null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => groups.RemoveMemberAsync(group.Id, owner.Id, peer.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => groups.DeleteGroupAsync(group.Id, owner.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => groups.LeaveGroupAsync(group.Id, owner.Id));
        Assert.Empty(await groups.ListGroupsForUserAsync(owner.Id));
        Assert.Equal(owner.Id, group.OwnerUserId);
        Assert.Equal("Group", group.Name);
        Assert.Equal(GroupInvitation.InvitationStatuses.Pending, pending.Status);
        Assert.Equal(GroupMember.MembershipStatuses.Active,
            (await db.GroupMembers.SingleAsync(m => m.UserId == peer.Id)).Status);
    }

    /// <summary>Even a metadata owner needs an active Owner role to assign management privileges.</summary>
    [Theory]
    [InlineData(GroupMember.Roles.Member, GroupMember.Roles.Owner)]
    [InlineData(GroupMember.Roles.Member, GroupMember.Roles.Manager)]
    [InlineData(GroupMember.Roles.Manager, GroupMember.Roles.Owner)]
    [InlineData(GroupMember.Roles.Manager, GroupMember.Roles.Manager)]
    public async Task MetadataOwnershipCannotElevateAssignedRole(string actorRole, string assignedRole)
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(owner);
        var groups = new GroupService(db);
        var group = await groups.CreateGroupAsync(owner.Id, "Group", null);
        (await db.GroupMembers.SingleAsync()).Role = actorRole;
        await db.SaveChangesAsync();

        if (actorRole == GroupMember.Roles.Member)
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                groups.AddMemberAsync(group.Id, owner.Id, "target", assignedRole));
        else
            await Assert.ThrowsAsync<ArgumentException>(() =>
                groups.AddMemberAsync(group.Id, owner.Id, "target", assignedRole));

        Assert.False(await db.GroupMembers.AnyAsync(m => m.UserId == "target"));
    }

    /// <summary>Archiving revokes management and listing even for active elevated members.</summary>
    [Theory]
    [InlineData(GroupMember.Roles.Owner)]
    [InlineData(GroupMember.Roles.Manager)]
    public async Task ArchivedGroupsCannotBeManaged(string role)
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        db.Users.AddRange(owner, TestDataFixtures.CreateUser(id: "invitee"));
        var groups = new GroupService(db);
        var invitations = new InvitationService(db);
        var group = await groups.CreateGroupAsync(owner.Id, "Group", null);
        var pending = await invitations.InviteUserAsync(group.Id, owner.Id, "invitee", null);
        (await db.GroupMembers.SingleAsync()).Role = role;
        group.IsArchived = true;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            invitations.InviteUserAsync(group.Id, owner.Id, owner.Id, null));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => invitations.RevokeAsync(pending.Id, owner.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            groups.AddMemberAsync(group.Id, owner.Id, "target", GroupMember.Roles.Member));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => groups.UpdateGroupAsync(group.Id, owner.Id, "Changed", null));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => groups.RemoveMemberAsync(group.Id, owner.Id, owner.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => groups.DeleteGroupAsync(group.Id, owner.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => groups.LeaveGroupAsync(group.Id, owner.Id));
        Assert.Empty(await groups.ListGroupsForUserAsync(owner.Id));
        Assert.Equal(GroupInvitation.InvitationStatuses.Pending, pending.Status);
    }
}
