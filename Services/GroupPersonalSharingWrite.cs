using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Updates personal sharing on both surfaces under the Group write boundary.</summary>
internal static class GroupPersonalSharingWrite
{
    /// <summary>Preserves the scalar error order and checks episode identity before policy on reviewed writes.</summary>
    internal static async Task SetAsync(ApplicationDbContext db, Guid groupId, string actor, bool disabled,
        string? membershipRef, bool audit, CancellationToken ct, Func<CancellationToken, Task<bool>>? bearer = null)
    {
        await using var transaction = await GroupWrite.BeginAsync(db, groupId, actor, ct, bearer);
        var group = await db.Groups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == groupId, ct)
            ?? throw new GroupRefusal(404, "group_unavailable");
        if (membershipRef is not null && group.IsArchived)
            throw new GroupRefusal(404, "group_unavailable");
        var writable = GroupLocationVisibility.CanSetPersonalSharing(group);
        if (membershipRef is null && !writable) throw new GroupRefusal(400, "sharing_unavailable");

        var member = await GroupWrite.FreshAsync(db, db.GroupMembers.Where(m => m.GroupId == groupId
            && m.UserId == actor && m.Status == GroupMember.MembershipStatuses.Active), ct)
            ?? throw new GroupRefusal(403, "group_role_required");
        if (membershipRef is not null && membershipRef != GroupMembershipEpisode.Reference(member))
            throw new GroupRefusal(409, "membership_changed");
        if (!writable) throw new GroupRefusal(400, "sharing_unavailable");

        member.OrgPeerVisibilityAccessDisabled = disabled;
        if (audit)
            db.AuditLogs.Add(new AuditLog
            {
                UserId = actor, Action = "OrgPeerVisibilityAccessSet", Timestamp = DateTime.UtcNow,
                Details = $"Group {groupId}, User {actor}, Disabled={disabled}"
            });
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }
}
