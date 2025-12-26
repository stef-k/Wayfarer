using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Services;

namespace Wayfarer.Areas.User.Controllers;

/// <summary>
/// User UI for listing joined groups and viewing the group map.
/// </summary>
[Area("User")]
[Authorize(Roles = "User")]
public class GroupsController : BaseController
{
    private readonly IGroupService _groupService;
    private readonly IInvitationService _invitationService;
    private readonly Wayfarer.Parsers.SseService _sse;

    [ActivatorUtilitiesConstructor]
    public GroupsController(ILogger<BaseController> logger, ApplicationDbContext dbContext, IGroupService groupService, IInvitationService invitationService, Wayfarer.Parsers.SseService sse)
        : base(logger, dbContext)
    {
        _groupService = groupService;
        _invitationService = invitationService;
        _sse = sse;
    }

    // Backward-compatible ctor for tests
    public GroupsController(ILogger<BaseController> logger, ApplicationDbContext dbContext)
        : this(logger, dbContext, new GroupService(dbContext), new InvitationService(dbContext), new Wayfarer.Parsers.SseService())
    {}

    /// <summary>
    /// Lists groups that the current user has joined (role Member, Active).
    /// GET /User/Groups
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var joined = await (from m in _dbContext.GroupMembers
                            where m.UserId == userId && m.Status == GroupMember.MembershipStatuses.Active
                            join g in _dbContext.Groups on m.GroupId equals g.Id
                            select new { g.Id, g.Name, g.Description, g.GroupType }).AsNoTracking().ToListAsync();

        // Active member counts per group (same approach as Manager UI)
        var joinedIds = joined.Select(x => (Guid)x.Id).ToList();
        var counts = await _dbContext.GroupMembers
            .Where(m => joinedIds.Contains(m.GroupId) && m.Status == GroupMember.MembershipStatuses.Active)
            .GroupBy(m => m.GroupId)
            .Select(g => new { GroupId = g.Key, Count = g.Count() })
            .ToListAsync();
        ViewBag.MemberCounts = counts.ToDictionary(x => x.GroupId, x => x.Count);

