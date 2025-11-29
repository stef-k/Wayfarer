using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Util;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Areas.Api.Controllers;

[Area("Api")]
[Route("api/groups")]
[ApiController]
[Authorize]
public class GroupsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IGroupService _groups;
    private readonly LocationService _locationService;
    private readonly ILogger<GroupsController> _logger;
    private readonly SseService _sse;

    [ActivatorUtilitiesConstructor]
    public GroupsController(ApplicationDbContext db, IGroupService groups, ILogger<GroupsController> logger,
        LocationService locationService, SseService sse)
    {
        _db = db;
        _groups = groups;
        _logger = logger;
        _locationService = locationService;
        _sse = sse;
    }

    // Backward-compatible ctor for tests
    public GroupsController(ApplicationDbContext db, IGroupService groups, ILogger<GroupsController> logger,
        LocationService locationService)
        : this(db, groups, logger, locationService, new SseService())
    {
    }

    private string? CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] GroupCreateRequest request, CancellationToken ct)
    {
        if (CurrentUserId is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request?.Name)) return BadRequest(new { message = "Name is required" });

        try
        {
            var group = await _groups.CreateGroupAsync(CurrentUserId, request.Name.Trim(), request.Description, ct);
            return CreatedAtAction(nameof(Get), new { id = group.Id },
                new { id = group.Id, name = group.Name, description = group.Description });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Group create failed");
            return Conflict(new { message = ex.Message });
        }
    }

    // GET /api/groups/{groupId}/members
    [HttpGet("{groupId}/members")]
    public async Task<IActionResult> Members([FromRoute] Guid groupId, CancellationToken ct)
    {
        if (CurrentUserId is null) return Unauthorized();

        // must be a member to view list
        var isMember = await _db.GroupMembers
            .AnyAsync(
                m => m.GroupId == groupId && m.UserId == CurrentUserId &&
                     m.Status == GroupMember.MembershipStatuses.Active, ct);
        if (!isMember) return StatusCode(403);

        var roster = await (from m in _db.GroupMembers
                where m.GroupId == groupId
                join u in _db.Users on m.UserId equals u.Id
                select new { m, u })
            .AsNoTracking()
            .ToListAsync(ct);

        var payload = roster.Select(x => new GroupMemberDto
        {
            UserId = x.u.Id,
            UserName = x.u.UserName ?? string.Empty,
            DisplayName = x.u.DisplayName,
            GroupRole = x.m.Role,
            Status = x.m.Status
        }).ToList();

        return Ok(payload);
    }

    // POST /api/groups/{groupId}/locations/latest
    [HttpPost("{groupId}/locations/latest")]
    public async Task<IActionResult> Latest([FromRoute] Guid groupId, [FromBody] GroupLocationsLatestRequest req,
        CancellationToken ct)
    {
        if (CurrentUserId is null) return Unauthorized();
        // must be active member
        var isMember = await _db.GroupMembers.AnyAsync(
            m => m.GroupId == groupId && m.UserId == CurrentUserId && m.Status == GroupMember.MembershipStatuses.Active,
            ct);
        if (!isMember) return StatusCode(403);

        var group = await _db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        var isFriends = string.Equals(group?.GroupType, "Friends", StringComparison.OrdinalIgnoreCase);

        // determine allowed userIds (default: all active members)
        var activeMembers = await _db.GroupMembers
            .Where(m => m.GroupId == groupId && m.Status == GroupMember.MembershipStatuses.Active)
            .Select(m => new { m.UserId, m.OrgPeerVisibilityAccessDisabled })
            .ToListAsync(ct);
        var activeMemberIds = activeMembers.Select(x => x.UserId).ToList();
        var requested = req?.IncludeUserIds != null && req.IncludeUserIds.Count > 0
            ? req.IncludeUserIds.Distinct().ToList()
            : activeMemberIds;
        // enforce visibility for Friends: others only if not disabled; always include self
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in activeMembers)
        {
            if (m.UserId == CurrentUserId)
            {
                allowed.Add(m.UserId);
                continue;
            }

            if (!isFriends)
            {
                allowed.Add(m.UserId);
                continue;
            }

            if (!m.OrgPeerVisibilityAccessDisabled) allowed.Add(m.UserId);
        }

        var userIds = requested.Intersect(activeMemberIds).Where(uid => allowed.Contains(uid)).ToList();

        // query latest per user in the same order as userIds for stable client mapping
        var latestPerUser = new List<(string UserId, Location Loc)>();
        foreach (var uid in userIds)
        {
            var latest = await _db.Locations
                .Where(l => l.UserId == uid)
                .OrderByDescending(l => l.LocalTimestamp)
                .Include(l => l.ActivityType)
                .AsNoTracking()
                .FirstOrDefaultAsync(ct);
            if (latest != null)
                latestPerUser.Add((uid, latest));
        }

        // settings for threshold
        var settings = await _db.ApplicationSettings.FirstOrDefaultAsync(ct);
        var locationTimeThreshold = settings?.LocationTimeThresholdMinutes ?? 10;

        var result = latestPerUser.Select(t => new PublicLocationDto
        {
            Id = t.Loc.Id,
            UserId = t.UserId,
            Timestamp = t.Loc.Timestamp,
            LocalTimestamp = CoordinateTimeZoneConverter.ConvertUtcToLocal(t.Loc.Coordinates.Y, t.Loc.Coordinates.X,
                DateTime.SpecifyKind(t.Loc.LocalTimestamp, DateTimeKind.Utc)),
            Coordinates = t.Loc.Coordinates,
            Timezone = t.Loc.TimeZoneId,
            Accuracy = t.Loc.Accuracy,
            Altitude = t.Loc.Altitude,
            Speed = t.Loc.Speed,
            LocationType = t.Loc.LocationType,
            ActivityType = t.Loc.ActivityType?.Name,
            Address = t.Loc.Address,
            FullAddress = t.Loc.FullAddress,
            StreetName = t.Loc.StreetName,
            PostCode = t.Loc.PostCode,
            Place = t.Loc.Place,
            Region = t.Loc.Region,
            Country = t.Loc.Country,
            Notes = t.Loc.Notes,
            IsLatestLocation = true,
            LocationTimeThresholdMinutes = locationTimeThreshold
        }).ToList();

        return Ok(result);
    }

    // POST /api/groups/{groupId}/locations/query
    [HttpPost("{groupId}/locations/query")]
    public async Task<IActionResult> Query([FromRoute] Guid groupId, [FromBody] GroupLocationsQueryRequest req,
        CancellationToken ct)
    {
        if (CurrentUserId is null) return Unauthorized();
        if (req is null) return BadRequest("Request body is required");
        var isMember = await _db.GroupMembers.AnyAsync(
            m => m.GroupId == groupId && m.UserId == CurrentUserId && m.Status == GroupMember.MembershipStatuses.Active,
            ct);
        if (!isMember) return StatusCode(403);

        var group = await _db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        var isFriends = string.Equals(group?.GroupType, "Friends", StringComparison.OrdinalIgnoreCase);
        var activeMembers = await _db.GroupMembers
            .Where(m => m.GroupId == groupId && m.Status == GroupMember.MembershipStatuses.Active)
            .Select(m => new { m.UserId, m.OrgPeerVisibilityAccessDisabled })
            .ToListAsync(ct);
        var activeMemberIds = activeMembers.Select(x => x.UserId).ToList();
        var requested = req.UserIds != null && req.UserIds.Count > 0
            ? req.UserIds.Distinct().ToList()
            : activeMemberIds;
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in activeMembers)
        {
            if (m.UserId == CurrentUserId)
            {
                allowed.Add(m.UserId);
                continue;
            }

            if (!isFriends)
            {
                allowed.Add(m.UserId);
                continue;
            }

            if (!m.OrgPeerVisibilityAccessDisabled) allowed.Add(m.UserId);
        }

        var userIds = requested.Intersect(activeMemberIds).Where(uid => allowed.Contains(uid)).ToList();

        // Performance guard: if multiple users are requested, enforce day-only queries
        // This limits the result size and keeps UI responsive even for large datasets.
        if (userIds.Count > 1)
        {
            req.DateType = "day";
            // If specific day not provided, default to today's UTC date
            if (!req.Year.HasValue || !req.Month.HasValue || !req.Day.HasValue)
            {
                var today = DateTime.UtcNow;
                req.Year ??= today.Year;
                req.Month ??= today.Month;
                req.Day ??= today.Day;
            }
        }

        // Fallback: if client sent no DateType at all, default to today's day view to avoid huge history loads
        if (string.IsNullOrWhiteSpace(req.DateType))
        {
            var today = DateTime.UtcNow;
            req.DateType = "day";
            req.Year = today.Year;
            req.Month = today.Month;
            req.Day = today.Day;
        }

        var combined = new List<PublicLocationDto>();
        var total = 0;
        foreach (var uid in userIds)
            if (!string.IsNullOrWhiteSpace(req.DateType) && req.Year.HasValue)
            {
                var (locs, userTotal) = await _locationService.GetLocationsByDateAsync(
                    uid, req.DateType!, req.Year!.Value, req.Month, req.Day, ct);
                // filter by bbox
                var filtered = locs.Where(l =>
                    l.Coordinates.X >= req.MinLng && l.Coordinates.X <= req.MaxLng &&
                    l.Coordinates.Y >= req.MinLat && l.Coordinates.Y <= req.MaxLat).ToList();
                foreach (var d in filtered) d.UserId = uid;
                combined.AddRange(filtered);
                total += filtered.Count; // for date-filtered path, count filtered
            }
            else
            {
                var (locations, userTotal) = await _locationService.GetLocationsAsync(
                    req.MinLng, req.MinLat, req.MaxLng, req.MaxLat, req.ZoomLevel, uid, ct);
                foreach (var d in locations) d.UserId = uid;
                combined.AddRange(locations);
                total += userTotal;
            }

        return Ok(new { totalItems = total, results = combined });
    }


    // POST /api/groups/{id}/settings/org-peer-visibility
    [HttpPost("{id}/settings/org-peer-visibility")]
    public async Task<IActionResult> ToggleOrgPeerVisibility([FromRoute] Guid id,
        [FromBody] OrgPeerVisibilityToggleRequest req, CancellationToken ct)
    {
        if (CurrentUserId is null) return Unauthorized();
        var group = await _db.Groups.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group == null) return NotFound();
        if (!string.Equals(group.GroupType, "Organization", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Not an Organisation group" });

        var membership = await _db.GroupMembers.AsNoTracking().FirstOrDefaultAsync(
            m => m.GroupId == id && m.UserId == CurrentUserId && m.Status == GroupMember.MembershipStatuses.Active, ct);
        var isOwnerOrManager = membership != null &&
                               (membership.Role == GroupMember.Roles.Owner ||
                                membership.Role == GroupMember.Roles.Manager);
        if (!isOwnerOrManager) return StatusCode(403);

        group.OrgPeerVisibilityEnabled = req.Enabled;
        group.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await AddAuditAsync(CurrentUserId, "OrgPeerVisibilityToggle",
            $"Group {group.Id} set Enabled={group.OrgPeerVisibilityEnabled}", ct);
        return Ok(new { enabled = group.OrgPeerVisibilityEnabled });
    }

    // POST /api/groups/{id}/members/{userId}/org-peer-visibility-access
    [HttpPost("{id}/members/{userId}/org-peer-visibility-access")]
    public async Task<IActionResult> SetMemberOrgPeerVisibilityAccess([FromRoute] Guid id, [FromRoute] string userId,
        [FromBody] OrgPeerVisibilityAccessRequest req, CancellationToken ct)
    {
        if (CurrentUserId is null) return Unauthorized();
        if (!string.Equals(CurrentUserId, userId, StringComparison.Ordinal)) return StatusCode(403);

        var group = await _db.Groups.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group == null) return NotFound();
        // Allow per-user peer visibility control; currently applied by Friends logic,
        // but stored for all groups for consistency (tests depend on this).

        var member = await _db.GroupMembers.FirstOrDefaultAsync(
            m => m.GroupId == id && m.UserId == userId && m.Status == GroupMember.MembershipStatuses.Active, ct);
        if (member == null) return StatusCode(403);

        member.OrgPeerVisibilityAccessDisabled = req.Disabled;
        await _db.SaveChangesAsync(ct);
        await AddAuditAsync(CurrentUserId, "OrgPeerVisibilityAccessSet",
            $"Group {group.Id}, User {userId}, Disabled={member.OrgPeerVisibilityAccessDisabled}", ct);

        // Broadcast visibility change to all group members via SSE
        await _sse.BroadcastAsync($"group-membership-update-{id}",
            JsonSerializer.Serialize(new
                { action = "peer-visibility-changed", userId, disabled = member.OrgPeerVisibilityAccessDisabled }));

        return Ok(new { disabled = member.OrgPeerVisibilityAccessDisabled });
    }

    // GET /api/groups?scope=managed|joined
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? scope, CancellationToken ct)
    {
        if (CurrentUserId is null) return Unauthorized();

        scope = (scope ?? string.Empty).Trim().ToLowerInvariant();

        if (scope == "managed")
        {
            var managed = await (from m in _db.GroupMembers
                where m.UserId == CurrentUserId && m.Status == GroupMember.MembershipStatuses.Active &&
                      (m.Role == GroupMember.Roles.Owner || m.Role == GroupMember.Roles.Manager)
                join g in _db.Groups on m.GroupId equals g.Id
                select new { g.Id, g.Name, g.Description }).AsNoTracking().ToListAsync(ct);
            // also include groups the user owns explicitly
            var owned = await _db.Groups.Where(g => g.OwnerUserId == CurrentUserId)
                .Select(g => new { g.Id, g.Name, g.Description }).AsNoTracking().ToListAsync(ct);
            var combined = managed.Union(owned).Distinct().ToList();
            return Ok(combined);
        }

        if (scope == "joined")
        {
            var joined = await (from m in _db.GroupMembers
                where m.UserId == CurrentUserId && m.Status == GroupMember.MembershipStatuses.Active &&
                      m.Role == GroupMember.Roles.Member
                join g in _db.Groups on m.GroupId equals g.Id
                select new { g.Id, g.Name, g.Description }).AsNoTracking().ToListAsync(ct);
            return Ok(joined);
        }

        // default: all user-related groups
        var list = await _groups.ListGroupsForUserAsync(CurrentUserId, ct);
        var payload = list.Select(g => new { g.Id, g.Name, g.Description }).ToList();
        return Ok(payload);
    }

    /// <summary>
    ///     Returns recent activity for groups the current user manages (owner/manager roles).
    ///     Used for offline notification badge for managers.
    /// </summary>
    [HttpGet("managed/activity")]
    [Authorize(Roles = "Manager")]
    public async Task<IActionResult> ManagedActivity([FromQuery] int sinceHours = 24, CancellationToken ct = default)
    {
        if (CurrentUserId is null) return Unauthorized();
        if (sinceHours <= 0 || sinceHours > 168) sinceHours = 24; // clamp to [1..168]

        var managedGroupIds = await (from m in _db.GroupMembers
                where m.UserId == CurrentUserId
                      && m.Status == GroupMember.MembershipStatuses.Active
                      && (m.Role == GroupMember.Roles.Owner || m.Role == GroupMember.Roles.Manager)
                select m.GroupId)
            .Distinct()
            .ToListAsync(ct);

        if (managedGroupIds.Count == 0) return Ok(new { count = 0, items = Array.Empty<object>() });

        var since = DateTime.UtcNow.AddHours(-sinceHours);
        // Fetch recent logs and filter by group id occurrence in details
        var logs = await _db.AuditLogs
            .Where(a => a.Timestamp >= since)
            .OrderByDescending(a => a.Timestamp)
            .Take(500)
            .AsNoTracking()
            .ToListAsync(ct);

        // Map groupId -> name for convenience
        var gidNames = await _db.Groups.Where(g => managedGroupIds.Contains(g.Id))
            .Select(g => new { g.Id, g.Name })
            .AsNoTracking().ToListAsync(ct);
        var nameMap = gidNames.ToDictionary(x => x.Id, x => x.Name);

        var items = new List<object>();
        foreach (var log in logs)
        {
            Guid? gid = null;
            foreach (var mgid in managedGroupIds)
                if (log.Details?.Contains(mgid.ToString(), StringComparison.OrdinalIgnoreCase) == true)
                {
                    gid = mgid;
                    break;
                }

            if (gid.HasValue)
            {
                var gname = nameMap.ContainsKey(gid.Value) ? nameMap[gid.Value] : null;
                items.Add(new { log.Timestamp, log.Action, log.Details, GroupId = gid.Value, GroupName = gname });
            }
        }

        return Ok(new { count = items.Count, items });
    }

    // POST /api/groups/{groupId}/leave
    [HttpPost("{groupId}/leave")]
    public async Task<IActionResult> Leave([FromRoute] Guid groupId, CancellationToken ct)
    {
        if (CurrentUserId is null) return Unauthorized();
        try
        {
            await _groups.LeaveGroupAsync(groupId, CurrentUserId, ct);
            await _sse.BroadcastAsync($"group-membership-update-{groupId}",
                JsonSerializer.Serialize(new { action = "member-left", userId = CurrentUserId }));
            var group = await _db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
            var gname = group?.Name;
            await _sse.BroadcastAsync($"membership-update-{CurrentUserId}",
                JsonSerializer.Serialize(new { action = "left", groupId, groupName = gname }));
            return Ok(new { message = "Left group" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    // POST /api/groups/{groupId}/members/{userId}/remove
    [HttpPost("{groupId}/members/{userId}/remove")]
    public async Task<IActionResult> RemoveMember([FromRoute] Guid groupId, [FromRoute] string userId,
        CancellationToken ct)
    {
        if (CurrentUserId is null) return Unauthorized();
        try
        {
            await _groups.RemoveMemberAsync(groupId, CurrentUserId, userId, ct);
            await _sse.BroadcastAsync($"group-membership-update-{groupId}",
                JsonSerializer.Serialize(new { action = "member-removed", userId }));
            var group = await _db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
            var gname = group?.Name;
            await _sse.BroadcastAsync($"membership-update-{userId}",
                JsonSerializer.Serialize(new { action = "removed", groupId, groupName = gname }));
            return Ok(new { message = "Member removed" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403);
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