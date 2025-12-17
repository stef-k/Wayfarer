using System.Data;
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

    /// <summary>
    /// Adds a new member to a group with the specified role.
    /// Requires Owner or Manager permissions for the actor.
    /// Role assignment restrictions:
    /// - Only the actual Owner (Group.OwnerUserId) or someone with Owner role in GroupMembers can assign "Manager" or "Owner" roles.
    /// - Managers can only assign the "Member" role.
    /// Uses database-level unique constraint on (GroupId, UserId) to prevent race conditions
    /// where concurrent requests might pass the application-level check.
    /// </summary>
    /// <param name="groupId">The group to add the member to.</param>
    /// <param name="actorUserId">The user performing the action (must be Owner or Manager).</param>
    /// <param name="targetUserId">The user being added to the group.</param>
    /// <param name="role">The role to assign. Must be one of: Owner, Manager, Member. Defaults to Member if empty. Managers can only assign "Member" role.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created GroupMember entity.</returns>
    /// <exception cref="ArgumentException">Thrown when an invalid role is specified or when a Manager tries to assign Owner/Manager role.</exception>
    /// <exception cref="InvalidOperationException">Thrown when user is already a member (detected at application or database level).</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the actor lacks required permissions.</exception>
    public async Task<GroupMember> AddMemberAsync(Guid groupId, string actorUserId, string targetUserId, string role, CancellationToken ct = default)
    {
        await EnsureOwnerOrManagerAsync(groupId, actorUserId, requireOwner: false, ct);

        // Validate role if provided
        var validRoles = new[] { GroupMember.Roles.Owner, GroupMember.Roles.Manager, GroupMember.Roles.Member };
        var assignedRole = string.IsNullOrWhiteSpace(role) ? GroupMember.Roles.Member : role;
        if (!validRoles.Contains(assignedRole, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Invalid role '{role}'. Valid roles are: {string.Join(", ", validRoles)}", nameof(role));
        }

        // Check if actor is an Owner (either actual owner via Group.OwnerUserId or has Owner role in GroupMembers)
        var isActualOwner = await _db.Groups.AsNoTracking()
            .AnyAsync(g => g.Id == groupId && g.OwnerUserId == actorUserId, ct);
        var hasOwnerRole = await _db.GroupMembers.AsNoTracking()
            .AnyAsync(m => m.GroupId == groupId && m.UserId == actorUserId
                        && m.Status == GroupMember.MembershipStatuses.Active
                        && m.Role == GroupMember.Roles.Owner, ct);
        var actorIsOwner = isActualOwner || hasOwnerRole;

        // If actor is not an Owner, they can only assign "Member" role (prevents privilege escalation)
        if (!actorIsOwner && (assignedRole == GroupMember.Roles.Owner || assignedRole == GroupMember.Roles.Manager))
        {
            throw new ArgumentException($"Managers can only assign the 'Member' role. Only Owners can assign '{assignedRole}' role.", nameof(role));
        }

        // Application-level check for existing membership (fast path for most cases)
        var exists = await _db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == targetUserId, ct);
        if (exists) throw new InvalidOperationException("User already a member");

        var member = new GroupMember
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            UserId = targetUserId,
            Role = assignedRole,
            Status = GroupMember.MembershipStatuses.Active,
            JoinedAt = DateTime.UtcNow
        };

        await _db.GroupMembers.AddAsync(member, ct);
        await AddAuditAsync(actorUserId, "MemberAdd", $"Added {targetUserId} to group {groupId}", ct);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Race condition: another request added the same member between our check and save.
            // The database unique constraint on (GroupId, UserId) ensures data integrity.
            throw new InvalidOperationException("User already a member", ex);
        }

        return member;
    }

    /// <summary>
    /// Determines if the DbUpdateException is caused by a unique constraint violation.
    /// Checks for PostgreSQL error code 23505 (unique_violation) in the exception chain.
    /// </summary>
    /// <param name="ex">The exception to check.</param>
    /// <returns>True if this is a unique constraint violation, false otherwise.</returns>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        // PostgreSQL unique constraint violation has SqlState "23505"
        // Check inner exceptions for Npgsql.PostgresException
        var innerException = ex.InnerException;
        while (innerException != null)
        {
            if (innerException is Npgsql.PostgresException pgEx && pgEx.SqlState == "23505")
            {
                return true;
            }
            innerException = innerException.InnerException;
        }
        return false;
    }

    /// <summary>
    /// Removes a member from a group by setting their status to Removed.
    /// Requires Owner or Manager permissions for the actor.
    /// If the removed member is the group owner, ownership is transferred to an eligible successor.
    /// If this was the last active member, the group will be automatically deleted in the same transaction.
    /// </summary>
    /// <remarks>
    /// For Organization groups:
    /// - If owner is removed, ownership transfers to the next Manager (by JoinedAt)
    /// - If no Manager successor exists but there are other members, removal fails
    /// - If owner is the last member, they can be removed and the group is auto-deleted
    ///
    /// For Friends/Family groups:
    /// - If owner is removed, ownership transfers to the next member (by JoinedAt)
    /// - If owner is the last member, they can be removed and the group is auto-deleted
    /// </remarks>
    /// <param name="groupId">The ID of the group to remove the member from.</param>
    /// <param name="actorUserId">The user performing the removal (must be Owner or Manager).</param>
    /// <param name="targetUserId">The user being removed from the group.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="KeyNotFoundException">Thrown when the membership or group is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when removal would leave Organization group without a manager.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the actor lacks required permissions.</exception>
    public async Task RemoveMemberAsync(Guid groupId, string actorUserId, string targetUserId, CancellationToken ct = default)
    {
        // Use RepeatableRead isolation to prevent race conditions during ownership transfer.
        // This ensures the permission check, member lookup, ownership transfer, and status update
        // are all atomic, preventing scenarios where ownership could be transferred to a user
        // being simultaneously removed by another request.
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

        try
        {
            await EnsureOwnerOrManagerAsync(groupId, actorUserId, requireOwner: false, ct);

            var member = await _db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == targetUserId, ct)
                         ?? throw new KeyNotFoundException("Membership not found");

            var group = await _db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, ct)
                        ?? throw new KeyNotFoundException("Group not found");

            // Check if the target user is the actual group owner (using Group.OwnerUserId)
            var isActualOwner = group.OwnerUserId == targetUserId;

            if (isActualOwner)
            {
                // Attempt ownership transfer before removing
                var transferred = await TransferOwnershipAsync(group, member, isLeaving: false, ct);
                if (!transferred)
                {
                    // Check if owner is the last active member - allow removal to trigger group deletion
                    var activeCount = await _db.GroupMembers
                        .CountAsync(m => m.GroupId == groupId && m.Status == GroupMember.MembershipStatuses.Active, ct);
                    if (activeCount > 1)
                    {
                        // There are other members but no eligible successor for Organization group
                        throw new InvalidOperationException("Cannot remove owner from Organization group without an eligible Manager successor. Promote a member to Manager first or delete the group.");
                    }
                    // Owner is last member - proceed with removal, group will be deleted if enabled
                }
            }
            else
            {
                // Non-owner removal: Guard for Organization groups retaining at least one manager/owner
                if (string.Equals(group.GroupType, "Organization", StringComparison.OrdinalIgnoreCase))
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
            }

            member.Status = GroupMember.MembershipStatuses.Removed;
            member.LeftAt = DateTime.UtcNow;
            await AddAuditAsync(actorUserId, "MemberRemove", $"Removed {targetUserId} from group {groupId}", ct);

            // Mark group for deletion before SaveChanges to ensure atomic operation
            await MarkGroupForDeletionIfEmptyAsync(group, actorUserId, ct);

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception)
        {
            // Transaction auto-rollback on dispose
            throw;
        }
    }

    /// <summary>
    /// Allows a user to leave the specified group by setting their membership status to Left.
    /// If the leaving member is the group owner, ownership is transferred to an eligible successor.
    /// If this was the last active member, the group will be automatically deleted in the same transaction.
    /// </summary>
    /// <remarks>
    /// For Organization groups:
    /// - If owner leaves, ownership transfers to the next Manager (by JoinedAt)
    /// - If no Manager successor exists but there are other members, leaving fails
    /// - If owner is the last member, they can leave and the group is auto-deleted
    ///
    /// For Friends/Family groups:
    /// - If owner leaves, ownership transfers to the next member (by JoinedAt)
    /// - If owner is the last member, they can leave and the group is auto-deleted
    /// </remarks>
    /// <param name="groupId">The group to leave.</param>
    /// <param name="userId">The user leaving the group.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="KeyNotFoundException">Thrown when the membership or group is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when leaving would leave Organization group without a manager.</exception>
    public async Task LeaveGroupAsync(Guid groupId, string userId, CancellationToken ct = default)
    {
        // Use RepeatableRead isolation to prevent race conditions during ownership transfer.
        // This ensures the member lookup, ownership transfer check, status update, and auto-delete check
        // are all atomic, preventing scenarios where ownership could be transferred to a user
        // being simultaneously removed by another request.
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

        try
        {
            var member = await _db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId, ct)
                         ?? throw new KeyNotFoundException("Membership not found");

            var group = await _db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, ct)
                        ?? throw new KeyNotFoundException("Group not found");

            // Check if the leaving user is the actual group owner (using Group.OwnerUserId)
            var isActualOwner = group.OwnerUserId == userId;

            if (isActualOwner)
            {
                // Attempt ownership transfer before leaving
                var transferred = await TransferOwnershipAsync(group, member, isLeaving: true, ct);
                if (!transferred)
                {
                    // Check if owner is the last active member - allow leaving to trigger group deletion
                    var activeCount = await _db.GroupMembers
                        .CountAsync(m => m.GroupId == groupId && m.Status == GroupMember.MembershipStatuses.Active, ct);
                    if (activeCount > 1)
                    {
                        // There are other members but no eligible successor for Organization group
                        throw new InvalidOperationException("You are the owner of this Organization group and there is no eligible Manager successor. Promote a member to Manager first or delete the group.");
                    }
                    // Owner is last member - proceed with leaving, group will be deleted if enabled
                }
            }
            else
            {
                // Non-owner leaving: Guard for Organization groups retaining at least one manager/owner
                if (string.Equals(group.GroupType, "Organization", StringComparison.OrdinalIgnoreCase))
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
            }

            member.Status = GroupMember.MembershipStatuses.Left;
            member.LeftAt = DateTime.UtcNow;
            await AddAuditAsync(userId, "MemberLeave", $"User {userId} left group {groupId}", ct);

            // Mark group for deletion before SaveChanges to ensure atomic operation
            await MarkGroupForDeletionIfEmptyAsync(group, userId, ct);

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception)
        {
            // Transaction auto-rollback on dispose
            throw;
        }
    }

    /// <summary>
    /// Transfers group ownership from the current owner to an eligible successor based on group type rules.
    /// For Organization groups: transfers to the next available Manager (by JoinedAt date).
    /// For Friends/Family groups: transfers to the next member (by JoinedAt date).
    /// Updates both Group.OwnerUserId and the membership roles accordingly.
    /// </summary>
    /// <param name="group">The group entity (must be tracked by EF context).</param>
    /// <param name="currentOwnerMember">The current owner's membership record (must be tracked by EF context).</param>
    /// <param name="isLeaving">True if the owner is leaving voluntarily, false if being removed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if ownership was successfully transferred, false if no eligible successor was found.</returns>
    private async Task<bool> TransferOwnershipAsync(Group group, GroupMember currentOwnerMember, bool isLeaving, CancellationToken ct)
    {
        var groupId = group.Id;
        var currentOwnerId = group.OwnerUserId;

        // Find eligible successor based on group type
        GroupMember? successor = null;

        if (string.Equals(group.GroupType, "Organization", StringComparison.OrdinalIgnoreCase))
        {
            // Organization: Must transfer to the next available Manager (by JoinedAt)
            successor = await _db.GroupMembers
                .Where(m => m.GroupId == groupId
                         && m.UserId != currentOwnerId
                         && m.Status == GroupMember.MembershipStatuses.Active
                         && m.Role == GroupMember.Roles.Manager)
                .OrderBy(m => m.JoinedAt)
                .FirstOrDefaultAsync(ct);

            // If no manager found, return false - caller handles the error
            if (successor == null)
            {
                return false;
            }
        }
        else
        {
            // Friends/Family (or any other type): Transfer to next member by JoinedAt
            successor = await _db.GroupMembers
                .Where(m => m.GroupId == groupId
                         && m.UserId != currentOwnerId
                         && m.Status == GroupMember.MembershipStatuses.Active)
                .OrderBy(m => m.JoinedAt)
                .FirstOrDefaultAsync(ct);

            // If no successor found, the owner is the last member - return false to allow deletion
            if (successor == null)
            {
                return false;
            }
        }

        // Demote current owner's membership role to Member before they leave/are removed
        currentOwnerMember.Role = GroupMember.Roles.Member;

        // Promote successor's membership role to Owner
        successor.Role = GroupMember.Roles.Owner;

        // Update the Group.OwnerUserId to the successor
        group.OwnerUserId = successor.UserId;
        group.UpdatedAt = DateTime.UtcNow;

        // Audit the ownership transfer
        var action = isLeaving ? "OwnershipTransferOnLeave" : "OwnershipTransferOnRemoval";
        var details = $"Ownership of group '{group.Name}' transferred from {currentOwnerId} to {successor.UserId}";
        await AddAuditAsync(currentOwnerId, action, details, ct);

        return true;
    }

    /// <summary>
    /// Validates that the actor has Owner or Manager permissions for the specified group.
    /// Checks both the GroupMembers table (for delegated roles) and Group.OwnerUserId (for actual ownership).
    /// First verifies that the group exists before checking permissions.
    /// </summary>
    /// <param name="groupId">The group to check permissions for.</param>
    /// <param name="actorUserId">The user attempting the action.</param>
    /// <param name="requireOwner">If true, only Owner role is accepted; if false, Manager role also allowed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">Thrown when actorUserId is null or empty.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the group does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the user lacks required permissions.</exception>
    private async Task EnsureOwnerOrManagerAsync(Guid groupId, string actorUserId, bool requireOwner, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actorUserId)) throw new ArgumentException("actorUserId required");

        // First check if the group exists
        var group = await _db.Groups.AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group == null) throw new KeyNotFoundException("Group not found");

        // Check if user is the actual group owner via Group.OwnerUserId
        var isActualOwner = group.OwnerUserId == actorUserId;

        // Check membership table for delegated Owner/Manager roles
        var membership = await _db.GroupMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == actorUserId && m.Status == GroupMember.MembershipStatuses.Active, ct);

        var hasOwnerRole = membership?.Role == GroupMember.Roles.Owner;
        var hasManagerRole = membership?.Role == GroupMember.Roles.Manager;

        if (requireOwner)
        {
            // Allow if user is actual owner OR has Owner role in membership
            if (!(isActualOwner || hasOwnerRole)) throw new UnauthorizedAccessException("Owner permissions required");
        }
        else
        {
            // Allow if user is actual owner OR has Owner/Manager role in membership
            if (!(isActualOwner || hasOwnerRole || hasManagerRole)) throw new UnauthorizedAccessException("Manager or Owner permissions required");
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

    /// <summary>
    /// Marks a group for deletion if there are no remaining active members.
    /// This method checks the in-memory state of tracked entities to determine if the group will be empty
    /// after the current changes are saved. The group is marked for deletion (via DbContext.Remove) but
    /// SaveChangesAsync is NOT called - the caller is responsible for calling SaveChangesAsync to commit
    /// all changes atomically in a single transaction.
    /// </summary>
    /// <remarks>
    /// This method considers both:
    /// - Members already saved to the database with Active status
    /// - Members modified in the current DbContext (e.g., status changed to Left/Removed but not yet saved)
    ///
    /// Empty groups are always deleted to prevent orphaned data.
    /// </remarks>
    /// <param name="group">The group entity to check (must be a tracked entity).</param>
    /// <param name="actorUserId">The user performing the action (for audit logging).</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task MarkGroupForDeletionIfEmptyAsync(Group group, string actorUserId, CancellationToken ct)
    {
        // Count active members considering in-memory changes (modified/added entities not yet saved)
        // This ensures we check the state AFTER the member status change but BEFORE SaveChangesAsync
        var activeCount = await CountActiveMembersIncludingPendingChangesAsync(group.Id, ct);
        if (activeCount > 0) return;

        var groupName = group.Name;
        _db.Groups.Remove(group);
        await AddAuditAsync(actorUserId, "GroupDelete", $"Auto-deleted empty group '{groupName}'", ct);
    }

    /// <summary>
    /// Counts active members for a group, considering both database state and pending in-memory changes.
    /// This is necessary because when a member's status is changed to Left/Removed but not yet saved,
    /// a simple database query would still count them as active.
    /// </summary>
    /// <remarks>
    /// The method works by:
    /// 1. Querying the database for the current count of active members
    /// 2. Adjusting for tracked entities that have changed status from Active to non-Active (subtract)
    /// 3. Adjusting for tracked entities that are newly added with Active status (add)
    /// This ensures we get the correct count even when only some members are loaded in the context.
    /// </remarks>
    /// <param name="groupId">The group to count active members for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The count of members that will be active after pending changes are saved.</returns>
    private async Task<int> CountActiveMembersIncludingPendingChangesAsync(Guid groupId, CancellationToken ct)
    {
        // Start with the database count of active members
        var dbActiveCount = await _db.GroupMembers
            .CountAsync(m => m.GroupId == groupId && m.Status == GroupMember.MembershipStatuses.Active, ct);

        // Get tracked GroupMember entities for this group
        var trackedEntries = _db.ChangeTracker.Entries<GroupMember>()
            .Where(e => e.Entity.GroupId == groupId)
            .ToList();

        var adjustment = 0;

        foreach (var entry in trackedEntries)
        {
            var currentStatus = entry.Entity.Status;
            var isCurrentlyActive = currentStatus == GroupMember.MembershipStatuses.Active;

            if (entry.State == EntityState.Added)
            {
                // New member being added - add to count if active
                if (isCurrentlyActive)
                    adjustment++;
            }
            else if (entry.State == EntityState.Modified)
            {
                // Check if status changed from Active to non-Active or vice versa
                var originalStatus = entry.OriginalValues.GetValue<string>(nameof(GroupMember.Status));
                var wasOriginallyActive = originalStatus == GroupMember.MembershipStatuses.Active;

                if (wasOriginallyActive && !isCurrentlyActive)
                {
                    // Was active in DB, now inactive in memory - subtract from count
                    adjustment--;
                }
                else if (!wasOriginallyActive && isCurrentlyActive)
                {
                    // Was inactive in DB, now active in memory - add to count
                    adjustment++;
                }
            }
            else if (entry.State == EntityState.Deleted)
            {
                // Member being hard-deleted - subtract if was active
                var originalStatus = entry.OriginalValues.GetValue<string>(nameof(GroupMember.Status));
                if (originalStatus == GroupMember.MembershipStatuses.Active)
                    adjustment--;
            }
            // EntityState.Unchanged - no adjustment needed, already counted in DB
        }

        return Math.Max(0, dbActiveCount + adjustment);
    }
}
