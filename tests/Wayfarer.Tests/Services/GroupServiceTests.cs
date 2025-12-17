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

    /// <summary>
    /// Verifies that a Manager cannot assign Owner role - only Owners can do this.
    /// This prevents privilege escalation attacks.
    /// </summary>
    [Fact]
    public async Task AddMember_ManagerCannotAssignOwnerRole()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var manager = TestDataFixtures.CreateUser(id: "manager");
        var newUser = TestDataFixtures.CreateUser(id: "newuser");
        db.Users.AddRange(owner, manager, newUser);
        await db.SaveChangesAsync();

        var svc = new GroupService(db);
        var g = await svc.CreateGroupAsync(owner.Id, "TestGroup", null);

        // Make manager a Manager role member
        var membership = TestDataFixtures.CreateGroupMember(g, manager, GroupMember.Roles.Manager);
        db.GroupMembers.Add(membership);
        await db.SaveChangesAsync();

        // Act & Assert - Manager trying to assign Owner role should fail
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.AddMemberAsync(g.Id, manager.Id, newUser.Id, GroupMember.Roles.Owner));

        Assert.Contains("Managers can only assign the 'Member' role", ex.Message);
    }

    /// <summary>
    /// Verifies that a Manager cannot assign Manager role - only Owners can do this.
    /// This prevents privilege escalation attacks.
    /// </summary>
    [Fact]
    public async Task AddMember_ManagerCannotAssignManagerRole()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var manager = TestDataFixtures.CreateUser(id: "manager");
        var newUser = TestDataFixtures.CreateUser(id: "newuser");
        db.Users.AddRange(owner, manager, newUser);
        await db.SaveChangesAsync();

        var svc = new GroupService(db);
        var g = await svc.CreateGroupAsync(owner.Id, "TestGroup", null);

        // Make manager a Manager role member
        var membership = TestDataFixtures.CreateGroupMember(g, manager, GroupMember.Roles.Manager);
        db.GroupMembers.Add(membership);
        await db.SaveChangesAsync();

        // Act & Assert - Manager trying to assign Manager role should fail
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.AddMemberAsync(g.Id, manager.Id, newUser.Id, GroupMember.Roles.Manager));

        Assert.Contains("Managers can only assign the 'Member' role", ex.Message);
    }

    /// <summary>
    /// Verifies that the actual Owner (via Group.OwnerUserId) can assign Owner role.
    /// </summary>
    [Fact]
    public async Task AddMember_ActualOwnerCanAssignOwnerRole()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var newUser = TestDataFixtures.CreateUser(id: "newuser");
        db.Users.AddRange(owner, newUser);
        await db.SaveChangesAsync();

        var svc = new GroupService(db);
        var g = await svc.CreateGroupAsync(owner.Id, "TestGroup", null);

        // Act - Owner should be able to assign Owner role
        var m = await svc.AddMemberAsync(g.Id, owner.Id, newUser.Id, GroupMember.Roles.Owner);

        // Assert
        Assert.Equal(GroupMember.Roles.Owner, m.Role);
    }

    /// <summary>
    /// Verifies that the actual Owner (via Group.OwnerUserId) can assign Manager role.
    /// </summary>
    [Fact]
    public async Task AddMember_ActualOwnerCanAssignManagerRole()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var newUser = TestDataFixtures.CreateUser(id: "newuser");
        db.Users.AddRange(owner, newUser);
        await db.SaveChangesAsync();

        var svc = new GroupService(db);
        var g = await svc.CreateGroupAsync(owner.Id, "TestGroup", null);

        // Act - Owner should be able to assign Manager role
        var m = await svc.AddMemberAsync(g.Id, owner.Id, newUser.Id, GroupMember.Roles.Manager);

        // Assert
        Assert.Equal(GroupMember.Roles.Manager, m.Role);
    }

    /// <summary>
    /// Verifies that a user with Owner role in GroupMembers (but not the actual Group.OwnerUserId)
    /// can assign Owner and Manager roles.
    /// </summary>
    [Fact]
    public async Task AddMember_MemberWithOwnerRoleCanAssignManagerRole()
    {
        // Arrange
        var db = CreateDbContext();
        var actualOwner = TestDataFixtures.CreateUser(id: "actual-owner");
        var delegatedOwner = TestDataFixtures.CreateUser(id: "delegated-owner");
        var newUser = TestDataFixtures.CreateUser(id: "newuser");
        db.Users.AddRange(actualOwner, delegatedOwner, newUser);
        await db.SaveChangesAsync();

        var svc = new GroupService(db);
        var g = await svc.CreateGroupAsync(actualOwner.Id, "TestGroup", null);

        // Add delegated owner with Owner role in GroupMembers
        var delegatedOwnerMembership = TestDataFixtures.CreateGroupMember(g, delegatedOwner, GroupMember.Roles.Owner);
        db.GroupMembers.Add(delegatedOwnerMembership);
        await db.SaveChangesAsync();

        // Act - Delegated owner should be able to assign Manager role
        var m = await svc.AddMemberAsync(g.Id, delegatedOwner.Id, newUser.Id, GroupMember.Roles.Manager);

        // Assert
        Assert.Equal(GroupMember.Roles.Manager, m.Role);
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

    /// <summary>
    /// Verifies that removing the owner from an Organization group when they are the LAST member
    /// is allowed (no exception thrown). The group will be automatically deleted.
    /// </summary>
    [Fact]
    public async Task RemoveMemberAsync_AllowsRemovingOwner_WhenOwnerIsLastMemberInOrganization()
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

        // Act - Should NOT throw because owner is the last member
        await service.RemoveMemberAsync(group.Id, owner.Id, owner.Id);

        // Assert - Group should be deleted since it's now empty
        Assert.Null(await db.Groups.FindAsync(group.Id));
    }

    /// <summary>
    /// Verifies that removing the owner from an Organization group throws an exception
    /// when there are other members but no Manager to transfer ownership to.
    /// </summary>
    [Fact]
    public async Task RemoveMemberAsync_ThrowsException_WhenRemovingOwnerWithoutManagerSuccessorInOrganization()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var regularMember = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, regularMember);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Organization",
            GroupType = "Organization",
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
                UserId = regularMember.Id,
                Role = GroupMember.Roles.Member, // Regular member, not a Manager
                Status = GroupMember.MembershipStatuses.Active,
                JoinedAt = DateTime.UtcNow.AddMinutes(1)
            });
        await db.SaveChangesAsync();

        var service = new GroupService(db);

        // Act & Assert - Should throw because there are other members but no Manager to transfer to
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

    /// <summary>
    /// Verifies that the owner can leave an Organization group when they are the LAST member.
    /// The group will be automatically deleted.
    /// </summary>
    [Fact]
    public async Task LeaveGroupAsync_AllowsOwnerToLeave_WhenOwnerIsLastMemberInOrganization()
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

        // Act - Should NOT throw because owner is the last member
        await service.LeaveGroupAsync(group.Id, owner.Id);

        // Assert - Group should be deleted since it's now empty
        Assert.Null(await db.Groups.FindAsync(group.Id));
    }

    /// <summary>
    /// Verifies that the owner cannot leave an Organization group when there are other members
    /// but no Manager to transfer ownership to.
    /// </summary>
    [Fact]
    public async Task LeaveGroupAsync_ThrowsException_WhenOwnerLeavesWithoutManagerSuccessorInOrganization()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var regularMember = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, regularMember);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Organization",
            GroupType = "Organization",
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
                UserId = regularMember.Id,
                Role = GroupMember.Roles.Member, // Regular member, not a Manager
                Status = GroupMember.MembershipStatuses.Active,
                JoinedAt = DateTime.UtcNow.AddMinutes(1)
            });
        await db.SaveChangesAsync();

        var service = new GroupService(db);

        // Act & Assert - Should throw because there are other members but no Manager to transfer to
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LeaveGroupAsync(group.Id, owner.Id));
    }

    #endregion

    #region Auto-Delete Empty Groups Tests

    /// <summary>
    /// Verifies that a group is automatically deleted when the last member leaves.
    /// Empty groups are always deleted to prevent orphaned data.
    /// </summary>
    [Fact]
    public async Task LeaveGroupAsync_DeletesGroup_WhenLastMemberLeaves()
    {
        // Arrange
        var db = CreateDbContext();
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

    #endregion

    #region Ownership Transfer Tests

    /// <summary>
    /// Verifies that ownership is transferred to a Manager when the owner leaves an Organization group.
    /// </summary>
    [Fact]
    public async Task LeaveGroupAsync_TransfersOwnershipToManager_WhenOwnerLeavesOrganization()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var manager = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, manager);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Organization",
            GroupType = "Organization",
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
                UserId = manager.Id,
                Role = GroupMember.Roles.Manager,
                Status = GroupMember.MembershipStatuses.Active,
                JoinedAt = DateTime.UtcNow.AddMinutes(1)
            });
        await db.SaveChangesAsync();

        var service = new GroupService(db);

        // Act
        await service.LeaveGroupAsync(group.Id, owner.Id);

        // Assert
        var updatedGroup = await db.Groups.FindAsync(group.Id);
        Assert.NotNull(updatedGroup);
        Assert.Equal(manager.Id, updatedGroup.OwnerUserId);

        var ownerMembership = db.GroupMembers.FirstOrDefault(m => m.UserId == owner.Id && m.GroupId == group.Id);
        Assert.NotNull(ownerMembership);
        Assert.Equal(GroupMember.MembershipStatuses.Left, ownerMembership.Status);
        Assert.Equal(GroupMember.Roles.Member, ownerMembership.Role);

        var managerMembership = db.GroupMembers.FirstOrDefault(m => m.UserId == manager.Id && m.GroupId == group.Id);
        Assert.NotNull(managerMembership);
        Assert.Equal(GroupMember.Roles.Owner, managerMembership.Role);
    }

    /// <summary>
    /// Verifies that ownership is transferred to the next member (by JoinedAt) when the owner leaves a Friends/Family group.
    /// </summary>
    [Fact]
    public async Task LeaveGroupAsync_TransfersOwnershipToNextMember_WhenOwnerLeavesFriendsGroup()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var member1 = TestDataFixtures.CreateUser();
        var member2 = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, member1, member2);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Friends Group",
            GroupType = "Friends",
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
                UserId = member1.Id,
                Role = GroupMember.Roles.Member,
                Status = GroupMember.MembershipStatuses.Active,
                JoinedAt = DateTime.UtcNow.AddMinutes(1) // Joined first among non-owners
            },
            new GroupMember
            {
                Id = Guid.NewGuid(),
                GroupId = group.Id,
                UserId = member2.Id,
                Role = GroupMember.Roles.Member,
                Status = GroupMember.MembershipStatuses.Active,
                JoinedAt = DateTime.UtcNow.AddMinutes(2)
            });
        await db.SaveChangesAsync();

        var service = new GroupService(db);

        // Act
        await service.LeaveGroupAsync(group.Id, owner.Id);

        // Assert - Ownership should transfer to member1 (earliest JoinedAt among non-owners)
        var updatedGroup = await db.Groups.FindAsync(group.Id);
        Assert.NotNull(updatedGroup);
        Assert.Equal(member1.Id, updatedGroup.OwnerUserId);

        var member1Membership = db.GroupMembers.FirstOrDefault(m => m.UserId == member1.Id && m.GroupId == group.Id);
        Assert.NotNull(member1Membership);
        Assert.Equal(GroupMember.Roles.Owner, member1Membership.Role);
    }

    /// <summary>
    /// Verifies that ownership is transferred when the owner is removed from an Organization group.
    /// </summary>
    [Fact]
    public async Task RemoveMemberAsync_TransfersOwnershipToManager_WhenOwnerRemovedFromOrganization()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var manager = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, manager);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Organization",
            GroupType = "Organization",
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
                UserId = manager.Id,
                Role = GroupMember.Roles.Manager,
                Status = GroupMember.MembershipStatuses.Active,
                JoinedAt = DateTime.UtcNow.AddMinutes(1)
            });
        await db.SaveChangesAsync();

        var service = new GroupService(db);

        // Act
        await service.RemoveMemberAsync(group.Id, owner.Id, owner.Id);

        // Assert
        var updatedGroup = await db.Groups.FindAsync(group.Id);
        Assert.NotNull(updatedGroup);
        Assert.Equal(manager.Id, updatedGroup.OwnerUserId);

        var ownerMembership = db.GroupMembers.FirstOrDefault(m => m.UserId == owner.Id && m.GroupId == group.Id);
        Assert.NotNull(ownerMembership);
        Assert.Equal(GroupMember.MembershipStatuses.Removed, ownerMembership.Status);

        var managerMembership = db.GroupMembers.FirstOrDefault(m => m.UserId == manager.Id && m.GroupId == group.Id);
        Assert.NotNull(managerMembership);
        Assert.Equal(GroupMember.Roles.Owner, managerMembership.Role);
    }

    /// <summary>
    /// Verifies that the ownership transfer creates an audit log entry.
    /// </summary>
    [Fact]
    public async Task LeaveGroupAsync_CreatesAuditLog_WhenOwnershipTransferred()
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
            GroupType = "Organization",
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
                UserId = manager.Id,
                Role = GroupMember.Roles.Manager,
                Status = GroupMember.MembershipStatuses.Active,
                JoinedAt = DateTime.UtcNow.AddMinutes(1)
            });
        await db.SaveChangesAsync();

        var service = new GroupService(db);

        // Act
        await service.LeaveGroupAsync(group.Id, owner.Id);

        // Assert - Check for ownership transfer audit log
        var transferAudit = db.AuditLogs.FirstOrDefault(a => a.Action == "OwnershipTransferOnLeave");
        Assert.NotNull(transferAudit);
        Assert.Contains(owner.Id, transferAudit.Details);
        Assert.Contains(manager.Id, transferAudit.Details);
    }

    #endregion
}
