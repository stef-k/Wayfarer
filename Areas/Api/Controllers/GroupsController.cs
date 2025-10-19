using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;
using Wayfarer.Parsers;
using NetTopologySuite.Geometries;
using Wayfarer.Util;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly SseService _sse;
    private readonly ILogger<GroupsController> _logger;

    [ActivatorUtilitiesConstructor]
    public GroupsController(ApplicationDbContext db, IGroupService groups, ILogger<GroupsController> logger, LocationService locationService, SseService sse)
    {
        _db = db;
        _groups = groups;
        _logger = logger;
        _locationService = locationService;
        _sse = sse;
    }

    // Backward-compatible ctor for tests
    public GroupsController(ApplicationDbContext db, IGroupService groups, ILogger<GroupsController> logger, LocationService locationService)
        : this(db, groups, logger, locationService, new SseService())
    {}

    private string? CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] GroupCreateRequest request, CancellationToken ct)
    {
        if (CurrentUserId is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request?.Name)) return BadRequest(new { message = "Name is required" });

        try
        {
            var group = await _groups.CreateGroupAsync(CurrentUserId, request.Name.Trim(), request.Description, ct);
            return CreatedAtAction(nameof(Get), new { id = group.Id }, new { id = group.Id, name = group.Name, description = group.Description });
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
            .AnyAsync(m => m.GroupId == groupId && m.UserId == CurrentUserId && m.Status == GroupMember.MembershipStatuses.Active, ct);
        if (!isMember) return StatusCode(403);

        var roster = await (from m in _db.GroupMembers
                            where m.GroupId == groupId
                            join u in _db.Users on m.UserId equals u.Id
                            select new { m, u })
            .AsNoTracking()
            .ToListAsync(ct);

        var payload = roster.Select(x => new Wayfarer.Models.Dtos.GroupMemberDto
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
    public async Task<IActionResult> Latest([FromRoute] Guid groupId, [FromBody] GroupLocationsLatestRequest req, CancellationToken ct)
    {
        if (CurrentUserId is null) return Unauthorized();
        // must be active member
        var isMember = await _db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == CurrentUserId && m.Status == GroupMember.MembershipStatuses.Active, ct);
        if (!isMember) return StatusCode(403);

        // determine allowed userIds (default: all active members)
        var activeMemberIds = await _db.GroupMembers.Where(m => m.GroupId == groupId && m.Status == GroupMember.MembershipStatuses.Active)
            .Select(m => m.UserId).ToListAsync(ct);
        var userIds = (req?.IncludeUserIds != null && req.IncludeUserIds.Count > 0)
            ? req.IncludeUserIds.Intersect(activeMemberIds).Distinct().ToList()
            : activeMemberIds;

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
        int locationTimeThreshold = settings?.LocationTimeThresholdMinutes ?? 10;

        var result = latestPerUser.Select(t => new PublicLocationDto
        {
            Id = t.Loc.Id,
            UserId = t.UserId,
            Timestamp = t.Loc.Timestamp,
            LocalTimestamp = CoordinateTimeZoneConverter.ConvertUtcToLocal(t.Loc.Coordinates.Y, t.Loc.Coordinates.X, DateTime.SpecifyKind(t.Loc.LocalTimestamp, DateTimeKind.Utc)),
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
            VehicleId = t.Loc.VehicleId,
            IsLatestLocation = true,
            LocationTimeThresholdMinutes = locationTimeThreshold
        }).ToList();

        return Ok(result);
    }

    // POST /api/groups/{groupId}/locations/query
    [HttpPost("{groupId}/locations/query")]
    public async Task<IActionResult> Query([FromRoute] Guid groupId, [FromBody] GroupLocationsQueryRequest req, CancellationToken ct)
    {
        if (CurrentUserId is null) return Unauthorized();
        var isMember = await _db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == CurrentUserId && m.Status == GroupMember.MembershipStatuses.Active, ct);
        if (!isMember) return StatusCode(403);

        var activeMemberIds = await _db.GroupMembers.Where(m => m.GroupId == groupId && m.Status == GroupMember.MembershipStatuses.Active)
            .Select(m => m.UserId).ToListAsync(ct);
        var userIds = (req?.UserIds != null && req.UserIds.Count > 0)
            ? req.UserIds.Intersect(activeMemberIds).Distinct().ToList()
            : activeMemberIds;

        var combined = new List<PublicLocationDto>();
        int total = 0;
        foreach (var uid in userIds)
        {
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
        }

        return Ok(new { totalItems = total, results = combined });
    }


    // POST /api/groups/{id}/settings/org-peer-visibility
    [HttpPost("{id}/settings/org-peer-visibility")]
    public async Task<IActionResult> ToggleOrgPeerVisibility([FromRoute] Guid id, [FromBody] OrgPeerVisibilityToggleRequest req, CancellationToken ct)
    {
        if (CurrentUserId is null) return Unauthorized();
        var group = await _db.Groups.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group == null) return NotFound();
        if (!string.Equals(group.GroupType, "Organization", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Not an Organisation group" });

        var membership = await _db.GroupMembers.AsNoTracking().FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == CurrentUserId && m.Status == GroupMember.MembershipStatuses.Active, ct);
        var isOwnerOrManager = membership != null && (membership.Role == GroupMember.Roles.Owner || membership.Role == GroupMember.Roles.Manager);
        if (!isOwnerOrManager) return StatusCode(403);

        group.OrgPeerVisibilityEnabled = req.Enabled;
        group.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { enabled = group.OrgPeerVisibilityEnabled });
    }

    // POST /api/groups/{id}/members/{userId}/org-peer-visibility-access
    [HttpPost("{id}/members/{userId}/org-peer-visibility-access")]
    public async Task<IActionResult> SetMemberOrgPeerVisibilityAccess([FromRoute] Guid id, [FromRoute] string userId, [FromBody] OrgPeerVisibilityAccessRequest req, CancellationToken ct)
    {
        if (CurrentUserId is null) return Unauthorized();
        if (!string.Equals(CurrentUserId, userId, StringComparison.Ordinal)) return StatusCode(403);

        var group = await _db.Groups.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group == null) return NotFound();
        if (!string.Equals(group.GroupType, "Organization", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Not an Organisation group" });

        var member = await _db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == userId && m.Status == GroupMember.MembershipStatuses.Active, ct);
        if (member == null) return StatusCode(403);

        member.OrgPeerVisibilityAccessDisabled = req.Disabled;
        await _db.SaveChangesAsync(ct);
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
                                where m.UserId == CurrentUserId && m.Status == GroupMember.MembershipStatuses.Active && m.Role == GroupMember.Roles.Member
                                join g in _db.Groups on m.GroupId equals g.Id
                                select new { g.Id, g.Name, g.Description }).AsNoTracking().ToListAsync(ct);
            return Ok(joined);
        }

        // default: all user-related groups
        var list = await _groups.ListGroupsForUserAsync(CurrentUserId, ct);
        var payload = list.Select(g => new { g.Id, g.Name, g.Description }).ToList();
        return Ok(payload);
    }

    // POST /api/groups/{groupId}/leave
    [HttpPost("{groupId}/leave")]
    public async Task<IActionResult> Leave([FromRoute] Guid groupId, CancellationToken ct)
    {
        if (CurrentUserId is null) return Unauthorized();
        try
        {
            await _groups.LeaveGroupAsync(groupId, CurrentUserId, ct);
            await _sse.BroadcastAsync($"group-membership-update-{groupId}", System.Text.Json.JsonSerializer.Serialize(new { action = "member-left", userId = CurrentUserId }));
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
    public async Task<IActionResult> RemoveMember([FromRoute] Guid groupId, [FromRoute] string userId, CancellationToken ct)
    {
        if (CurrentUserId is null) return Unauthorized();
        try
        {
            await _groups.RemoveMemberAsync(groupId, CurrentUserId, userId, ct);
            await _sse.BroadcastAsync($"group-membership-update-{groupId}", System.Text.Json.JsonSerializer.Serialize(new { action = "member-removed", userId }));
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
}
