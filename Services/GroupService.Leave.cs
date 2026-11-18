using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Services;

public partial class GroupService
{
    /// <summary>Uses the current Leave decision while keeping the Web endpoint's existing failure formats.</summary>
    public async Task LeaveGroupAsync(Guid groupId, string userId, CancellationToken ct = default)
    {
        try { await LeaveAsync(groupId, userId, null, null, ct); }
        catch (GroupRefusal refusal)
        {
            throw refusal.Code switch
            {
                "group_unavailable" or "group_role_required" => new KeyNotFoundException("Group or membership not found"),
                "invalid_bearer" => new UnauthorizedAccessException(),
                "organization_no_manager_successor" => new InvalidOperationException(
                    "You are the owner of this Organization group and there is no eligible Manager successor. Promote a member to Manager first or delete the group."),
                "organization_last_manager" => new InvalidOperationException(
                    "You are the last manager of this Organization group. Transfer or add another manager before leaving."),
                _ => new InvalidOperationException("The successor already owns a group with the same name.")
            };
        }
    }

    /// <summary>Builds a Leave preview within the caller's coherent read snapshot.</summary>
    internal async Task<GroupLeavePreview> PreviewLeaveAsync(Guid groupId, string userId, string? selectedSuccessor,
        CancellationToken ct)
    {
        var (group, member) = await GroupRead.ScopeAsync(_db, groupId, userId, manage: false, ct);
        return await DecideLeaveAsync(group, member, selectedSuccessor, reviewed: true, ct);
    }

    /// <summary>Recomputes the reviewed effect after locks and commits only a still-permitted match.</summary>
    internal async Task<GroupLeaveResult> LeaveAsync(Guid groupId, string userId, GroupLeaveReview? expected,
        string? selectedSuccessor, CancellationToken ct, Func<CancellationToken, Task<bool>>? bearer = null)
    {
        await using var transaction = await GroupWrite.BeginAsync(_db, groupId, userId, ct, bearer,
            participants: () => DepartureParticipantsAsync(groupId, userId, ct));
        var (scopedGroup, scopedMember) = await GroupRead.ScopeAsync(_db, groupId, userId, manage: false, ct);
        if (expected is not null)
        {
            if (expected.GroupId != groupId) throw new GroupRefusal(400, "review_identity_mismatch");
            if (expected.MembershipRef != GroupMembershipEpisode.Reference(scopedMember))
                throw new GroupRefusal(409, "membership_changed");
        }

        var preview = await DecideLeaveAsync(scopedGroup, scopedMember, selectedSuccessor, expected is not null, ct);
        var current = preview.Review;
        if (expected is not null && expected != current) throw new GroupRefusal(409, "leave_changed");
        if (current.BlockedReason is { } reason) throw new GroupRefusal(409, reason);

        var group = await GroupWrite.FreshAsync(_db, _db.Groups.Where(g => g.Id == groupId), ct)
            ?? throw new GroupRefusal(404, "group_unavailable");
        var member = await GroupWrite.FreshAsync(_db, _db.GroupMembers.Where(m => m.GroupId == groupId && m.UserId == userId), ct)
            ?? throw new GroupRefusal(403, "group_role_required");
        var now = GroupMembershipEpisode.AtStoredPrecision(DateTime.UtcNow);
        if (current.SuccessorUserId is { } id)
        {
            var successor = await GroupWrite.FreshAsync(_db,
                _db.GroupMembers.Where(m => m.GroupId == groupId && m.UserId == id), ct)
                ?? throw new GroupRefusal(409, "leave_changed");
            member.Role = GroupMember.Roles.Member;
            successor.Role = GroupMember.Roles.Owner;
            group.OwnerUserId = id;
            group.UpdatedAt = now;
            await AddAuditAsync(userId, "OwnershipTransferOnLeave",
                $"Ownership of group '{group.Name}' transferred from {userId} to {id}", ct);
        }

        member.Status = GroupMember.MembershipStatuses.Left;
        member.LeftAt = now;
        await AddAuditAsync(userId, "MemberLeave", $"User {userId} left group {groupId}", ct);
        if (current.Impact is "DeleteEmptyGroup" or "DisbandGroup")
        {
            _db.Groups.Remove(group);
            var details = current.Impact == "DisbandGroup" ? $"Disbanded group '{group.Name}'" : $"Auto-deleted empty group '{group.Name}'";
            await AddAuditAsync(userId, "GroupDelete", details, ct);
        }
        else
        {
            await RevokePendingInvitationsAsync(groupId, userId, now, ct);
        }
        await SaveNameChangeAsync(transaction, "successor_name_conflict", ct);
        return new(groupId, current.Impact, current.SuccessorUserId);
    }

