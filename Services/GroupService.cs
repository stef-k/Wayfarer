using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>
/// EF-backed implementation of group and membership operations with basic permission checks and auditing.
/// </summary>
public partial class GroupService : IGroupService
{
    private readonly ApplicationDbContext _db;

    public GroupService(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <summary>Creates an untyped Group through the released cookie API.</summary>
    public Task<Group> CreateGroupAsync(string ownerUserId, string name, string? description, CancellationToken ct = default) =>
        CreateGroupAsync(ownerUserId, name, description, groupType: null, ct);

    /// <summary>Commits Group type, Owner membership and audit together for typed Web creation.</summary>
    public async Task<Group> CreateGroupAsync(string ownerUserId, string name, string? description, string? groupType,
        CancellationToken ct = default)
    {
        try { return await CreateAsync(ownerUserId, name, description, groupType, ct); }
        catch (GroupRefusal refusal) when (refusal.Code == "group_name_taken")
        {
            throw new InvalidOperationException("Group with the same name already exists for owner");
        }
        catch (GroupRefusal refusal) when (refusal.Status == 401)
        {
            throw new UnauthorizedAccessException("An active account is required");
        }
    }

    /// <summary>Creates an owned Group while the active account and credential remain valid.</summary>
    internal async Task<Group> CreateAsync(string ownerUserId, string name, string? description, string? groupType,
        CancellationToken ct, Func<CancellationToken, Task<bool>>? bearer = null)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId)) throw new ArgumentException("ownerUserId required");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name required");

        await using var transaction = await GroupWrite.BeginAsync(_db, null, ownerUserId, ct, bearer);
        if (await _db.Groups.AnyAsync(g => g.OwnerUserId == ownerUserId && g.Name == name, ct))
            throw new GroupRefusal(409, "group_name_taken");

        var now = GroupMembershipEpisode.AtStoredPrecision(DateTime.UtcNow);
        var group = new Group
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerUserId, Name = name, Description = description,
            GroupType = groupType, CreatedAt = now, UpdatedAt = now
        };
        _db.Groups.Add(group);
        _db.GroupMembers.Add(new GroupMember
        {
            Id = Guid.NewGuid(), GroupId = group.Id, UserId = ownerUserId,
            Role = GroupMember.Roles.Owner, Status = GroupMember.MembershipStatuses.Active, JoinedAt = now
        });
        await AddAuditAsync(ownerUserId, "GroupCreate", $"Created group '{name}'", ct);
        await SaveNameChangeAsync(transaction, "group_name_taken", ct);
        return group;
    }

    /// <summary>Updates the Group's text while preserving its current type.</summary>
    public async Task<Group> UpdateGroupAsync(Guid groupId, string actorUserId, string name, string? description,
        CancellationToken ct = default) =>
        (await UpdateGroupAsync(groupId, actorUserId, name, description, groupType: null, ct)).Group;

    /// <summary>Applies Web name and type changes together and reports whether to invalidate peer policy.</summary>
    public async Task<(Group Group, bool TypeChanged)> UpdateGroupAsync(Guid groupId, string actorUserId, string name,
        string? description, string? groupType, CancellationToken ct = default)
    {
        await using var transaction = await BeginWebWriteAsync(groupId, actorUserId, ct);
        await EnsureOwnerOrManagerAsync(groupId, actorUserId, requireOwner: true, ct);
        var group = await GroupWrite.FreshAsync(_db, _db.Groups.Where(g => g.Id == groupId), ct)
            ?? throw new KeyNotFoundException("Group not found");

        var typeChanged = groupType is not null && !string.Equals(group.GroupType, groupType, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(name)) group.Name = name;
        if (groupType is not null) group.GroupType = groupType;
        group.Description = description;
        group.UpdatedAt = DateTime.UtcNow;
        await AddAuditAsync(actorUserId, "GroupUpdate", $"Updated group '{group.Name}'", ct);
        await SaveNameChangeAsync(transaction, "group_name_taken", ct);
        return (group, typeChanged);
    }

    /// <summary>Deletes a Group under current Owner membership and the shared write ordering.</summary>
    public async Task DeleteGroupAsync(Guid groupId, string actorUserId, CancellationToken ct = default)
    {
        await using var transaction = await BeginWebWriteAsync(groupId, actorUserId, ct);
        await EnsureOwnerOrManagerAsync(groupId, actorUserId, requireOwner: true, ct);
        var group = await GroupWrite.FreshAsync(_db, _db.Groups.Where(g => g.Id == groupId), ct)
            ?? throw new KeyNotFoundException("Group not found");

        _db.Groups.Remove(group);
        await AddAuditAsync(actorUserId, "GroupDelete", $"Deleted group '{group.Name}'", ct);
        await _db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    /// <summary>Uses the shared write boundary and keeps the legacy Group service authorization exception.</summary>
    private async Task<IDbContextTransaction?> BeginWebWriteAsync(Guid groupId, string actor, CancellationToken ct,
        string? target = null, Func<Task<string[]>>? participants = null)
    {
        try { return await GroupWrite.BeginAsync(_db, groupId, actor, ct, target: target, participants: participants); }
        catch (GroupRefusal refusal) when (refusal.Status == 401)
        {
            throw new UnauthorizedAccessException("An active account is required");
        }
    }

    /// <summary>Commits Group data and maps only the owner/name index to a name conflict.</summary>
    private async Task SaveNameChangeAsync(IDbContextTransaction? transaction,
        string conflictCode, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (GroupWrite.IsUnique(ex, "IX_Groups_OwnerUserId_Name"))
        {
            throw new GroupRefusal(409, conflictCode);
        }
    }

    /// <summary>Lists unarchived groups with active membership; ownership metadata grants no access.</summary>
    public async Task<IReadOnlyList<Group>> ListGroupsForUserAsync(string userId, CancellationToken ct = default)
    {
        var memberOf = from m in _db.GroupMembers
                       where m.UserId == userId && m.Status == GroupMember.MembershipStatuses.Active
                       join g in _db.Groups on m.GroupId equals g.Id
                       where !g.IsArchived
                       select g;
        return await memberOf.AsNoTracking().ToListAsync(ct);
    }

    /// <summary>
    /// Adds a new member to a group with the specified role.
    /// Requires active Owner or Manager membership in an unarchived group.
    /// Role assignment restrictions:
    /// - Only an active Owner membership can assign "Manager" or "Owner" roles.
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
    /// <exception cref="KeyNotFoundException">Thrown when the group does not exist or is archived.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the actor lacks required permissions.</exception>
    public async Task<GroupMember> AddMemberAsync(Guid groupId, string actorUserId, string targetUserId, string role, CancellationToken ct = default)
    {
        await using var transaction = await BeginWebWriteAsync(groupId, actorUserId, ct, target: targetUserId);
        await EnsureOwnerOrManagerAsync(groupId, actorUserId, requireOwner: false, ct);

        // Validate role if provided
        var validRoles = new[] { GroupMember.Roles.Owner, GroupMember.Roles.Manager, GroupMember.Roles.Member };
        var assignedRole = string.IsNullOrWhiteSpace(role) ? GroupMember.Roles.Member : role;
        if (!validRoles.Contains(assignedRole, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Invalid role '{role}'. Valid roles are: {string.Join(", ", validRoles)}", nameof(role));
        }

        // Elevated role assignment requires active Owner membership, independently of metadata.
        var actorIsOwner = await _db.GroupMembers.AsNoTracking()
            .AnyAsync(m => m.GroupId == groupId && m.UserId == actorUserId
                        && m.Status == GroupMember.MembershipStatuses.Active
                        && m.Role == GroupMember.Roles.Owner, ct);

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
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (GroupWrite.IsUnique(ex, "IX_GroupMembers_GroupId_UserId"))
        {
            throw new InvalidOperationException("User already a member", ex);
        }

        return member;
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
        await using var transaction = await BeginWebWriteAsync(groupId, actorUserId, ct,
            target: targetUserId, participants: () => DepartureParticipantsAsync(groupId, targetUserId, ct));
        await EnsureOwnerOrManagerAsync(groupId, actorUserId, requireOwner: false, ct);

        var member = await _db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == targetUserId, ct)
                     ?? throw new KeyNotFoundException("Membership not found");

        var group = await _db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, ct)
                    ?? throw new KeyNotFoundException("Group not found");

        await _db.Entry(member).ReloadAsync(ct);
        await _db.Entry(group).ReloadAsync(ct);

        // Check if the target user is the actual group owner (using Group.OwnerUserId)
        var isActualOwner = group.OwnerUserId == targetUserId;

        if (isActualOwner)
        {
            // Attempt ownership transfer before removing
            var transferred = await TransferOwnershipOnRemovalAsync(group, member, ct);
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
        await RevokePendingInvitationsAsync(groupId, member.UserId, member.LeftAt.Value, ct);
        await AddAuditAsync(actorUserId, "MemberRemove", $"Removed {targetUserId} from group {groupId}", ct);

        // Mark group for deletion before SaveChanges to ensure atomic operation
        await MarkGroupForDeletionIfEmptyAsync(group, actorUserId, ct);

        await _db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    /// <summary>Locks Group before membership/invitation rows; caller owns the transaction and commit.</summary>
    internal static async Task LockMembershipAsync(ApplicationDbContext db, Guid groupId, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "Groups" WHERE "Id" = {groupId} FOR UPDATE""", ct);
    }

    /// <summary>Invalidates only this recipient's pending invitations in the membership transaction.</summary>
    private async Task RevokePendingInvitationsAsync(Guid groupId, string userId, DateTime departedAt, CancellationToken ct)
    {
        var pending = await _db.GroupInvitations.Where(i => i.GroupId == groupId && i.InviteeUserId == userId
            && i.Status == GroupInvitation.InvitationStatuses.Pending).ToListAsync(ct);
        foreach (var invitation in pending)
        {
            await _db.Entry(invitation).ReloadAsync(ct);
            invitation.Status = GroupInvitation.InvitationStatuses.Revoked;
            invitation.RespondedAt = departedAt;
        }
    }

    /// <summary>Transfers ownership during administrative removal using the Group's succession rules.</summary>
    private async Task<bool> TransferOwnershipOnRemovalAsync(Group group, GroupMember currentOwner, CancellationToken ct)
    {
        var organization = GroupSharingPolicy.CanonicalType(group.GroupType) == "Organization";
        var successor = await _db.GroupMembers.Where(m => m.GroupId == group.Id && m.UserId != currentOwner.UserId
                && m.Status == GroupMember.MembershipStatuses.Active && (!organization || m.Role == GroupMember.Roles.Manager))
            .OrderBy(m => m.JoinedAt).ThenBy(m => m.Id).FirstOrDefaultAsync(ct);
        if (successor is null) return false;

        await _db.Entry(successor).ReloadAsync(ct);
        currentOwner.Role = GroupMember.Roles.Member;
        successor.Role = GroupMember.Roles.Owner;
        group.OwnerUserId = successor.UserId;
        group.UpdatedAt = DateTime.UtcNow;
        await AddAuditAsync(currentOwner.UserId, "OwnershipTransferOnRemoval",
            $"Ownership of group '{group.Name}' transferred from {currentOwner.UserId} to {successor.UserId}", ct);
        return true;
    }

    /// <summary>
    /// Requires active membership with the requested Owner/Manager role in an unarchived group.
    /// Ownership metadata is retained for transfers and never grants residual permissions.
    /// </summary>
    /// <param name="groupId">The group to check permissions for.</param>
    /// <param name="actorUserId">The user attempting the action.</param>
    /// <param name="requireOwner">If true, only Owner role is accepted; if false, Manager role also allowed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">Thrown when actorUserId is null or empty.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the group does not exist or is archived.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the user lacks required permissions.</exception>
    private async Task EnsureOwnerOrManagerAsync(Guid groupId, string actorUserId, bool requireOwner, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actorUserId)) throw new ArgumentException("actorUserId required");

        if (!await _db.Groups.AnyAsync(g => g.Id == groupId && !g.IsArchived, ct))
            throw new KeyNotFoundException("Group not found");

        var membership = await _db.GroupMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == actorUserId && m.Status == GroupMember.MembershipStatuses.Active, ct);
        if (requireOwner)
        {
            if (membership?.Role != GroupMember.Roles.Owner)
                throw new UnauthorizedAccessException("Owner permissions required");
        }
        else if (!GroupLocationVisibility.CanManage(membership))
        {
            throw new UnauthorizedAccessException("Manager or Owner permissions required");
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
