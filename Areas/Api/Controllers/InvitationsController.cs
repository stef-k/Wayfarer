using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Parsers;

namespace Wayfarer.Areas.Api.Controllers;

[Area("Api")]
[Route("api/invitations")]
[ApiController]
[Authorize]
public class InvitationsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IInvitationService _invites;
    private readonly ILogger<InvitationsController> _logger;
    private readonly SseService _sse;

    [ActivatorUtilitiesConstructor]
    public InvitationsController(ApplicationDbContext db, IInvitationService invites, ILogger<InvitationsController> logger, SseService sse)
    {
        _db = db;
        _invites = invites;
        _logger = logger;
        _sse = sse;
    }

    // Backward-compatible ctor for existing tests
    public InvitationsController(ApplicationDbContext db, IInvitationService invites, ILogger<InvitationsController> logger)
        : this(db, invites, logger, new SseService())
    {
    }

    private string? CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier);

    // GET /api/invitations -> pending invitations for current user
    [HttpGet]
    public async Task<IActionResult> ListForCurrentUser(CancellationToken ct)
    {
        if (CurrentUserId is null) return Unauthorized();
        try
        {
            var invites = await _db.GroupInvitations
                .Where(i => i.Status == GroupInvitation.InvitationStatuses.Pending && (i.InviteeUserId == CurrentUserId || i.InviteeUserId == null))
                .AsNoTracking()
                .ToListAsync(ct);

            var groupIds = invites.Select(i => i.GroupId).Distinct().ToList();
            var groups = await _db.Groups.Where(g => groupIds.Contains(g.Id))
                .Select(g => new { g.Id, g.Name, g.Description })
                .AsNoTracking().ToListAsync(ct);
            var groupMap = groups.ToDictionary(g => g.Id, g => g);

            var inviterIds = invites.Select(i => i.InviterUserId).Distinct().ToList();
            var inviters = await _db.ApplicationUsers.Where(u => inviterIds.Contains(u.Id))
                .Select(u => new { u.Id, u.UserName, u.DisplayName })
                .AsNoTracking().ToListAsync(ct);
            var inviterMap = inviters.ToDictionary(u => u.Id, u => u);

            var payload = invites.Select(i => new
            {
                i.Id,
                i.GroupId,
                GroupName = groupMap.TryGetValue(i.GroupId, out var g) ? g.Name : null,
                GroupDescription = groupMap.TryGetValue(i.GroupId, out var g2) ? g2.Description : null,
                i.InviterUserId,
                InviterUserName = inviterMap.TryGetValue(i.InviterUserId, out var inv) ? inv.UserName : null,
                InviterDisplayName = inviterMap.TryGetValue(i.InviterUserId, out var inv2) ? inv2.DisplayName : null,
                i.InviteeUserId,
                i.InviteeEmail,
                i.ExpiresAt,
                i.CreatedAt,
                i.Status
            }).ToList();

            return Ok(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list invitations for current user");
            return BadRequest(new { message = "Failed to load invitations." });
        }
    }

    // POST /api/invitations -> create by manager/owner
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] InvitationCreateRequest req, CancellationToken ct)
    {
        if (CurrentUserId is null) return Unauthorized();
        if (req.GroupId == Guid.Empty) return BadRequest(new { message = "GroupId required" });
        if (string.IsNullOrWhiteSpace(req.InviteeUserId) && string.IsNullOrWhiteSpace(req.InviteeEmail))
            return BadRequest(new { message = "InviteeUserId or InviteeEmail required" });

        try
        {
            var inv = await _invites.InviteUserAsync(req.GroupId, CurrentUserId, req.InviteeUserId, req.InviteeEmail, req.ExpiresAt, ct);
            // Notify invitee if known
            if (!string.IsNullOrEmpty(inv.InviteeUserId))
            {
                await _sse.BroadcastAsync($"invitation-update-{inv.InviteeUserId}", JsonSerializer.Serialize(new { action = "created", id = inv.Id }));
            }
            // Inform managers of new pending invite
            await _sse.BroadcastAsync($"group-membership-update-{inv.GroupId}", JsonSerializer.Serialize(new { action = "invite-created", id = inv.Id }));
            return Ok(new { inv.Id, inv.GroupId, inv.Status, inv.InviteeUserId, inv.InviteeEmail });
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invitation creation failed");
            return BadRequest(new { message = ex.Message });
        }
    }

    // POST /api/invitations/{id}/accept
    [HttpPost("{id}/accept")]
    public async Task<IActionResult> Accept([FromRoute] Guid id, CancellationToken ct)
    {
        if (CurrentUserId is null) return Unauthorized();
        var inv = await _db.GroupInvitations.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (inv == null) return NotFound();
        try
        {
            await _invites.AcceptAsync(inv.Token, CurrentUserId, ct);
            await _sse.BroadcastAsync($"invitation-update-{CurrentUserId}", JsonSerializer.Serialize(new { action = "accepted", id }));
            // Inform managers watching the group
            await _sse.BroadcastAsync($"group-membership-update-{inv.GroupId}", JsonSerializer.Serialize(new { action = "member-joined", userId = CurrentUserId, invitationId = id }));
            return Ok(new { message = "Accepted" });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("expired") || ex.Message.Contains("not pending"))
        {
            return Conflict(new { message = ex.Message });
        }
    }

    // POST /api/invitations/{id}/decline
    [HttpPost("{id}/decline")]
    public async Task<IActionResult> Decline([FromRoute] Guid id, CancellationToken ct)
    {
        if (CurrentUserId is null) return Unauthorized();
        var inv = await _db.GroupInvitations.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (inv == null) return NotFound();

        await _invites.DeclineAsync(inv.Token, CurrentUserId, ct);
        await _sse.BroadcastAsync($"invitation-update-{CurrentUserId}", JsonSerializer.Serialize(new { action = "declined", id }));
        // Inform managers watching the group
        await _sse.BroadcastAsync($"group-membership-update-{inv.GroupId}", JsonSerializer.Serialize(new { action = "invite-declined", userId = CurrentUserId, invitationId = id }));
        return Ok(new { message = "Declined" });
    }
}
