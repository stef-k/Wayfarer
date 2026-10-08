using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for the InvitationService business logic.
/// </summary>
public class InvitationServiceTests : TestBase
{
    [Fact]
    public async Task Invite_Accept_Flows()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "o");
        var user = TestDataFixtures.CreateUser(id: "u");
        db.Users.AddRange(owner, user);
        await db.SaveChangesAsync();

        var groups = new GroupService(db);
        var invites = new InvitationService(db);
        var g = await groups.CreateGroupAsync(owner.Id, "G1", null);

        // Act - owner invites user
        var inv = await invites.InviteUserAsync(g.Id, owner.Id, user.Id, null);

        // Assert
        Assert.Equal(GroupInvitation.InvitationStatuses.Pending, inv.Status);

        // Act - user accepts
        var member = await invites.AcceptAsync(inv.Token, user.Id);

        // Assert
        Assert.Equal(user.Id, member.UserId);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "InviteAccept"));
    }

    [Fact]
    public async Task Decline_Sets_Status_And_Audit()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "o");
        var user = TestDataFixtures.CreateUser(id: "u");
        db.Users.AddRange(owner, user);
        await db.SaveChangesAsync();

        var groups = new GroupService(db);
        var invites = new InvitationService(db);
        var g = await groups.CreateGroupAsync(owner.Id, "G2", null);

        var inv = await invites.InviteUserAsync(g.Id, owner.Id, user.Id, null);

        // Act
        await invites.DeclineAsync(inv.Token, user.Id);

        // Assert
        var reloaded = await db.GroupInvitations.FirstAsync(i => i.Id == inv.Id);
        Assert.Equal(GroupInvitation.InvitationStatuses.Declined, reloaded.Status);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "InviteDecline"));
    }

    #region InviteUserAsync Tests

    [Fact]
    public async Task InviteUserAsync_ThrowsException_WhenNoInviteeProvided()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var groups = new GroupService(db);
        var invites = new InvitationService(db);
        var g = await groups.CreateGroupAsync(owner.Id, "Test Group", null);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            invites.InviteUserAsync(g.Id, owner.Id, null!, null!));
    }

    [Fact]
    public async Task InviteUserAsync_ThrowsException_WhenNotManagerOrOwner()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var member = TestDataFixtures.CreateUser();
        var invitee = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, member, invitee);
        await db.SaveChangesAsync();

        var groups = new GroupService(db);
        var invites = new InvitationService(db);
        var g = await groups.CreateGroupAsync(owner.Id, "Test Group", null);

        // Add regular member
        await groups.AddMemberAsync(g.Id, owner.Id, member.Id, GroupMember.Roles.Member);

        // Act & Assert - regular member cannot invite
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            invites.InviteUserAsync(g.Id, member.Id, invitee.Id, null));
    }

    [Fact]
    public async Task InviteUserAsync_AllowsManagerToInvite()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var manager = TestDataFixtures.CreateUser();
        var invitee = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, manager, invitee);
        await db.SaveChangesAsync();

        var groups = new GroupService(db);
        var invites = new InvitationService(db);
        var g = await groups.CreateGroupAsync(owner.Id, "Test Group", null);

        // Add manager
        await groups.AddMemberAsync(g.Id, owner.Id, manager.Id, GroupMember.Roles.Manager);

        // Act
        var inv = await invites.InviteUserAsync(g.Id, manager.Id, invitee.Id, null);

        // Assert
        Assert.NotNull(inv);
        Assert.Equal(GroupInvitation.InvitationStatuses.Pending, inv.Status);
    }

    [Fact]
    public async Task InviteUserAsync_RejectsUnknownRecipient()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var groups = new GroupService(db);
        var invites = new InvitationService(db);
        var g = await groups.CreateGroupAsync(owner.Id, "Test Group", null);

        // Act
        await Assert.ThrowsAsync<ArgumentException>(() =>
            invites.InviteUserAsync(g.Id, owner.Id, "unknown-user", null));

        Assert.Empty(await db.GroupInvitations.ToListAsync());
    }

    [Fact]
    public async Task InviteUserAsync_SetsExpirationDate()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var invitee = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, invitee);
        await db.SaveChangesAsync();

        var groups = new GroupService(db);
        var invites = new InvitationService(db);
        var g = await groups.CreateGroupAsync(owner.Id, "Test Group", null);

        var expiresAt = DateTime.UtcNow.AddDays(7);

        // Act
        var inv = await invites.InviteUserAsync(g.Id, owner.Id, invitee.Id, expiresAt);

        // Assert
        Assert.NotNull(inv.ExpiresAt);
        Assert.Equal(expiresAt, inv.ExpiresAt.Value);
    }

    #endregion

    #region AcceptAsync Tests

    [Fact]
    public async Task AcceptAsync_ThrowsException_WhenTokenNotFound()
    {
        // Arrange
        var db = CreateDbContext();
        var invites = new InvitationService(db);

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            invites.AcceptAsync("invalid-token", "user-id"));
    }

    [Fact]
    public async Task AcceptAsync_ThrowsException_WhenAlreadyAccepted()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var user = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, user);
        await db.SaveChangesAsync();

        var groups = new GroupService(db);
        var invites = new InvitationService(db);
        var g = await groups.CreateGroupAsync(owner.Id, "Test Group", null);

        var inv = await invites.InviteUserAsync(g.Id, owner.Id, user.Id, null);
        await invites.AcceptAsync(inv.Token, user.Id);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            invites.AcceptAsync(inv.Token, user.Id));
    }

    [Fact]
    public async Task AcceptAsync_ThrowsException_WhenExpired()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var user = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, user);
        await db.SaveChangesAsync();

        var groups = new GroupService(db);
        var invites = new InvitationService(db);
        var g = await groups.CreateGroupAsync(owner.Id, "Test Group", null);

        // Create expired invitation
        var expiredDate = DateTime.UtcNow.AddDays(-1);
        var inv = await invites.InviteUserAsync(g.Id, owner.Id, user.Id, expiredDate);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            invites.AcceptAsync(inv.Token, user.Id));
    }

    /// <summary>Ordinary re-invitations revive membership without restoring historical management authority.</summary>
    [Theory]
    [InlineData(GroupMember.MembershipStatuses.Left, GroupMember.Roles.Member)]
    [InlineData(GroupMember.MembershipStatuses.Left, GroupMember.Roles.Manager)]
    [InlineData(GroupMember.MembershipStatuses.Removed, GroupMember.Roles.Manager)]
    [InlineData(GroupMember.MembershipStatuses.Left, GroupMember.Roles.Owner)]
    [InlineData(GroupMember.MembershipStatuses.Removed, GroupMember.Roles.Owner)]
    public async Task AcceptAsync_RevivesMembershipAsOrdinaryMember(string status, string role)
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var user = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, user);
        await db.SaveChangesAsync();

        var groups = new GroupService(db);
        var invites = new InvitationService(db);
        var g = await groups.CreateGroupAsync(owner.Id, "Test Group", null);

        var historical = await groups.AddMemberAsync(g.Id, owner.Id, user.Id, role);
        historical.Status = status;
        historical.LeftAt = DateTime.UtcNow;
        historical.OrgPeerVisibilityAccessDisabled = true;
        await db.SaveChangesAsync();

        // Create new invitation
        var inv = await invites.InviteUserAsync(g.Id, owner.Id, user.Id, null);

        // Act
        var member = await invites.AcceptAsync(inv.Token, user.Id);

        // Assert
        Assert.Equal(GroupMember.MembershipStatuses.Active, member.Status);
        Assert.Equal(GroupMember.Roles.Member, member.Role);
        Assert.Null(member.LeftAt);
        Assert.True(member.OrgPeerVisibilityAccessDisabled);
        db.ChangeTracker.Clear();
        Assert.Equal(GroupMember.Roles.Member, (await db.GroupMembers.SingleAsync(m => m.Id == member.Id)).Role);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            invites.InviteUserAsync(g.Id, user.Id, "another-user", null));
    }

    /// <summary>An obsolete active-member invitation cannot grant membership or publish acceptance.</summary>
    [Theory]
    [InlineData(GroupMember.Roles.Owner)]
    [InlineData(GroupMember.Roles.Manager)]
    public async Task AcceptAsync_RejectsObsoleteActiveMemberInvitation(string role)
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var user = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, user);
        var groups = new GroupService(db);
        var invites = new InvitationService(db);
        var group = await groups.CreateGroupAsync(owner.Id, "Group", null);
        var membership = await groups.AddMemberAsync(group.Id, owner.Id, user.Id, role);
        var invitation = TestDataFixtures.CreateGroupInvitation(group, owner, user);
        db.GroupInvitations.Add(invitation);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => invites.AcceptAsync(invitation.Token, user.Id));

        Assert.Equal(role, membership.Role);
        Assert.Equal(GroupInvitation.InvitationStatuses.Pending, invitation.Status);
        Assert.False(await db.AuditLogs.AnyAsync(a => a.Action == "InviteAccept"));
    }

    /// <summary>A pending invitation cannot revive access after its Group is archived.</summary>
    [Fact]
    public async Task AcceptAsync_RejectsArchivedGroupWithoutMutation()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var user = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, user);
        var groups = new GroupService(db);
        var invites = new InvitationService(db);
        var group = await groups.CreateGroupAsync(owner.Id, "Group", null);
        var invitation = await invites.InviteUserAsync(group.Id, owner.Id, user.Id, null);
        group.IsArchived = true;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => invites.AcceptAsync(invitation.Token, user.Id));

        Assert.Equal(GroupInvitation.InvitationStatuses.Pending, invitation.Status);
        Assert.False(await db.GroupMembers.AnyAsync(m => m.UserId == user.Id));
    }

    #endregion

    #region DeclineAsync Tests

    [Fact]
    public async Task DeclineAsync_ThrowsException_WhenTokenNotFound()
    {
        // Arrange
        var db = CreateDbContext();
        var invites = new InvitationService(db);

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            invites.DeclineAsync("invalid-token", "user-id"));
    }

    [Fact]
    public async Task DeclineAsync_ThrowsException_WhenNotPending()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var user = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, user);
        await db.SaveChangesAsync();

        var groups = new GroupService(db);
        var invites = new InvitationService(db);
        var g = await groups.CreateGroupAsync(owner.Id, "Test Group", null);

        var inv = await invites.InviteUserAsync(g.Id, owner.Id, user.Id, null);
        await invites.AcceptAsync(inv.Token, user.Id);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            invites.DeclineAsync(inv.Token, user.Id));
    }

    [Fact]
    public async Task DeclineAsync_SetsRespondedAt()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var user = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, user);
        await db.SaveChangesAsync();

        var groups = new GroupService(db);
        var invites = new InvitationService(db);
        var g = await groups.CreateGroupAsync(owner.Id, "Test Group", null);

        var inv = await invites.InviteUserAsync(g.Id, owner.Id, user.Id, null);

        // Act
        await invites.DeclineAsync(inv.Token, user.Id);

        // Assert
        var reloaded = await db.GroupInvitations.FirstAsync(i => i.Id == inv.Id);
        Assert.NotNull(reloaded.RespondedAt);
    }

    #endregion

    #region RevokeAsync Tests

    [Fact]
    public async Task RevokeAsync_RevokesInvitation()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var user = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, user);
        await db.SaveChangesAsync();

        var groups = new GroupService(db);
        var invites = new InvitationService(db);
        var g = await groups.CreateGroupAsync(owner.Id, "Test Group", null);

        var inv = await invites.InviteUserAsync(g.Id, owner.Id, user.Id, null);

        // Act
        var result = await invites.RevokeAsync(inv.Id, owner.Id);

        // Assert
        var reloaded = await db.GroupInvitations.FirstAsync(i => i.Id == inv.Id);
        Assert.Equal(GroupInvitation.InvitationStatuses.Revoked, reloaded.Status);
        Assert.Equal(g.Id, result.GroupId);
        Assert.Equal(user.Id, result.InviteeUserId);
    }

    [Fact]
    public async Task RevokeAsync_ThrowsException_WhenNotManagerOrOwner()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var member = TestDataFixtures.CreateUser();
        var invitee = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, member, invitee);
        await db.SaveChangesAsync();

        var groups = new GroupService(db);
        var invites = new InvitationService(db);
        var g = await groups.CreateGroupAsync(owner.Id, "Test Group", null);

        await groups.AddMemberAsync(g.Id, owner.Id, member.Id, GroupMember.Roles.Member);
        var inv = await invites.InviteUserAsync(g.Id, owner.Id, invitee.Id, null);

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            invites.RevokeAsync(inv.Id, member.Id));
    }

    [Fact]
    public async Task RevokeAsync_ThrowsException_WhenInvitationNotFound()
    {
        // Arrange
        var db = CreateDbContext();
        var invites = new InvitationService(db);

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            invites.RevokeAsync(Guid.NewGuid(), "user-id"));
    }

    [Fact]
    public async Task RevokeAsync_CreatesAuditLog()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var user = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, user);
        await db.SaveChangesAsync();

        var groups = new GroupService(db);
        var invites = new InvitationService(db);
        var g = await groups.CreateGroupAsync(owner.Id, "Test Group", null);

        var inv = await invites.InviteUserAsync(g.Id, owner.Id, user.Id, null);

        // Act
        await invites.RevokeAsync(inv.Id, owner.Id);

        // Assert
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "InviteRevoke"));
    }

    #endregion
}
