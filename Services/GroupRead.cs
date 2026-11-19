using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Reads Group scope and projections together with the request's authority.</summary>
internal static class GroupRead
{
    /// <summary>Starts the coherent read before resolving the actor or loading any Group state.</summary>
    internal static async Task<IDbContextTransaction?> BeginAsync(ApplicationDbContext db, CancellationToken ct) =>
        db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;

    /// <summary>Group availability precedes membership and role checks.</summary>
    internal static async Task<(Group Group, GroupMember Member)> ScopeAsync(ApplicationDbContext db, Guid groupId,
        string userId, bool manage, CancellationToken ct)
    {
        var group = await db.Groups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == groupId && !g.IsArchived, ct)
            ?? throw new GroupRefusal(404, "group_unavailable");
        var member = await db.GroupMembers.AsNoTracking().SingleOrDefaultAsync(m => m.GroupId == groupId
            && m.UserId == userId && m.Status == GroupMember.MembershipStatuses.Active, ct);
        if (member is null || manage && !GroupLocationVisibility.CanManage(member))
            throw new GroupRefusal(403, "group_role_required");
        return (group, member);
    }
}
