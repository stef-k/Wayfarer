using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

[Area("Api")]
[Route("api/groups")]
[ApiController]
[Authorize]
public class GroupsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IGroupService _groups;
    private readonly ILogger<GroupsController> _logger;

    public GroupsController(ApplicationDbContext db, IGroupService groups, ILogger<GroupsController> logger)
    {
        _db = db;
        _groups = groups;
        _logger = logger;
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