        ViewBag.Joined = joined;
        SetPageTitle("My Groups");
        return View();
    }

    /// <summary>
    /// Create a Family/Friends group (Organization is not allowed for User area).
    /// </summary>
    [HttpGet]
    public IActionResult Create()
    {
        SetPageTitle("Create Group");
        return View();
    }

    /// <summary>
    /// Persist a new Family/Friends group (Organization rejected).
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, string? description, string? groupType)
    {
        var userId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(name))
        {
            ModelState.AddModelError("name", "Name is required.");
            return View();
        }
        if (string.IsNullOrWhiteSpace(groupType))
        {
            ModelState.AddModelError("groupType", "Group type is required.");
            return View();
        }

        // Only allow Family or Friends
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Family", "Friends" };
        if (!allowed.Contains(groupType))
        {
            ModelState.AddModelError("groupType", "Only Family or Friends are allowed.");
            return View();
        }

        try
        {
            var group = await _groupService.CreateGroupAsync(userId, name.Trim(), description);
            group.GroupType = groupType.Equals("family", StringComparison.OrdinalIgnoreCase) ? "Family" : "Friends";
            await _dbContext.SaveChangesAsync();
            LogAudit("GroupCreate", $"Created group {name}", "User UI");
            return RedirectWithAlert("Index", "Groups", "Group created", "success", area: "User");
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError("name", ex.Message);
            return View();
        }
    }

    /// <summary>
    /// Members/invites management for a user-owned group.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Members(Guid groupId)
    {
        var userId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var group = await _dbContext.Groups.FirstOrDefaultAsync(g => g.Id == groupId);
        if (group == null) return NotFound();

        // Only the owner can manage in User area
        var isOwner = await _dbContext.GroupMembers.AsNoTracking()
            .AnyAsync(m => m.GroupId == groupId && m.UserId == userId && m.Role == GroupMember.Roles.Owner && m.Status == GroupMember.MembershipStatuses.Active);
        if (!isOwner && group.OwnerUserId != userId) return Forbid();

        var members = await (from m in _dbContext.GroupMembers
                             where m.GroupId == groupId && m.Status == GroupMember.MembershipStatuses.Active
                             join u in _dbContext.Users on m.UserId equals u.Id
                             select new { m, u }).AsNoTracking().ToListAsync();

        var invites = await (from i in _dbContext.GroupInvitations
                             where i.GroupId == groupId && i.Status == GroupInvitation.InvitationStatuses.Pending
                             join u in _dbContext.Users on i.InviteeUserId equals u.Id into iu
                             from u in iu.DefaultIfEmpty()
                             select new
                             {
                                 i.Id,
                                 i.InviteeUserId,
                                 i.InviteeEmail,
                                 i.CreatedAt,
                                 UserName = u != null ? u.UserName : null,
                                 DisplayName = u != null ? u.DisplayName : null
                             })
            .AsNoTracking()
            .ToListAsync();

        ViewBag.Group = group;
        ViewBag.Members = members;
        ViewBag.Invites = invites;
        ViewBag.CurrentUserId = userId;
        SetPageTitle($"Members - {group.Name}");
        return View();
    }

    /// <summary>
    /// AJAX endpoint to invite a user to a group.
    /// Permission check is delegated to InvitationService which validates Owner or Manager role.
    /// </summary>
    /// <param name="groupId">The group to invite the user to.</param>
    /// <param name="inviteeUserId">The user ID to invite.</param>
    /// <returns>JSON response with invitation details or error message.</returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> InviteAjax(Guid groupId, string inviteeUserId)
    {
        var actorId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(actorId))
            return Unauthorized();

        try
        {
            var inv = await _invitationService.InviteUserAsync(groupId, actorId, inviteeUserId, null, null);
            var group = await _dbContext.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId);
            var gname = group?.Name;
            if (!string.IsNullOrEmpty(inv.InviteeUserId))
            {
                await _sse.BroadcastAsync($"invitation-update-{inv.InviteeUserId}", System.Text.Json.JsonSerializer.Serialize(new { action = "created", id = inv.Id, groupId = groupId, groupName = gname }));
            }
            await _sse.BroadcastAsync($"group-{groupId}", JsonSerializer.Serialize(GroupSseEventDto.InviteCreated(inv.Id)));
            return Ok(new { success = true, invite = new { id = inv.Id, inviteeUserId = inv.InviteeUserId, createdAt = inv.CreatedAt } });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { success = false, message = "Group not found" });
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403, new { success = false, message = "You do not have permission to invite users to this group" });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMemberAjax(Guid groupId, string? userId)
    {
        var actorId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (actorId == null) return Unauthorized();
        if (userId == null) return BadRequest("User ID is required");

        // After null check, userId is guaranteed non-null
        string userIdNonNull = userId;

        try
        {
            var isOwner = await _dbContext.GroupMembers.AsNoTracking()
                .AnyAsync(m => m.GroupId == groupId && m.UserId == actorId! && m.Role == GroupMember.Roles.Owner && m.Status == GroupMember.MembershipStatuses.Active);
            if (!isOwner) return StatusCode(403, new { success = false, message = "Forbidden" });

            await _groupService.RemoveMemberAsync(groupId, actorId!, userIdNonNull);
            await _sse.BroadcastAsync($"group-{groupId}", JsonSerializer.Serialize(GroupSseEventDto.MemberRemoved(userIdNonNull)));
            var group = await _dbContext.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId);
            var gname = group?.Name;
            await _sse.BroadcastAsync($"membership-update-{userIdNonNull}", System.Text.Json.JsonSerializer.Serialize(new { action = "removed", groupId, groupName = gname }));
            return Ok(new { success = true, userId = userIdNonNull });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeInviteAjax(Guid groupId, Guid inviteId)
    {
        var actorId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (actorId == null) return Unauthorized();
        try
        {
            var isOwner = await _dbContext.GroupMembers.AsNoTracking()
                .AnyAsync(m => m.GroupId == groupId && m.UserId == actorId! && m.Role == GroupMember.Roles.Owner && m.Status == GroupMember.MembershipStatuses.Active);
            if (!isOwner) return StatusCode(403, new { success = false, message = "Forbidden" });

            await _invitationService.RevokeAsync(inviteId, actorId);
            // Consolidated group channel
            await _sse.BroadcastAsync($"group-{groupId}", JsonSerializer.Serialize(GroupSseEventDto.InviteRevoked(inviteId)));
            return Ok(new { success = true, inviteId });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Group map for a group the user is a member of. Mirrors manager map behavior but without admin actions.
    /// GET /User/Groups/Map?groupId={id}
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Map(Guid groupId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var group = await _dbContext.Groups.FirstOrDefaultAsync(g => g.Id == groupId);
        if (group == null) return NotFound();

        var isMember = await _dbContext.GroupMembers.AsNoTracking()
            .AnyAsync(m => m.GroupId == groupId && m.UserId == userId! && m.Status == GroupMember.MembershipStatuses.Active);
        if (!isMember) return Forbid();

        ViewBag.Group = group;
        ViewBag.GroupId = groupId;
        var members = await (from m in _dbContext.GroupMembers
                             where m.GroupId == groupId && m.Status == GroupMember.MembershipStatuses.Active
                             join u in _dbContext.Users on m.UserId equals u.Id
                             select new { u.Id, u.UserName, u.DisplayName, m.Role, m.OrgPeerVisibilityAccessDisabled }).AsNoTracking().ToListAsync();
        ViewBag.Members = members;
        ViewBag.CurrentUserId = userId;
        var me = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        ViewBag.CurrentUserName = me?.UserName;
        // current user's peer-visibility flag (reuse existing field)
        var myMembership = await _dbContext.GroupMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId && m.Status == GroupMember.MembershipStatuses.Active);
        ViewBag.MyPeerVisibilityDisabled = myMembership?.OrgPeerVisibilityAccessDisabled ?? false;
        SetPageTitle($"Map - {group.Name}");
        return View();
    }
}
