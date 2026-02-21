using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Services;

namespace Wayfarer.Areas.Manager.Controllers;

/// <summary>
/// Manager UI controller for listing and managing groups.
/// </summary>
[Area("Manager")]
[Authorize(Roles = "Manager")]
    public class GroupsController : BaseController
    {
        private readonly IGroupService _groupService;
        private readonly IInvitationService _invitationService;
        private readonly Wayfarer.Parsers.SseService _sse;
    private static readonly HashSet<string> AllowedGroupTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Organization", "Family", "Friends"
    };

    [ActivatorUtilitiesConstructor]
    public GroupsController(ILogger<BaseController> logger, ApplicationDbContext dbContext, IGroupService groupService, IInvitationService invitationService, Wayfarer.Parsers.SseService sse)
        : base(logger, dbContext)
    {
        _groupService = groupService;
        _invitationService = invitationService;
        _sse = sse;
    }

    // Backward-compatible ctor for tests
    public GroupsController(ILogger<BaseController> logger, ApplicationDbContext dbContext, IGroupService groupService, IInvitationService invitationService)
        : this(logger, dbContext, groupService, invitationService, new Wayfarer.Parsers.SseService())
    {
    }

    /// <summary>
    /// Shows managed groups for the current manager (owner/manager roles or owned).
    /// </summary>
    public async Task<IActionResult> Index()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var managed = await (from m in _dbContext.GroupMembers
                             where m.UserId == userId && m.Status == GroupMember.MembershipStatuses.Active &&
                                   (m.Role == GroupMember.Roles.Owner || m.Role == GroupMember.Roles.Manager)
                             join g in _dbContext.Groups on m.GroupId equals g.Id
                             select g).AsNoTracking().ToListAsync();

        var owned = await _dbContext.Groups.Where(g => g.OwnerUserId == userId).AsNoTracking().ToListAsync();
        // Distinct by Id to avoid duplicates when the owner is also listed as manager/owner membership
        var model = managed
            .Concat(owned)
            .GroupBy(g => g.Id)
            .Select(g => g.OrderBy(x => x.CreatedAt).First())
            .OrderBy(g => g.Name)
            .ToList();

        // Member counts (Active only)
        var counts = await _dbContext.GroupMembers
            .Where(m => m.Status == GroupMember.MembershipStatuses.Active)
            .GroupBy(m => m.GroupId)
            .Select(g => new { GroupId = g.Key, Count = g.Count() })
            .ToListAsync();
        ViewBag.MemberCounts = counts.ToDictionary(x => x.GroupId, x => x.Count);

        SetPageTitle("Groups");
        return View(model);
    }

    /// <summary>
    /// Roster and invitations management for a group (owner/manager only).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Members(Guid groupId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var group = await _dbContext.Groups.FirstOrDefaultAsync(g => g.Id == groupId);
        if (group == null) return NotFound();

        var membership = await _dbContext.GroupMembers.AsNoTracking().FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId && m.Status == GroupMember.MembershipStatuses.Active);
        var isOwnerOrManager = membership != null && (membership.Role == GroupMember.Roles.Owner || membership.Role == GroupMember.Roles.Manager);
        if (!isOwnerOrManager && group.OwnerUserId != userId) return Forbid();

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

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Invite(Guid groupId, string? inviteeUserId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        try
        {
            if (string.IsNullOrWhiteSpace(inviteeUserId))
            {
                SetAlert("Please select a user to invite.", "danger");
                return RedirectToAction(nameof(Members), new { groupId });
            }
            await _invitationService.InviteUserAsync(groupId, userId, inviteeUserId, null, null);
            SetAlert("Invitation sent.");
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            SetAlert(ex.Message, "danger");
        }
        return RedirectToAction(nameof(Members), new { groupId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMember(Guid groupId, string userId)
    {
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actorId == null) return Unauthorized();
        try
        {
            await _groupService.RemoveMemberAsync(groupId, actorId, userId);
            SetAlert("Member removed.");
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            SetAlert(ex.Message, "danger");
        }
        catch (Exception ex)
        {
            SetAlert(ex.Message, "danger");
        }
        return RedirectToAction(nameof(Members), new { groupId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeInvite(Guid groupId, Guid inviteId)
    {
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actorId == null) return Unauthorized();
        try
        {
            await _invitationService.RevokeAsync(inviteId, actorId);
            SetAlert("Invitation revoked.");
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            SetAlert(ex.Message, "danger");
        }
        return RedirectToAction(nameof(Members), new { groupId });
    }
    /// <summary>
    /// Form to create a new group.
    /// </summary>
    [HttpGet]
    public IActionResult Create()
    {
        SetPageTitle("Create Group");
        return View();
    }

    /// <summary>
    /// Deletes a group (owner-only).
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmDelete(Guid id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        try
        {
            await _groupService.DeleteGroupAsync(id, userId);
            LogAudit("GroupDelete", $"Deleted group {id}", "Manager UI");
            return RedirectWithAlert("Index", "Groups", "Group deleted", "success", area: "Manager");
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // AJAX variants for members/invites
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> InviteAjax(Guid groupId, string inviteeUserId)
    {
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actorId == null) return Unauthorized();
        try
        {
            var inv = await _invitationService.InviteUserAsync(groupId, actorId, inviteeUserId, null, null);
            // SSE: notify invitee (if known) and managers; include group name
            var group = await _dbContext.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId);
            var gname = group?.Name;
            if (!string.IsNullOrEmpty(inv.InviteeUserId))
            {
                await _sse.BroadcastAsync($"invitation-update-{inv.InviteeUserId}", System.Text.Json.JsonSerializer.Serialize(new { action = "created", id = inv.Id, groupId = groupId, groupName = gname }));
            }
            await _sse.BroadcastAsync($"group-{groupId}", JsonSerializer.Serialize(GroupSseEventDto.InviteCreated(inv.Id)));
            return Ok(new { success = true, invite = new { id = inv.Id, inviteeUserId = inv.InviteeUserId, createdAt = inv.CreatedAt } });
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403, new { success = false, message = "Forbidden" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMemberAjax(Guid groupId, string userId)
    {
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actorId == null) return Unauthorized();
        try
        {
            await _groupService.RemoveMemberAsync(groupId, actorId, userId);
            await _sse.BroadcastAsync($"group-{groupId}", JsonSerializer.Serialize(GroupSseEventDto.MemberRemoved(userId)));
            var group = await _dbContext.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId);
            var gname = group?.Name;
            await _sse.BroadcastAsync($"membership-update-{userId}", System.Text.Json.JsonSerializer.Serialize(new { action = "removed", groupId, groupName = gname }));
            return Ok(new { success = true, userId });
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403, new { success = false, message = "Forbidden" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
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
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actorId == null) return Unauthorized();
        try
        {
            await _invitationService.RevokeAsync(inviteId, actorId);
            // Consolidated group channel
            await _sse.BroadcastAsync($"group-{groupId}", JsonSerializer.Serialize(GroupSseEventDto.InviteRevoked(inviteId)));
            return Ok(new { success = true, inviteId });
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403, new { success = false, message = "Forbidden" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Creates a new group owned by current user.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, string? description, string? groupType)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
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
        if (!AllowedGroupTypes.Contains(groupType))
        {
            ModelState.AddModelError("groupType", "Invalid group type.");
            return View();
        }

        try
        {
            var group = await _groupService.CreateGroupAsync(userId, name.Trim(), description);
            // canonicalize value
            group.GroupType = groupType.Equals("organization", StringComparison.OrdinalIgnoreCase)
                ? "Organization"
                : groupType.Equals("family", StringComparison.OrdinalIgnoreCase)
                    ? "Family"
                    : "Friends";
            await _dbContext.SaveChangesAsync();

            LogAudit("GroupCreate", $"Created group {name}", "Manager UI");
            return RedirectWithAlert("Index", "Groups", "Group created", "success", area: "Manager");
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError("name", ex.Message);
            return View();
        }
    }

    /// <summary>
    /// Edit group name/description (owner-only).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var group = await _dbContext.Groups.FirstOrDefaultAsync(g => g.Id == id);
        if (group == null) return NotFound();

        var isOwner = await _dbContext.GroupMembers.AnyAsync(m => m.GroupId == id && m.UserId == userId && m.Role == GroupMember.Roles.Owner && m.Status == GroupMember.MembershipStatuses.Active);
        if (!isOwner && group.OwnerUserId != userId) return Forbid();

        SetPageTitle($"Edit {group.Name}");
        return View(group);
    }

    /// <summary>
    /// Persist edits to a group (owner-only).
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, string name, string? description, string? groupType)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(name))
        {
            ModelState.AddModelError("name", "Name is required.");
            var fallback = await _dbContext.Groups.FirstOrDefaultAsync(g => g.Id == id);
            return View(fallback);
        }
        if (string.IsNullOrWhiteSpace(groupType))
        {
            ModelState.AddModelError("groupType", "Group type is required.");
            var fallback = await _dbContext.Groups.FirstOrDefaultAsync(g => g.Id == id);
            return View(fallback);
        }
        if (!AllowedGroupTypes.Contains(groupType))
        {
            ModelState.AddModelError("groupType", "Invalid group type.");
            var fallback = await _dbContext.Groups.FirstOrDefaultAsync(g => g.Id == id);
            return View(fallback);
        }

        var group = await _groupService.UpdateGroupAsync(id, userId, name.Trim(), description ?? string.Empty);
        if (group != null)
        {
            // canonicalize value
            group.GroupType = groupType.Equals("organization", StringComparison.OrdinalIgnoreCase)
                ? "Organization"
                : groupType.Equals("family", StringComparison.OrdinalIgnoreCase)
                    ? "Family"
                    : "Friends";
            await _dbContext.SaveChangesAsync();
            LogAudit("GroupUpdate", $"Updated group {group.Name}", "Manager UI");
        }

        return RedirectWithAlert("Index", "Groups", "Group updated", "success", area: "Manager");
    }

    [HttpGet]
    public async Task<IActionResult> Map(Guid groupId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        var group = await _dbContext.Groups.FirstOrDefaultAsync(g => g.Id == groupId);
        if (group == null) return NotFound();

        var membership = await _dbContext.GroupMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId && m.Status == GroupMember.MembershipStatuses.Active);
        var isOwnerOrManager = membership != null && (membership.Role == GroupMember.Roles.Owner || membership.Role == GroupMember.Roles.Manager);
        if (!isOwnerOrManager && group.OwnerUserId != userId) return Forbid();

        ViewBag.Group = group;
        ViewBag.GroupId = groupId;
        ViewBag.CurrentUserId = userId;
        var members = await (from m in _dbContext.GroupMembers
                             where m.GroupId == groupId && m.Status == GroupMember.MembershipStatuses.Active
                             join u in _dbContext.Users on m.UserId equals u.Id
                             select new { u.Id, u.UserName, u.DisplayName, m.Role, m.OrgPeerVisibilityAccessDisabled }).AsNoTracking().ToListAsync();
        ViewBag.Members = members;
        SetPageTitle($"Map - {group.Name}");
        return View();
    }
}
