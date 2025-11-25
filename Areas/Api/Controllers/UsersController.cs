using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Wayfarer.Models;
using Wayfarer.Parsers;

namespace Wayfarer.Areas.Api.Controllers;

[Area("Api")]
[Route("api/users")]
[ApiController]
public class UsersController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<UsersController> _logger;
    private readonly ILocationStatsService _statsService;

    public UsersController(ApplicationDbContext dbContext, ILogger<UsersController> logger, ILocationStatsService statsService)
    {
        _dbContext = dbContext;
        _logger = logger;
        _statsService = statsService;
    }

    /// <summary>
    /// Returns basic user info (id, userName, displayName).
    /// Available to Managers and Users for roster rendering.
    /// </summary>
    [HttpGet("{id}/basic")]
    [Authorize(Roles = "Manager,User")]
    public async Task<IActionResult> GetBasic([FromRoute] string id, CancellationToken ct)
    {
        var u = await _dbContext.Users.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { id = x.Id, userName = x.UserName, displayName = x.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (u == null) return NotFound();
        return Ok(u);
    }

    /// <summary>
    /// Deletes all locations for the given user.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    // DELETE /api/users/{userId}/locations
    [HttpDelete("{userId}/locations")]
    [Authorize]
    public async Task<IActionResult> DeleteAllUserLocations([FromRoute] string userId)
    {
        var userExists = await _dbContext.Users.AnyAsync(u => u.Id == userId);
        if (!userExists)
            return NotFound(new { message = "User not found." });

        var callerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (callerId != userId)
            return Forbid();

        try
        {
            await _dbContext.Locations
                .Where(l => l.UserId == userId)
                .ExecuteDeleteAsync();

            _logger.LogInformation("Deleted all locations for user {UserId}", userId);
            return NoContent();
        }
        catch (DbUpdateException dbEx)
        {
            _logger.LogError(dbEx, "Error deleting locations for user {UserId}", userId);
            return StatusCode(500, new { message = "Database update error." });
        }
    }
    
    /// <summary>
    /// Calculates User x Location stats
    /// </summary>
    /// GET /api/users/stats
    [HttpGet("stats")]
    public async Task<IActionResult> GetPublicStats()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId == null)
        {
            return NotFound("User not found or timeline is not public.");
        }


        // 2) Delegate all the heavy‐lifting to your stats service
        var statsDto = await _statsService.GetStatsForUserAsync(userId);

        return Ok(statsDto);
    }

    /// <summary>
    /// Calculates detailed User x Location stats including arrays of countries, regions, and cities
    /// </summary>
    /// GET /api/users/stats/detailed
    [HttpGet("stats/detailed")]
    public async Task<IActionResult> GetDetailedStats()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId == null)
        {
            return NotFound("User not found or timeline is not public.");
        }

        var detailedStatsDto = await _statsService.GetDetailedStatsForUserAsync(userId);

        return Ok(detailedStatsDto);
    }

    /// <summary>
    /// Returns recent user activity for invites and membership changes.
    /// Used to notify users after logging in (offline period).
    /// </summary>
    [HttpGet("activity")]
    [Authorize]
    public async Task<IActionResult> GetUserActivity([FromQuery] int sinceHours = 24, CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        if (sinceHours <= 0 || sinceHours > 168) sinceHours = 24;

        var since = DateTime.UtcNow.AddHours(-sinceHours);

        // Pending invites for this user (recent first)
        var pendingInvites = await _dbContext.GroupInvitations
            .Where(i => i.Status == GroupInvitation.InvitationStatuses.Pending && (i.InviteeUserId == userId || i.InviteeUserId == null))
            .OrderByDescending(i => i.CreatedAt)
            .Take(50)
            .Join(_dbContext.Groups, i => i.GroupId, g => g.Id, (i, g) => new { i.Id, i.GroupId, GroupName = g.Name, i.CreatedAt })
            .AsNoTracking()
            .ToListAsync(ct);

        // Joined in the last window
        var joined = await _dbContext.GroupMembers
            .Where(m => m.UserId == userId && m.Status == GroupMember.MembershipStatuses.Active && m.JoinedAt >= since)
            .Join(_dbContext.Groups, m => m.GroupId, g => g.Id, (m, g) => new { m.GroupId, GroupName = g.Name, m.JoinedAt })
            .AsNoTracking()
            .ToListAsync(ct);

        // Left/Removed in the last window
        var leftRemoved = await _dbContext.GroupMembers
            .Where(m => m.UserId == userId && m.LeftAt != null && m.LeftAt >= since)
            .Join(_dbContext.Groups, m => m.GroupId, g => g.Id, (m, g) => new { m.GroupId, GroupName = g.Name, m.Status, m.LeftAt })
            .AsNoTracking()
            .ToListAsync(ct);

        var removed = leftRemoved.Where(x => x.Status == GroupMember.MembershipStatuses.Removed).Select(x => x.GroupName).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
        var left = leftRemoved.Where(x => x.Status == GroupMember.MembershipStatuses.Left).Select(x => x.GroupName).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();

        return Ok(new
        {
            invites = pendingInvites.Select(x => new { x.Id, x.GroupId, x.GroupName, createdAt = x.CreatedAt }).ToList(),
            joined = joined.Select(x => new { groupName = x.GroupName, joinedAt = x.JoinedAt }).ToList(),
            removed = leftRemoved.Where(x => x.Status == GroupMember.MembershipStatuses.Removed).Select(x => new { groupName = x.GroupName, at = x.LeftAt }).ToList(),
            left = leftRemoved.Where(x => x.Status == GroupMember.MembershipStatuses.Left).Select(x => new { groupName = x.GroupName, at = x.LeftAt }).ToList()
        });
    }

    /// <summary>
    /// Search users by username or display name (case-insensitive). Returns up to 10 results.
    /// Available to Managers and Users (for inviting to Family/Friends groups).
    /// </summary>
    /// <param name="query">Search term (min 2 chars).</param>
    [HttpGet("search")]
    [Authorize(Roles = "Manager,User")]
    public async Task<IActionResult> Search([FromQuery] string? query, [FromQuery] Guid? groupId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
            return Ok(Array.Empty<object>());

        query = query.Trim();
        var users = _dbContext.Users
            .Where(u => EF.Functions.ILike(u.UserName, $"%{query}%") || EF.Functions.ILike(u.DisplayName, $"%{query}%"));

        if (groupId.HasValue)
        {
            var gid = groupId.Value;
            var pendingInviteUserIds = _dbContext.GroupInvitations
                .Where(i => i.GroupId == gid && i.Status == GroupInvitation.InvitationStatuses.Pending && i.InviteeUserId != null)
                .Select(i => i.InviteeUserId!);
            var activeMemberUserIds = _dbContext.GroupMembers
                .Where(m => m.GroupId == gid && m.Status == GroupMember.MembershipStatuses.Active)
                .Select(m => m.UserId);
            users = users.Where(u => !pendingInviteUserIds.Contains(u.Id) && !activeMemberUserIds.Contains(u.Id));
        }

        var results = await users
            .OrderBy(u => u.UserName)
            .Select(u => new { id = u.Id, userName = u.UserName, displayName = u.DisplayName })
            .Take(10)
            .AsNoTracking()
            .ToListAsync(ct);

        return Ok(results);
    }
}
