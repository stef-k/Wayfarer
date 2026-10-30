using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;

namespace Wayfarer.Services;

/// <summary>Best-effort postcommit deletion hints; durable deletion and receipts do not depend on SSE delivery.</summary>
internal static class LocationDeletionNotifications
{
    /// <summary>Retains existing owner and active-group audiences without creating an outbox or publication authority.</summary>
    internal static async Task BroadcastAsync(ApplicationDbContext db, SseService sse, string userId,
        IEnumerable<int> locationIds, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId)) return;
        var idList = locationIds?.ToList();
        if (idList == null || idList.Count == 0) return;

        // Get user for per-user broadcast
        var user = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.UserName })
            .FirstOrDefaultAsync(cancellationToken);

        // Broadcast to per-user channel for timeline views
        if (!string.IsNullOrEmpty(user?.UserName))
        {
            foreach (var locationId in idList)
            {
                var userPayload = new
                {
                    type = "location-deleted",
                    locationId,
                    userId
                };
                await sse.BroadcastAsync($"location-update-{user.UserName}", JsonSerializer.Serialize(userPayload));
            }
        }

        // Broadcast to group channels
        var groupIds = await db.GroupMembers
            .Where(m => m.UserId == userId && m.Status == GroupMember.MembershipStatuses.Active)
            .Join(
                db.Groups,
                member => member.GroupId,
                group => group.Id,
                (member, group) => new { member.GroupId, group.IsArchived })
            .Where(x => !x.IsArchived)
            .Select(x => x.GroupId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (groupIds.Count == 0) return;

        foreach (var locationId in idList)
        {
            var payload = GroupSseEventDto.LocationDeleted(locationId, userId);
            var serializedPayload = JsonSerializer.Serialize(payload);

            foreach (var groupId in groupIds)
                await sse.BroadcastAsync($"group-{groupId}", serializedPayload);
        }
    }

}
