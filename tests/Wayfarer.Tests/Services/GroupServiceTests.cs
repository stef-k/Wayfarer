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

    #region CreateGroupAsync Tests

    [Fact]
    public async Task CreateGroupAsync_ThrowsException_WhenOwnerIdEmpty()
    {
        // Arrange
        var db = CreateDbContext();
        var service = new GroupService(db);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateGroupAsync("", "Test Group", null));
    }

    [Fact]
    public async Task CreateGroupAsync_ThrowsException_WhenNameEmpty()
    {
        // Arrange
        var db = CreateDbContext();
        var service = new GroupService(db);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateGroupAsync("user-id", "", null));
    }

    [Fact]
    public async Task CreateGroupAsync_ThrowsException_WhenDuplicateName()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var existingGroup = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Existing Group",
            OwnerUserId = user.Id,
            CreatedAt = DateTime.UtcNow
        };
        db.Groups.Add(existingGroup);
        await db.SaveChangesAsync();

        var service = new GroupService(db);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateGroupAsync(user.Id, "Existing Group", null));
    }

    [Fact]
    public async Task CreateGroupAsync_AllowsSameNameForDifferentOwners()
    {
        // Arrange
        var db = CreateDbContext();
        var user1 = TestDataFixtures.CreateUser();
        var user2 = TestDataFixtures.CreateUser();
        db.Users.AddRange(user1, user2);

        var existingGroup = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Same Name",
            OwnerUserId = user1.Id,
            CreatedAt = DateTime.UtcNow
        };
        db.Groups.Add(existingGroup);
        await db.SaveChangesAsync();

        var service = new GroupService(db);

        // Act
        var group = await service.CreateGroupAsync(user2.Id, "Same Name", null);

        // Assert
        Assert.NotNull(group);
        Assert.Equal("Same Name", group.Name);
    }

    #endregion

    #region UpdateGroupAsync Tests

    [Fact]
    public async Task UpdateGroupAsync_UpdatesGroup_WhenOwner()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Original Name",
            Description = "Original Description",
            OwnerUserId = user.Id,
            CreatedAt = DateTime.UtcNow
        };
        db.Groups.Add(group);

        var membership = new GroupMember
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            UserId = user.Id,
            Role = GroupMember.Roles.Owner,
            Status = GroupMember.MembershipStatuses.Active,
            JoinedAt = DateTime.UtcNow
        };
        db.GroupMembers.Add(membership);
        await db.SaveChangesAsync();

        var service = new GroupService(db);

        // Act
        var updated = await service.UpdateGroupAsync(group.Id, user.Id, "New Name", "New Description");

        // Assert
        Assert.Equal("New Name", updated.Name);
        Assert.Equal("New Description", updated.Description);
    }

    [Fact]
    public async Task UpdateGroupAsync_ThrowsException_WhenNotOwner()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var manager = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, manager);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Test Group",
            OwnerUserId = owner.Id,
            CreatedAt = DateTime.UtcNow
        };
        db.Groups.Add(group);

        var membership = new GroupMember
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            UserId = manager.Id,
            Role = GroupMember.Roles.Manager,
            Status = GroupMember.MembershipStatuses.Active,
            JoinedAt = DateTime.UtcNow
        };
        db.GroupMembers.Add(membership);
        await db.SaveChangesAsync();

        var service = new GroupService(db);

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.UpdateGroupAsync(group.Id, manager.Id, "New Name", null));
    }

    [Fact]
    public async Task UpdateGroupAsync_ThrowsException_WhenGroupNotFound()
    {
        // Arrange
        var db = CreateDbContext();
        var service = new GroupService(db);

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.UpdateGroupAsync(Guid.NewGuid(), "user-id", "Name", null));
    }

    #endregion

    #region DeleteGroupAsync Tests

    [Fact]
    public async Task DeleteGroupAsync_DeletesGroup_WhenOwner()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "To Delete",
            OwnerUserId = user.Id,
            CreatedAt = DateTime.UtcNow
        };
        db.Groups.Add(group);

        var membership = new GroupMember
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            UserId = user.Id,
            Role = GroupMember.Roles.Owner,
            Status = GroupMember.MembershipStatuses.Active,
            JoinedAt = DateTime.UtcNow
        };
        db.GroupMembers.Add(membership);
        await db.SaveChangesAsync();

        var service = new GroupService(db);

        // Act
        await service.DeleteGroupAsync(group.Id, user.Id);

        // Assert
        Assert.Null(await db.Groups.FindAsync(group.Id));
    }

    [Fact]
    public async Task DeleteGroupAsync_ThrowsException_WhenNotOwner()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var member = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, member);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Test Group",
            OwnerUserId = owner.Id,
            CreatedAt = DateTime.UtcNow
        };
        db.Groups.Add(group);

        var membership = new GroupMember
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            UserId = member.Id,
            Role = GroupMember.Roles.Member,
            Status = GroupMember.MembershipStatuses.Active,
            JoinedAt = DateTime.UtcNow
        };
        db.GroupMembers.Add(membership);
        await db.SaveChangesAsync();

        var service = new GroupService(db);

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.DeleteGroupAsync(group.Id, member.Id));
    }

    #endregion

    #region ListGroupsForUserAsync Tests

    [Fact]
    public async Task ListGroupsForUserAsync_ReturnsOwnedGroups()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Owned Group",
            OwnerUserId = user.Id,
            CreatedAt = DateTime.UtcNow
        };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        var service = new GroupService(db);

        // Act
        var groups = await service.ListGroupsForUserAsync(user.Id);

        // Assert
        Assert.Single(groups);
        Assert.Equal("Owned Group", groups[0].Name);
    }

    [Fact]
    public async Task ListGroupsForUserAsync_ExcludesInactiveMemberships()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var member = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, member);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Left Group",
            OwnerUserId = owner.Id,
            CreatedAt = DateTime.UtcNow
        };
        db.Groups.Add(group);

        var membership = new GroupMember
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            UserId = member.Id,
            Role = GroupMember.Roles.Member,
            Status = GroupMember.MembershipStatuses.Left,
            JoinedAt = DateTime.UtcNow
        };
        db.GroupMembers.Add(membership);
        await db.SaveChangesAsync();

        var service = new GroupService(db);

        // Act
        var groups = await service.ListGroupsForUserAsync(member.Id);

        // Assert
        Assert.Empty(groups);
    }

    #endregion

    #region RemoveMemberAsync Tests

    [Fact]
    public async Task RemoveMemberAsync_SetsMembershipToRemoved()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var memberToRemove = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, memberToRemove);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Test Group",
            OwnerUserId = owner.Id,
            CreatedAt = DateTime.UtcNow
        };
        db.Groups.Add(group);

        db.GroupMembers.AddRange(
            new GroupMember
            {
                Id = Guid.NewGuid(),
                GroupId = group.Id,
                UserId = owner.Id,
                Role = GroupMember.Roles.Owner,
                Status = GroupMember.MembershipStatuses.Active,
                JoinedAt = DateTime.UtcNow
            },
            new GroupMember
            {
                Id = Guid.NewGuid(),
                GroupId = group.Id,
                UserId = memberToRemove.Id,
                Role = GroupMember.Roles.Member,
                Status = GroupMember.MembershipStatuses.Active,
                JoinedAt = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        var service = new GroupService(db);

        // Act
        await service.RemoveMemberAsync(group.Id, owner.Id, memberToRemove.Id);

        // Assert
        var membership = db.GroupMembers.FirstOrDefault(m => m.UserId == memberToRemove.Id);
        Assert.NotNull(membership);
        Assert.Equal(GroupMember.MembershipStatuses.Removed, membership.Status);
    }

    [Fact]
    public async Task RemoveMemberAsync_ThrowsException_WhenRemovingLastManagerFromOrganization()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        db.Users.Add(owner);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Organization",
            GroupType = "Organization",
            OwnerUserId = owner.Id,
            CreatedAt = DateTime.UtcNow
        };
        db.Groups.Add(group);

        var membership = new GroupMember
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            UserId = owner.Id,
            Role = GroupMember.Roles.Owner,
            Status = GroupMember.MembershipStatuses.Active,
            JoinedAt = DateTime.UtcNow
        };
        db.GroupMembers.Add(membership);
        await db.SaveChangesAsync();

        var service = new GroupService(db);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RemoveMemberAsync(group.Id, owner.Id, owner.Id));
    }

    #endregion

    #region LeaveGroupAsync Tests

    [Fact]
    public async Task LeaveGroupAsync_SetsMembershipToLeft()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var member = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, member);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Test Group",
            OwnerUserId = owner.Id,
            CreatedAt = DateTime.UtcNow
        };
        db.Groups.Add(group);

        db.GroupMembers.AddRange(
            new GroupMember
            {
                Id = Guid.NewGuid(),
                GroupId = group.Id,
                UserId = owner.Id,
                Role = GroupMember.Roles.Owner,
                Status = GroupMember.MembershipStatuses.Active,
                JoinedAt = DateTime.UtcNow
            },
            new GroupMember
            {
                Id = Guid.NewGuid(),
                GroupId = group.Id,
                UserId = member.Id,
                Role = GroupMember.Roles.Member,
                Status = GroupMember.MembershipStatuses.Active,
                JoinedAt = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        var service = new GroupService(db);

        // Act
        await service.LeaveGroupAsync(group.Id, member.Id);

        // Assert
        var membership = db.GroupMembers.FirstOrDefault(m => m.UserId == member.Id);
        Assert.NotNull(membership);
        Assert.Equal(GroupMember.MembershipStatuses.Left, membership.Status);
    }

    [Fact]
    public async Task LeaveGroupAsync_ThrowsException_WhenLastManagerLeavesOrganization()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        db.Users.Add(owner);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Organization",
            GroupType = "Organization",
            OwnerUserId = owner.Id,
            CreatedAt = DateTime.UtcNow
        };
        db.Groups.Add(group);

        var membership = new GroupMember
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            UserId = owner.Id,
            Role = GroupMember.Roles.Owner,
            Status = GroupMember.MembershipStatuses.Active,
            JoinedAt = DateTime.UtcNow
        };
        db.GroupMembers.Add(membership);
        await db.SaveChangesAsync();

        var service = new GroupService(db);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LeaveGroupAsync(group.Id, owner.Id));
    }

    #endregion

    #region Auto-Delete Empty Groups Tests

    [Fact]
    public async Task LeaveGroupAsync_DeletesGroup_WhenLastMemberLeavesAndFeatureEnabled()
    {
        // Arrange
        var db = CreateDbContext();
        var settings = new ApplicationSettings
        {
            Id = 1,
            AutoDeleteEmptyGroups = true
        };
        db.ApplicationSettings.Add(settings);

        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "To Auto-Delete",
            OwnerUserId = user.Id,
            CreatedAt = DateTime.UtcNow
        };
        db.Groups.Add(group);

        var membership = new GroupMember
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            UserId = user.Id,
            Role = GroupMember.Roles.Owner,
            Status = GroupMember.MembershipStatuses.Active,
            JoinedAt = DateTime.UtcNow
        };
        db.GroupMembers.Add(membership);
        await db.SaveChangesAsync();

        var service = new GroupService(db);

        // Act
        await service.LeaveGroupAsync(group.Id, user.Id);

        // Assert
        Assert.Null(await db.Groups.FindAsync(group.Id));
    }

    [Fact]
    public async Task LeaveGroupAsync_KeepsGroup_WhenFeatureDisabled()
    {
        // Arrange
        var db = CreateDbContext();
        var settings = new ApplicationSettings
        {
            Id = 1,
            AutoDeleteEmptyGroups = false
        };
        db.ApplicationSettings.Add(settings);

        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Should Keep",
            OwnerUserId = user.Id,
            CreatedAt = DateTime.UtcNow
        };
        db.Groups.Add(group);

        var membership = new GroupMember
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            UserId = user.Id,
            Role = GroupMember.Roles.Owner,
            Status = GroupMember.MembershipStatuses.Active,
            JoinedAt = DateTime.UtcNow
        };
        db.GroupMembers.Add(membership);
        await db.SaveChangesAsync();

        var service = new GroupService(db);

        // Act
        await service.LeaveGroupAsync(group.Id, user.Id);

        // Assert
        Assert.NotNull(await db.Groups.FindAsync(group.Id));
    }

    #endregion
}
