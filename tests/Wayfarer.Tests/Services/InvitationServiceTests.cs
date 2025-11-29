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
        var inv = await invites.InviteUserAsync(g.Id, owner.Id, user.Id, null, null);

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

        var inv = await invites.InviteUserAsync(g.Id, owner.Id, user.Id, null, null);

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
            invites.InviteUserAsync(g.Id, owner.Id, null!, null!, null!));
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
            invites.InviteUserAsync(g.Id, member.Id, invitee.Id, null, null));
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
        var inv = await invites.InviteUserAsync(g.Id, manager.Id, invitee.Id, null, null);

        // Assert
        Assert.NotNull(inv);
        Assert.Equal(GroupInvitation.InvitationStatuses.Pending, inv.Status);
    }

    [Fact]
    public async Task InviteUserAsync_CreatesInvitationWithEmail()
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
        var inv = await invites.InviteUserAsync(g.Id, owner.Id, null, "test@example.com", null);

        // Assert
        Assert.NotNull(inv);
        Assert.Equal("test@example.com", inv.InviteeEmail);
        Assert.Null(inv.InviteeUserId);
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
        var inv = await invites.InviteUserAsync(g.Id, owner.Id, invitee.Id, null, expiresAt);

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

        var inv = await invites.InviteUserAsync(g.Id, owner.Id, user.Id, null, null);
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
        var inv = await invites.InviteUserAsync(g.Id, owner.Id, user.Id, null, expiredDate);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            invites.AcceptAsync(inv.Token, user.Id));
    }

    [Fact]
    public async Task AcceptAsync_RevivesLeftMembership()
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

        // Add and then leave
        await groups.AddMemberAsync(g.Id, owner.Id, user.Id, GroupMember.Roles.Member);
        await groups.LeaveGroupAsync(g.Id, user.Id);

        // Create new invitation
        var inv = await invites.InviteUserAsync(g.Id, owner.Id, user.Id, null, null);

        // Act
        var member = await invites.AcceptAsync(inv.Token, user.Id);

        // Assert
        Assert.Equal(GroupMember.MembershipStatuses.Active, member.Status);
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

        var inv = await invites.InviteUserAsync(g.Id, owner.Id, user.Id, null, null);
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

        var inv = await invites.InviteUserAsync(g.Id, owner.Id, user.Id, null, null);

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

        var inv = await invites.InviteUserAsync(g.Id, owner.Id, user.Id, null, null);

        // Act
        await invites.RevokeAsync(inv.Id, owner.Id);

        // Assert
        var reloaded = await db.GroupInvitations.FirstAsync(i => i.Id == inv.Id);
        Assert.Equal(GroupInvitation.InvitationStatuses.Revoked, reloaded.Status);
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
        var inv = await invites.InviteUserAsync(g.Id, owner.Id, invitee.Id, null, null);

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

        var inv = await invites.InviteUserAsync(g.Id, owner.Id, user.Id, null, null);

        // Act
        await invites.RevokeAsync(inv.Id, owner.Id);

        // Assert
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "InviteRevoke"));
    }

    #endregion
}
