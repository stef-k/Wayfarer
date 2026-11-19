using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>
/// Service for managing groups and group memberships.
/// </summary>
public interface IGroupService
{
    Task<Group> CreateGroupAsync(string ownerUserId, string name, string? description, CancellationToken ct = default);
    /// <summary>Creates a typed Group in the same transaction as ownership and audit.</summary>
    Task<Group> CreateGroupAsync(string ownerUserId, string name, string? description, string? groupType, CancellationToken ct = default);
    /// <summary>Applies a Group type and text edit atomically.</summary>
    Task<(Group Group, bool TypeChanged)> UpdateGroupAsync(Guid groupId, string actorUserId, string name,
        string? description, string? groupType, CancellationToken ct = default);
    Task<Group> UpdateGroupAsync(Guid groupId, string actorUserId, string name, string? description, CancellationToken ct = default);
    Task DeleteGroupAsync(Guid groupId, string actorUserId, CancellationToken ct = default);

    Task<IReadOnlyList<Group>> ListGroupsForUserAsync(string userId, CancellationToken ct = default);

    Task<GroupMember> AddMemberAsync(Guid groupId, string actorUserId, string targetUserId, string role, CancellationToken ct = default);
    Task RemoveMemberAsync(Guid groupId, string actorUserId, string targetUserId, CancellationToken ct = default);
    Task LeaveGroupAsync(Guid groupId, string userId, CancellationToken ct = default);
}

