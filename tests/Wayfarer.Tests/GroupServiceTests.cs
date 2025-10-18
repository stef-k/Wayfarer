using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests;

public class GroupServiceTests
{
    private static ApplicationDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(opts, new ServiceCollection().BuildServiceProvider());
    }

    [Fact]
    public async Task CreateGroup_CreatesOwnerMembership()
    {
        using var db = MakeDb();
        db.Users.Add(new ApplicationUser { Id = "owner1", UserName = "owner1" });
        await db.SaveChangesAsync();

        var svc = new GroupService(db);
        var g = await svc.CreateGroupAsync("owner1", "My Group", null);

        Assert.NotEqual(Guid.Empty, g.Id);
        Assert.Equal("owner1", g.OwnerUserId);
        Assert.True(await db.GroupMembers.AnyAsync(m => m.GroupId == g.Id && m.UserId == "owner1" && m.Role == GroupMember.Roles.Owner));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "GroupCreate"));
    }

    [Fact]
    public async Task AddMember_RequiresManagerOrOwner()
    {
        using var db = MakeDb();
        var owner = new ApplicationUser { Id = "o", UserName = "o" };
        var other = new ApplicationUser { Id = "u2", UserName = "u2" };
        var actor = new ApplicationUser { Id = "actor", UserName = "actor" };
        db.Users.AddRange(owner, other, actor);
        await db.SaveChangesAsync();

        var svc = new GroupService(db);
        var g = await svc.CreateGroupAsync(owner.Id, "G", null);

        // actor is not member -> should fail
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AddMemberAsync(g.Id, actor.Id, other.Id, GroupMember.Roles.Member));

        // make actor manager and retry
        db.GroupMembers.Add(new GroupMember
        {
            Id = Guid.NewGuid(), GroupId = g.Id, UserId = actor.Id, Role = GroupMember.Roles.Manager,
            Status = GroupMember.MembershipStatuses.Active, JoinedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var m = await svc.AddMemberAsync(g.Id, actor.Id, other.Id, GroupMember.Roles.Member);
        Assert.Equal(other.Id, m.UserId);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "MemberAdd"));
    }
}

