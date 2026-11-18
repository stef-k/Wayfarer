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

/// <summary>Bearer inbox, current admission disclosure and recipient responses.</summary>
[Route("api/mobile/invitations")]
public sealed class MobileInvitationsController(ApplicationDbContext db, InvitationService invitations,
    IDataProtectionProvider protection, SseService sse) : MobileGroupWorkflowController(db)
{
    private readonly GroupInvitationPage _pages = new(protection);

    /// <summary>Lists live invitations for the exact bearer, including accounts with zero Groups.</summary>
    [HttpGet]
    public Task<IActionResult> Inbox([FromQuery] string? cursor, CancellationToken ct)
    {
        if (!_pages.TryDecode(cursor ?? (Request.Query.ContainsKey("cursor") ? "" : null), out var continuation))
            return Task.FromResult<IActionResult>(BadRequest());

        return ReadAsync(async actor =>
        {
            var live = GroupInvitationQueries.Live(Db, DateTime.UtcNow).Where(i => i.InviteeUserId == actor);
            return Ok(await _pages.ReadAsync(live, actor, null, continuation, ct));
        }, ct);
    }

    /// <summary>Returns live disclosure or the minimal identity and status of a terminal invitation.</summary>
    [HttpGet("{id}")]
    public Task<IActionResult> Detail(Guid id, CancellationToken ct) =>
        ReadAsync(async actor =>
        {
            var now = GroupMembershipEpisode.AtStoredPrecision(DateTime.UtcNow);
            var scoped = Db.GroupInvitations.AsNoTracking().Where(i => i.Id == id && i.InviteeUserId == actor
                && i.Group != null && !i.Group.IsArchived);
            var invitation = await scoped.Include(i => i.Group).SingleOrDefaultAsync(ct)
                ?? throw new GroupRefusal(404, "invitation_unavailable");
            var status = GroupInvitationQueries.Status(invitation, now);
            if (status != GroupInvitation.InvitationStatuses.Pending)
                return Ok(new { id, groupId = invitation.GroupId, status });

            var item = await GroupInvitationPage.RecipientItems(scoped).SingleAsync(ct);
            var member = await Db.GroupMembers.AsNoTracking().SingleOrDefaultAsync(
                m => m.GroupId == invitation.GroupId && m.UserId == actor, ct);
            var disclosure = invitation.Group is { } group && GroupMembershipEpisode.CanAdmit(member, invitation.CreatedAt, now)
                ? GroupSharingPolicy.Disclosure(group, id, member)
                : null;
            return Ok(new
            {
                item.Id, item.GroupId, item.Status, item.CreatedAtUtc, item.ExpiresAtUtc,
                item.GroupName, item.GroupType, item.InviterUserName, item.InviterDisplayName, disclosure
            });
        }, ct);

    /// <summary>Accepts only the current reviewed consequences for the addressed account.</summary>
    [HttpPost("{id}/accept")]
    public Task<IActionResult> Accept(Guid id, MobileGroupAcceptRequest request, CancellationToken ct)
    {
        if (!request.HasValidReview()) return Task.FromResult<IActionResult>(BadRequest());

        return WithBearerAsync(async (actor, bearer) =>
        {
            var member = await invitations.AdmitAsync(id, actor, request.Expected, ct, bearer);
            await sse.BroadcastGroupNotificationAsync(actor, SseService.InvitationStateHint);
            await sse.BroadcastGroupNotificationAsync(actor, SseService.MembershipStateHint);
            await sse.BroadcastGroupHintAsync(member.GroupId, JsonSerializer.Serialize(GroupSseEventDto.MemberJoined(actor, id)));
            return Ok(new { groupId = member.GroupId });
        }, ct);
    }

    /// <summary>Declines even when an admission disclosure is unavailable.</summary>
    [HttpPost("{id}/decline")]
    public Task<IActionResult> Decline(Guid id, CancellationToken ct)
    {
        if (HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
            return Task.FromResult<IActionResult>(BadRequest());

        return WithBearerAsync(async (actor, bearer) =>
        {
            var groupId = await invitations.DeclineAsync(id, actor, ct, bearer);
            await sse.BroadcastGroupNotificationAsync(actor, SseService.InvitationStateHint);
            await sse.BroadcastGroupHintAsync(groupId, JsonSerializer.Serialize(GroupSseEventDto.InviteDeclined(actor, id)));
            return NoContent();
        }, ct);
    }
}