    /// <summary>Uses one policy for preview and confirmation, including ordered succession and name conflicts.</summary>
    private async Task<GroupLeavePreview> DecideLeaveAsync(Group group, GroupMember member, string? selectedSuccessor,
        bool reviewed, CancellationToken ct)
    {
        var others = await _db.GroupMembers.AsNoTracking().Where(m => m.GroupId == group.Id
                && m.UserId != member.UserId && m.Status == GroupMember.MembershipStatuses.Active)
            .OrderBy(m => m.JoinedAt).ThenBy(m => m.Id)
            .Select(m => new { m.UserId, m.Role, ActiveAccount = m.User != null && m.User.IsActive,
                DisplayName = m.User != null ? m.User.DisplayName : null })
            .ToListAsync(ct);
        var owner = group.OwnerUserId == member.UserId;
        var type = GroupSharingPolicy.CanonicalType(group.GroupType);
        var organization = type == "Organization";
        var canChoose = reviewed && owner && type is "Family" or "Friends";
        IReadOnlyList<GroupLeaveSuccessor>? choices = null;
        if (selectedSuccessor is not null && !canChoose) throw new GroupRefusal(409, "leave_changed");

        if (!owner)
        {
            if (organization && GroupLocationVisibility.CanManage(member)
                && !others.Any(m => m.Role is GroupMember.Roles.Owner or GroupMember.Roles.Manager))
                return Preview("Blocked", reason: "organization_last_manager");
            return Preview(others.Count == 0 ? "DeleteEmptyGroup" : "LeaveOnly");
        }
        if (others.Count == 0 && selectedSuccessor is null) return Preview("DeleteEmptyGroup");

        var eligible = others.Where(m => (!organization || m.Role == GroupMember.Roles.Manager)
            && (!canChoose || m.ActiveAccount)).ToList();
        if (canChoose && eligible.Count > 0)
            choices = eligible.Select(m => new GroupLeaveSuccessor(m.UserId, m.DisplayName)).ToArray();
        var successor = selectedSuccessor is null ? eligible.FirstOrDefault() : eligible.FirstOrDefault(m => m.UserId == selectedSuccessor);
        if (successor is null)
        {
            if (selectedSuccessor is not null) throw new GroupRefusal(409, "leave_changed");
            return canChoose ? Preview("DisbandGroup") : Preview("Blocked", reason: "organization_no_manager_successor");
        }
        if (await _db.Groups.AnyAsync(g => g.OwnerUserId == successor.UserId && g.Name == group.Name, ct))
            return Preview("Blocked", reason: "successor_name_conflict");
        return Preview("TransferOwnership", successor.UserId, successor.DisplayName);

        GroupLeavePreview Preview(string impact, string? successorId = null, string? label = null, string? reason = null) =>
            new(group.Name, new(group.Id, GroupMembershipEpisode.Reference(member), member.Role, owner, impact,
                successorId, reason), label, choices, selectedSuccessor);
    }

    /// <summary>Protects successor candidates and incoming invitations against independent account cascades.</summary>
    private Task<string[]> DepartureParticipantsAsync(Guid groupId, string userId, CancellationToken ct) =>
        _db.GroupMembers.Where(m => m.GroupId == groupId && m.Status == GroupMember.MembershipStatuses.Active)
            .Select(m => m.UserId)
            .Union(_db.GroupInvitations.Where(i => i.GroupId == groupId && i.InviteeUserId == userId
                && i.Status == GroupInvitation.InvitationStatuses.Pending).Select(i => i.InviterUserId))
            .ToArrayAsync(ct);
}
