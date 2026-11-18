using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>Bearer creation, scoped invitations, Leave and readback of personal sharing.</summary>
[Route("api/mobile/groups")]
public sealed class MobileGroupManagementController(ApplicationDbContext db, GroupService groups,
    InvitationService invitations, IDataProtectionProvider protection, SseService sse) : MobileGroupWorkflowController(db)
{
    private readonly GroupInvitationPage _pages = new(protection);

    /// <summary>Creates one Family or Friends Group with its active Owner membership.</summary>
    [HttpPost]
    public Task<IActionResult> Create(MobileGroupCreateRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (name is null || name.Length is < 1 or > 200 || request.Description?.Length > 1000
            || request.GroupType is not ("Family" or "Friends"))
            return Task.FromResult<IActionResult>(BadRequest());

        return WithBearerAsync(async (actor, bearer) =>
        {
            var group = await groups.CreateAsync(actor, name, request.Description, request.GroupType, ct, bearer);
            return StatusCode(201, new { groupId = group.Id, groupType = group.GroupType });
        }, ct);
    }

    /// <summary>Returns the active accounts eligible for selection in the exact Group.</summary>
    [HttpGet("{groupId}/invite-candidates")]
    public Task<IActionResult> InviteCandidates(Guid groupId, [FromQuery] string? query, CancellationToken ct)
    {
        query = query?.Trim();
        if (query is null || query.Length is < 2 or > 100)
            return Task.FromResult<IActionResult>(BadRequest());

        return ReadAsync(async actor =>
        {
            await GroupRead.ScopeAsync(Db, groupId, actor, manage: true, ct);
            return Ok(await GroupInvitationQueries.CandidatesAsync(Db, groupId, query, DateTime.UtcNow, ct));
        }, ct);
    }

    /// <summary>Lists only live registered-account invitations in the authorized Group.</summary>
    [HttpGet("{groupId}/invitations")]
    public Task<IActionResult> Invitations(Guid groupId, [FromQuery] string? cursor, CancellationToken ct)
    {
        if (!_pages.TryDecode(cursor ?? (Request.Query.ContainsKey("cursor") ? "" : null), out var continuation))
            return Task.FromResult<IActionResult>(BadRequest());

        return ReadAsync(async actor =>
        {
            await GroupRead.ScopeAsync(Db, groupId, actor, manage: true, ct);
            var live = GroupInvitationQueries.Live(Db, DateTime.UtcNow)
                .Where(i => i.GroupId == groupId && i.Invitee != null);
            return Ok(await _pages.ReadAsync(live, actor, groupId, continuation, ct));
        }, ct);
    }

    /// <summary>Issues a no-expiry invitation to the selected active account.</summary>
    [HttpPost("{groupId}/invitations")]
    public Task<IActionResult> Invite(Guid groupId, MobileGroupInviteRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.InviteeUserId))
            return Task.FromResult<IActionResult>(BadRequest());

        return WithBearerAsync(async (actor, bearer) =>
        {
            var invitation = await invitations.IssueAsync(groupId, actor, request.InviteeUserId, null, ct, bearer);
            await sse.BroadcastGroupNotificationAsync(request.InviteeUserId, SseService.InvitationStateHint);
            await sse.BroadcastGroupHintAsync(groupId, JsonSerializer.Serialize(GroupSseEventDto.InviteCreated(invitation.Id)));
            return StatusCode(201, new { id = invitation.Id });
        }, ct);
    }

    /// <summary>Revokes the live invitation only in the exact route Group.</summary>
    [HttpPost("{groupId}/invitations/{id}/revoke")]
    public Task<IActionResult> Revoke(Guid groupId, Guid id, CancellationToken ct)
    {
        if (HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
            return Task.FromResult<IActionResult>(BadRequest());

        return WithBearerAsync(async (actor, bearer) =>
        {
            var revoked = await invitations.RevokeAsync(groupId, id, actor, legacyCleanup: false, ct, bearer);
            await sse.BroadcastInvitationRevocationAsync(groupId, revoked.InviteeUserId,
                JsonSerializer.Serialize(GroupSseEventDto.InviteRevoked(id)));
            return NoContent();
        }, ct);
    }

    /// <summary>Returns the effect the active caller must review before leaving this Group.</summary>
    [HttpGet("{groupId}/leave")]
    public Task<IActionResult> PreviewLeave(Guid groupId, [FromQuery] string? successorUserId, CancellationToken ct)
    {
        if (Request.Query.ContainsKey("successorUserId") && string.IsNullOrWhiteSpace(successorUserId))
            return Task.FromResult<IActionResult>(BadRequest());

        return ReadAsync(async actor => Ok(await groups.PreviewLeaveAsync(groupId, actor, successorUserId, ct)), ct);
    }

    /// <summary>Leaves only when the current episode and impact match the reviewed effect.</summary>
    [HttpPost("{groupId}/leave")]
    public Task<IActionResult> Leave(Guid groupId, MobileGroupLeaveRequest request, CancellationToken ct)
    {
        if (!request.HasValidReview()) return Task.FromResult<IActionResult>(BadRequest());

        return WithBearerAsync(async (actor, bearer) =>
        {
            var result = await groups.LeaveAsync(groupId, actor, request.Expected, request.SuccessorUserId, ct, bearer);
            await sse.BroadcastGroupHintAsync(groupId, JsonSerializer.Serialize(GroupSseEventDto.MemberLeft(actor)));
            await sse.BroadcastGroupNotificationAsync(actor, SseService.MembershipStateHint);
            return Ok(result);
        }, ct);
    }

    /// <summary>Returns stored preference and effective sharing from the current membership snapshot.</summary>
    [HttpGet("{groupId}/peer-visibility")]
    public Task<IActionResult> PeerVisibility(Guid groupId, CancellationToken ct) =>
        ReadAsync(async actor =>
        {
            var (group, member) = await GroupRead.ScopeAsync(Db, groupId, actor, manage: false, ct);
            return Ok(GroupSharingPolicy.Personal(group, member));
        }, ct);
}
