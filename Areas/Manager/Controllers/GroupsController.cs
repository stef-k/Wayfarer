using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
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

    public GroupsController(ILogger<BaseController> logger, ApplicationDbContext dbContext, IGroupService groupService)
        : base(logger, dbContext)
    {
        _groupService = groupService;
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
            .Select(g => g.First())
            .OrderBy(g => g.Name)
            .ToList();

        SetPageTitle("Groups");
        return View(model);
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
    /// Delete confirmation view (owner-only).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        var group = await _dbContext.Groups.FirstOrDefaultAsync(g => g.Id == id);
        if (group == null) return NotFound();

        var isOwner = await _dbContext.GroupMembers.AnyAsync(m => m.GroupId == id && m.UserId == userId && m.Role == GroupMember.Roles.Owner && m.Status == GroupMember.MembershipStatuses.Active)
                      || group.OwnerUserId == userId;
        if (!isOwner) return Forbid();

        SetPageTitle($"Delete {group.Name}");
        return View(group);
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

        try
        {
            var group = await _groupService.CreateGroupAsync(userId, name.Trim(), description);
            group.GroupType = groupType;
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

        var group = await _groupService.UpdateGroupAsync(id, userId, name.Trim(), description ?? string.Empty);
        if (group != null)
        {
            group.GroupType = groupType;
            await _dbContext.SaveChangesAsync();
            LogAudit("GroupUpdate", $"Updated group {group.Name}", "Manager UI");
        }

        return RedirectWithAlert("Index", "Groups", "Group updated", "success", area: "Manager");
    }
}
