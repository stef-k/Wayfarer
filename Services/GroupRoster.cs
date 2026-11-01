using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Services;

/// <summary>Shared active-member identity projection for cookie, bearer and server-rendered Web rosters.</summary>
public static class GroupRoster
{
    /// <summary>Callers authorize the exact Group first; references reveal no owner state or location audience.</summary>
    public static async Task<List<GroupMemberDto>> ReadAsync(ApplicationDbContext db, Guid groupId, string caller,
        bool mobile = false, CancellationToken ct = default, IUserColorService? colors = null)
    {
        var rows = await (from member in db.GroupMembers
            where member.GroupId == groupId && member.Status == GroupMember.MembershipStatuses.Active
            join user in db.Users on member.UserId equals user.Id
            select new { member, user }).AsNoTracking().ToListAsync(ct);
        colors ??= new UserColorService();
        return rows.Select(row => new GroupMemberDto
        {
            UserId = row.user.Id, UserName = row.user.UserName ?? "", DisplayName = row.user.DisplayName,
            GroupRole = row.member.Role, Status = row.member.Status, IsSelf = row.user.Id == caller,
            ColorHex = colors.GetColorHex(row.user.UserName ?? ""),
            ProfilePhoto = row.user.IsActive ? ProfilePhotoReference.Group(row.user.ProfilePhotoVersion, groupId, row.user.Id, mobile) : null,
            OrgPeerVisibilityAccessDisabled = row.member.OrgPeerVisibilityAccessDisabled
        }).OrderBy(m => m.UserName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
