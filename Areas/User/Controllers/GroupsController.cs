using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Areas.User.Controllers;

/// <summary>
/// User UI for listing joined groups and viewing the group map.
/// </summary>
[Area("User")]
[Authorize(Roles = "User")]
public class GroupsController : BaseController
{
    public GroupsController(ILogger<BaseController> logger, ApplicationDbContext dbContext)
        : base(logger, dbContext)
    {
    }

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

        ViewBag.Joined = joined;
        SetPageTitle("My Groups");
        return View();
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
            .AnyAsync(m => m.GroupId == groupId && m.UserId == userId && m.Status == GroupMember.MembershipStatuses.Active);
        if (!isMember) return Forbid();

        ViewBag.Group = group;
        ViewBag.GroupId = groupId;
        var members = await (from m in _dbContext.GroupMembers
                             where m.GroupId == groupId && m.Status == GroupMember.MembershipStatuses.Active
                             join u in _dbContext.Users on m.UserId equals u.Id
                             select new { u.Id, u.UserName, u.DisplayName, m.Role }).AsNoTracking().ToListAsync();
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
