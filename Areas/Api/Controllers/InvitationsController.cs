using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;
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
        var list = await (from i in _db.GroupInvitations
                          where i.Status == GroupInvitation.InvitationStatuses.Pending
                                && (i.InviteeUserId == CurrentUserId || i.InviteeUserId == null)
                          join g in _db.Groups on i.GroupId equals g.Id
                          join u in _db.Users on i.InviterUserId equals u.Id into inviterJoin
                          from inviter in inviterJoin.DefaultIfEmpty()
                          select new
                          {
                              i.Id,
                              i.GroupId,
                              GroupName = g.Name,
                              GroupDescription = g.Description,
                              i.InviterUserId,
                              InviterUserName = inviter != null ? inviter.UserName : null,
                              InviterDisplayName = inviter != null ? inviter.DisplayName : null,
                              i.InviteeUserId,
                              i.InviteeEmail,
                              i.ExpiresAt,
                              i.CreatedAt,
                              i.Status
                          })
            .AsNoTracking()
            .ToListAsync(ct);
        return Ok(list);
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
