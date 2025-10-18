using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests;

public class InvitationServiceTests
{
    private static ApplicationDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(opts, new ServiceCollection().BuildServiceProvider());
    }

    [Fact]
    public async Task Invite_Accept_Flows()
    {
        using var db = MakeDb();
        var owner = new ApplicationUser { Id = "o", UserName = "o", DisplayName = "o" };
        var user = new ApplicationUser { Id = "u", UserName = "u", DisplayName = "u" };
        db.Users.AddRange(owner, user);
        await db.SaveChangesAsync();

        var groups = new GroupService(db);
        var invites = new InvitationService(db);
        var g = await groups.CreateGroupAsync(owner.Id, "G1", null);

        // owner invites user
        var inv = await invites.InviteUserAsync(g.Id, owner.Id, user.Id, null, null);
        Assert.Equal(GroupInvitation.InvitationStatuses.Pending, inv.Status);

        // user accepts
        var member = await invites.AcceptAsync(inv.Token, user.Id);
        Assert.Equal(user.Id, member.UserId);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "InviteAccept"));
    }

    [Fact]
    public async Task Decline_Sets_Status_And_Audit()
    {
        using var db = MakeDb();
        var owner = new ApplicationUser { Id = "o", UserName = "o", DisplayName = "o" };
        var user = new ApplicationUser { Id = "u", UserName = "u", DisplayName = "u" };
        db.Users.AddRange(owner, user);
        await db.SaveChangesAsync();

        var groups = new GroupService(db);
        var invites = new InvitationService(db);
        var g = await groups.CreateGroupAsync(owner.Id, "G2", null);

        var inv = await invites.InviteUserAsync(g.Id, owner.Id, user.Id, null, null);
        await invites.DeclineAsync(inv.Token, user.Id);

        var reloaded = await db.GroupInvitations.FirstAsync(i => i.Id == inv.Id);
        Assert.Equal(GroupInvitation.InvitationStatuses.Declined, reloaded.Status);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "InviteDecline"));
    }
}