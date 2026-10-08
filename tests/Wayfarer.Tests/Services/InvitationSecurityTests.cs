using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Recipient and membership lifecycle rules at the production invitation service seam.</summary>
public sealed class InvitationSecurityTests : TestBase
{
    /// <summary>An active Owner or Manager cannot stockpile a self-invitation.</summary>
    [Theory]
    [InlineData(GroupMember.Roles.Owner)]
    [InlineData(GroupMember.Roles.Manager)]
    public async Task ActiveManagementCannotInviteItself(string role)
    {
        var db = CreateDbContext();
        var (group, owner, target) = await SeedAsync(db);
        var actor = role == GroupMember.Roles.Owner ? owner : target;
        await new GroupService(db).AddMemberAsync(group.Id, owner.Id, target.Id, role);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new InvitationService(db).InviteUserAsync(group.Id, actor.Id, actor.Id, null, null));

        Assert.Empty(await db.GroupInvitations.ToListAsync());
        Assert.False(await db.AuditLogs.AnyAsync(a => a.Action == "InviteCreate"));
    }

    /// <summary>An authorized owner cannot send another invitation to an active member of any role.</summary>
    [Theory]
    [InlineData(GroupMember.Roles.Member)]
    [InlineData(GroupMember.Roles.Manager)]
    [InlineData(GroupMember.Roles.Owner)]
    public async Task ActiveMemberCannotReceiveAnotherInvitation(string role)
    {
        var db = CreateDbContext();
        var (group, owner, target) = await SeedAsync(db);
        await new GroupService(db).AddMemberAsync(group.Id, owner.Id, target.Id, role);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new InvitationService(db).InviteUserAsync(group.Id, owner.Id, target.Id, null, null));

        Assert.Empty(await db.GroupInvitations.ToListAsync());
    }

    /// <summary>Null and mismatched recipient IDs never authorize either response, even with the token.</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("someone-else", false)]
    [InlineData("someone-else", true)]
    public async Task WrongRecipientCannotRespond(string? recipient, bool decline)
    {
        var db = CreateDbContext();
        var (group, owner, target) = await SeedAsync(db);
        var invitation = TestDataFixtures.CreateGroupInvitation(group, owner);
        invitation.InviteeUserId = recipient;
        db.GroupInvitations.Add(invitation);
        await db.SaveChangesAsync();
        var service = new InvitationService(db);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => decline
            ? service.DeclineAsync(invitation.Token, target.Id)
            : service.AcceptAsync(invitation.Token, target.Id));

        db.ChangeTracker.Clear();
        var persisted = await db.GroupInvitations.SingleAsync();
        Assert.Equal(GroupInvitation.InvitationStatuses.Pending, persisted.Status);
        Assert.Null(persisted.RespondedAt);
        Assert.Equal(recipient, persisted.InviteeUserId);
        Assert.False(await db.GroupMembers.AnyAsync(m => m.UserId == target.Id));
        Assert.False(await db.AuditLogs.AnyAsync(a => a.Action == "InviteAccept" || a.Action == "InviteDecline"));
    }

    /// <summary>Legacy pending rows need reliable departure evidence and must be newer than that departure.</summary>
    [Theory]
    [InlineData(GroupMember.MembershipStatuses.Removed, "older")]
    [InlineData(GroupMember.MembershipStatuses.Left, "older")]
    [InlineData(GroupMember.MembershipStatuses.Removed, "equal")]
    [InlineData(GroupMember.MembershipStatuses.Removed, "missing")]
    [InlineData(GroupMember.MembershipStatuses.Left, "missing")]
    [InlineData(GroupMember.MembershipStatuses.Removed, "inconsistent")]
    [InlineData(GroupMember.MembershipStatuses.Removed, "future")]
    public async Task StaleOrUnreliableLifecycleCannotRestoreMembership(string status, string evidence)
    {
        var db = CreateDbContext();
        var (group, owner, target) = await SeedAsync(db);
        var member = await new GroupService(db).AddMemberAsync(group.Id, owner.Id, target.Id, GroupMember.Roles.Manager);
        var departure = DateTime.UtcNow.AddHours(-1);
        member.Status = status;
        member.JoinedAt = departure.AddDays(-1);
        member.LeftAt = evidence == "missing" ? null : departure;
        if (evidence == "inconsistent") member.JoinedAt = departure.AddMinutes(1);
        if (evidence == "future") member.LeftAt = DateTime.UtcNow.AddDays(1);
        var invitation = TestDataFixtures.CreateGroupInvitation(group, owner, target);
        invitation.CreatedAt = evidence == "older" ? departure.AddMinutes(-1)
            : evidence == "equal" ? departure : DateTime.UtcNow;
        db.GroupInvitations.Add(invitation);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new InvitationService(db).AcceptAsync(invitation.Token, target.Id));

        db.ChangeTracker.Clear();
        Assert.Equal(status, (await db.GroupMembers.SingleAsync(m => m.UserId == target.Id)).Status);
        Assert.Equal(GroupInvitation.InvitationStatuses.Pending, (await db.GroupInvitations.SingleAsync()).Status);
        Assert.False(await db.AuditLogs.AnyAsync(a => a.Action == "InviteAccept"));
    }

    /// <summary>Removal and leave revoke only this recipient's pending invitations, then allow fresh Member rejoining.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DepartureInvalidatesPendingInvitationsAndFreshRejoinRemainsOrdinary(bool leave)
    {
        var db = CreateDbContext();
        var (group, owner, target) = await SeedAsync(db);
        var groups = new GroupService(db);
        var member = await groups.AddMemberAsync(group.Id, owner.Id, target.Id, GroupMember.Roles.Manager);
        member.OrgPeerVisibilityAccessDisabled = true;
        var stale = TestDataFixtures.CreateGroupInvitation(group, owner, target);
        stale.CreatedAt = DateTime.UtcNow.AddHours(-1);
        var unrelated = TestDataFixtures.CreateGroupInvitation(group, owner, owner);
        var otherGroup = await groups.CreateGroupAsync(owner.Id, "Other", null);
        var otherInvitation = TestDataFixtures.CreateGroupInvitation(otherGroup, owner, target);
        var history = TestDataFixtures.CreateGroupInvitation(group, owner, target);
        history.Status = GroupInvitation.InvitationStatuses.Declined;
        db.GroupInvitations.AddRange(stale, unrelated, otherInvitation, history);
        await db.SaveChangesAsync();

        if (leave) await groups.LeaveGroupAsync(group.Id, target.Id);
        else await groups.RemoveMemberAsync(group.Id, owner.Id, target.Id);

        Assert.Equal(GroupInvitation.InvitationStatuses.Revoked, stale.Status);
        Assert.NotNull(stale.RespondedAt);
        Assert.Equal(GroupInvitation.InvitationStatuses.Pending, unrelated.Status);
        Assert.Equal(GroupInvitation.InvitationStatuses.Pending, otherInvitation.Status);
        Assert.Equal(GroupInvitation.InvitationStatuses.Declined, history.Status);
        var invitations = new InvitationService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => invitations.AcceptAsync(stale.Token, target.Id));
        // A historical row missed by cleanup still cannot bypass the departure timestamp.
        stale.Status = GroupInvitation.InvitationStatuses.Pending;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => invitations.AcceptAsync(stale.Token, target.Id));
        stale.Status = GroupInvitation.InvitationStatuses.Revoked;
        await db.SaveChangesAsync();
        var fresh = await invitations.InviteUserAsync(group.Id, owner.Id, target.Id, null, null);
        var rejoined = await invitations.AcceptAsync(fresh.Token, target.Id);
        Assert.Equal(GroupMember.Roles.Member, rejoined.Role);
        Assert.Equal(GroupMember.MembershipStatuses.Active, rejoined.Status);
        Assert.Null(rejoined.LeftAt);
        Assert.True(rejoined.OrgPeerVisibilityAccessDisabled);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            invitations.InviteUserAsync(group.Id, target.Id, owner.Id, null, null));
    }

    /// <summary>Seeds registered accounts with only the owner actively joined.</summary>
    private static async Task<(Group Group, ApplicationUser Owner, ApplicationUser Target)> SeedAsync(ApplicationDbContext db)
    {
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var target = TestDataFixtures.CreateUser(id: "target");
        db.Users.AddRange(owner, target);
        var group = await new GroupService(db).CreateGroupAsync(owner.Id, "Security", null);
        return (group, owner, target);
    }
}
