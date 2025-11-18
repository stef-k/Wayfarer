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
}
