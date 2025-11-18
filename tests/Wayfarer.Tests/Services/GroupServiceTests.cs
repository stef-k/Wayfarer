using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for the GroupService business logic.
/// </summary>
public class GroupServiceTests : TestBase
{
    [Fact]
    public async Task CreateGroup_CreatesOwnerMembership()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner1");
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var svc = new GroupService(db);

        // Act
        var g = await svc.CreateGroupAsync("owner1", "My Group", null);

        // Assert
        Assert.NotEqual(Guid.Empty, g.Id);
        Assert.Equal("owner1", g.OwnerUserId);
        Assert.True(await db.GroupMembers.AnyAsync(m =>
            m.GroupId == g.Id && m.UserId == "owner1" && m.Role == GroupMember.Roles.Owner));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "GroupCreate"));
    }

    [Fact]
    public async Task AddMember_RequiresManagerOrOwner()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "o");
        var other = TestDataFixtures.CreateUser(id: "u2");
        var actor = TestDataFixtures.CreateUser(id: "actor");
        db.Users.AddRange(owner, other, actor);
        await db.SaveChangesAsync();

        var svc = new GroupService(db);
        var g = await svc.CreateGroupAsync(owner.Id, "G", null);

        // Act & Assert - actor is not member -> should fail
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.AddMemberAsync(g.Id, actor.Id, other.Id, GroupMember.Roles.Member));

        // Arrange - make actor manager and retry
        var membership = TestDataFixtures.CreateGroupMember(g, actor, GroupMember.Roles.Manager);
        db.GroupMembers.Add(membership);
        await db.SaveChangesAsync();

        // Act
        var m = await svc.AddMemberAsync(g.Id, actor.Id, other.Id, GroupMember.Roles.Member);

        // Assert
        Assert.Equal(other.Id, m.UserId);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "MemberAdd"));
    }
}
