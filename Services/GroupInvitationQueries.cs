using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Shared registered-account invitation lifecycle and picker queries.</summary>
internal static class GroupInvitationQueries
{
    /// <summary>Filters live invitations in available Groups before downstream paging or limits.</summary>
    internal static IQueryable<GroupInvitation> Live(ApplicationDbContext db, DateTime now) =>
        db.GroupInvitations.AsNoTracking().Where(i => i.Status == GroupInvitation.InvitationStatuses.Pending
            && (i.ExpiresAt == null || i.ExpiresAt > now) && i.Group != null && !i.Group.IsArchived);

    /// <summary>Projects elapsed pending invitations without mutating storage.</summary>
    internal static string Status(GroupInvitation invitation, DateTime now) =>
        invitation.Status == GroupInvitation.InvitationStatuses.Pending && invitation.ExpiresAt <= now
            ? GroupInvitation.InvitationStatuses.Expired
            : invitation.Status;

    /// <summary>Returns a lifecycle refusal for a registered invitation that can no longer transition.</summary>
    internal static string? Refusal(GroupInvitation invitation, DateTime now) => Status(invitation, now) switch
    {
        GroupInvitation.InvitationStatuses.Pending => null,
        GroupInvitation.InvitationStatuses.Expired => "invitation_expired",
        _ => "invitation_not_pending"
    };

    /// <summary>Finds active accounts by literal substring, excluding members and live pending recipients.</summary>
    internal static Task<List<GroupInviteCandidate>> CandidatesAsync(ApplicationDbContext db, Guid groupId,
        string query, DateTime now, CancellationToken ct)
    {
        var pending = Live(db, now).Where(i => i.GroupId == groupId).Select(i => i.InviteeUserId);
        var active = db.GroupMembers.Where(m => m.GroupId == groupId && m.Status == GroupMember.MembershipStatuses.Active)
            .Select(m => m.UserId);
        var escaped = "%" + query.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
        var candidates = db.Users.AsNoTracking().Where(u => u.IsActive && !active.Contains(u.Id) && !pending.Contains(u.Id));
        candidates = db.Database.IsNpgsql()
            ? candidates.Where(u => EF.Functions.ILike(u.UserName ?? "", escaped, @"\")
                || EF.Functions.ILike(u.DisplayName ?? "", escaped, @"\"))
            : candidates.Where(u => (u.UserName ?? "").ToUpper().Contains(query.ToUpper())
                || (u.DisplayName ?? "").ToUpper().Contains(query.ToUpper()));
        return candidates.OrderBy(u => u.UserName == null).ThenBy(u => u.UserName).ThenBy(u => u.Id)
            .Select(u => new GroupInviteCandidate(u.Id, u.UserName, u.DisplayName)).Take(10).ToListAsync(ct);
    }
}

/// <summary>The safe picker projection, with an account ID and optional name fields.</summary>
internal sealed record GroupInviteCandidate(string UserId, string? UserName, string? DisplayName);
