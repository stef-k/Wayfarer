using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>
/// EF-backed implementation of group and membership operations with basic permission checks and auditing.
/// </summary>
public class GroupService : IGroupService
{
    private readonly ApplicationDbContext _db;

    public GroupService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Group> CreateGroupAsync(string ownerUserId, string name, string? description, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId)) throw new ArgumentException("ownerUserId required");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name required");

        var exists = await _db.Groups.AnyAsync(g => g.OwnerUserId == ownerUserId && g.Name == name, ct);
        if (exists) throw new InvalidOperationException("Group with the same name already exists for owner");

        var group = new Group
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = name,
            Description = description,
            IsArchived = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // owner membership
        var membership = new GroupMember
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            UserId = ownerUserId,
            Role = GroupMember.Roles.Owner,
            Status = GroupMember.MembershipStatuses.Active,
            JoinedAt = DateTime.UtcNow
        };

        await _db.Groups.AddAsync(group, ct);
        await _db.GroupMembers.AddAsync(membership, ct);
        await AddAuditAsync(ownerUserId, "GroupCreate", $"Created group '{name}'", ct);
        await _db.SaveChangesAsync(ct);
        return group;
    }

    public async Task<Group> UpdateGroupAsync(Guid groupId, string actorUserId, string name, string? description, CancellationToken ct = default)
    {
        var group = await _db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, ct) ?? throw new KeyNotFoundException("Group not found");

        await EnsureOwnerOrManagerAsync(groupId, actorUserId, requireOwner: true, ct);

        if (!string.IsNullOrWhiteSpace(name)) group.Name = name;
        group.Description = description;
        group.UpdatedAt = DateTime.UtcNow;

        await AddAuditAsync(actorUserId, "GroupUpdate", $"Updated group '{group.Name}'", ct);
        await _db.SaveChangesAsync(ct);
        return group;
    }

    public async Task DeleteGroupAsync(Guid groupId, string actorUserId, CancellationToken ct = default)
    {
        var group = await _db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, ct) ?? throw new KeyNotFoundException("Group not found");
        await EnsureOwnerOrManagerAsync(groupId, actorUserId, requireOwner: true, ct);

        _db.Groups.Remove(group);
        await AddAuditAsync(actorUserId, "GroupDelete", $"Deleted group '{group.Name}'", ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Group>> ListGroupsForUserAsync(string userId, CancellationToken ct = default)
    {
        var owned = _db.Groups.Where(g => g.OwnerUserId == userId);
        var memberOf = from m in _db.GroupMembers
                       where m.UserId == userId && m.Status == GroupMember.MembershipStatuses.Active
                       join g in _db.Groups on m.GroupId equals g.Id
                       select g;
        var res = await owned.Union(memberOf).Distinct().AsNoTracking().ToListAsync(ct);
        return res;
    }

    public async Task<GroupMember> AddMemberAsync(Guid groupId, string actorUserId, string targetUserId, string role, CancellationToken ct = default)
    {
        await EnsureOwnerOrManagerAsync(groupId, actorUserId, requireOwner: false, ct);

        var exists = await _db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == targetUserId, ct);
        if (exists) throw new InvalidOperationException("User already a member");

        var member = new GroupMember
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            UserId = targetUserId,
            Role = string.IsNullOrWhiteSpace(role) ? GroupMember.Roles.Member : role,
            Status = GroupMember.MembershipStatuses.Active,
            JoinedAt = DateTime.UtcNow
        };

        await _db.GroupMembers.AddAsync(member, ct);
        await AddAuditAsync(actorUserId, "MemberAdd", $"Added {targetUserId} to group {groupId}", ct);
        await _db.SaveChangesAsync(ct);
        return member;
    }

    public async Task RemoveMemberAsync(Guid groupId, string actorUserId, string targetUserId, CancellationToken ct = default)
    {
        await EnsureOwnerOrManagerAsync(groupId, actorUserId, requireOwner: false, ct);

        var member = await _db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == targetUserId, ct)
                     ?? throw new KeyNotFoundException("Membership not found");

        // Guard: Organization groups must always retain at least one manager/owner
        var group = await _db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group != null && string.Equals(group.GroupType, "Organization", StringComparison.OrdinalIgnoreCase))
        {
            var isManagerRole = member.Role == GroupMember.Roles.Owner || member.Role == GroupMember.Roles.Manager;
            if (isManagerRole)
            {
                var activeManagerCount = await _db.GroupMembers
                    .CountAsync(m => m.GroupId == groupId && m.Status == GroupMember.MembershipStatuses.Active && (m.Role == GroupMember.Roles.Owner || m.Role == GroupMember.Roles.Manager), ct);
                if (activeManagerCount <= 1)
                {
                    throw new InvalidOperationException("Cannot remove the last manager from an Organization group.");
                }
            }
        }

        member.Status = GroupMember.MembershipStatuses.Removed;
        member.LeftAt = DateTime.UtcNow;
        await AddAuditAsync(actorUserId, "MemberRemove", $"Removed {targetUserId} from group {groupId}", ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task LeaveGroupAsync(Guid groupId, string userId, CancellationToken ct = default)
    {
        var member = await _db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId, ct)
                     ?? throw new KeyNotFoundException("Membership not found");

        // Guard: Organization groups must always retain at least one manager/owner
        var group = await _db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group != null && string.Equals(group.GroupType, "Organization", StringComparison.OrdinalIgnoreCase))
        {
            var isManagerRole = member.Role == GroupMember.Roles.Owner || member.Role == GroupMember.Roles.Manager;
            if (isManagerRole)
            {
                var activeManagerCount = await _db.GroupMembers
                    .CountAsync(m => m.GroupId == groupId && m.Status == GroupMember.MembershipStatuses.Active && (m.Role == GroupMember.Roles.Owner || m.Role == GroupMember.Roles.Manager), ct);
                if (activeManagerCount <= 1)
                {
                    throw new InvalidOperationException("You are the last manager of this Organization group. Transfer or add another manager before leaving.");
                }
            }
        }

        member.Status = GroupMember.MembershipStatuses.Left;
        member.LeftAt = DateTime.UtcNow;
        await AddAuditAsync(userId, "MemberLeave", $"User {userId} left group {groupId}", ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task EnsureOwnerOrManagerAsync(Guid groupId, string actorUserId, bool requireOwner, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actorUserId)) throw new ArgumentException("actorUserId required");

        var membership = await _db.GroupMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == actorUserId && m.Status == GroupMember.MembershipStatuses.Active, ct);

        var isOwner = membership?.Role == GroupMember.Roles.Owner;
        var isManager = membership?.Role == GroupMember.Roles.Manager;

        if (requireOwner)
        {
            if (!isOwner) throw new UnauthorizedAccessException("Owner permissions required");
        }
        else
        {
            if (!(isOwner || isManager)) throw new UnauthorizedAccessException("Manager or Owner permissions required");
        }
    }

    private async Task AddAuditAsync(string userId, string action, string details, CancellationToken ct)
    {
        var audit = new AuditLog
        {
            UserId = userId,
            Action = action,
            Details = details,
            Timestamp = DateTime.UtcNow
        };
        await _db.AuditLogs.AddAsync(audit, ct);
    }
}
