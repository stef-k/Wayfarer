using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services;

public partial class InvitationService
{
    /// <summary>Issues a registered-account invitation using the Web expiry input and refusal convention.</summary>
    public async Task<GroupInvitation> InviteUserAsync(Guid groupId, string inviterUserId, string inviteeUserId,
        DateTime? expiresAt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(inviteeUserId)) throw new ArgumentException("InviteeUserId required");
        try { return await IssueAsync(groupId, inviterUserId, inviteeUserId, expiresAt, ct); }
        catch (GroupRefusal refusal) { throw WebError(refusal); }
    }

    /// <summary>Applies the shared admission rules to the existing Web token lookup.</summary>
    public async Task<GroupMember> AcceptAsync(string token, string acceptorUserId, CancellationToken ct = default)
    {
        var id = await IdForTokenAsync(token, ct);
        try { return await AdmitAsync(id, acceptorUserId, null, ct); }
        catch (GroupRefusal refusal) { throw WebError(refusal); }
    }

    /// <summary>Declines using the existing Web token lookup and shared lifecycle rules.</summary>
    public async Task DeclineAsync(string token, string userId, CancellationToken ct = default)
    {
        var id = await IdForTokenAsync(token, ct);
        try { await DeclineAsync(id, userId, ct); }
        catch (GroupRefusal refusal) { throw WebError(refusal); }
    }

    /// <summary>Revokes under current Group authority, including Web's legacy cleanup path.</summary>
    public async Task<InvitationRevocation> RevokeAsync(Guid invitationId, string actorUserId, CancellationToken ct = default)
    {
        try
        {
            var invitation = await RouteAsync(invitationId, ct);
            return await RevokeAsync(invitation.GroupId, invitationId, actorUserId, legacyCleanup: true, ct);
        }
        catch (GroupRefusal refusal) { throw WebError(refusal); }
    }

    /// <summary>Resolves the legacy token to an ID without establishing recipient authority.</summary>
    private async Task<Guid> IdForTokenAsync(string token, CancellationToken ct) =>
        await db.GroupInvitations.Where(i => i.Token == token).Select(i => (Guid?)i.Id).SingleOrDefaultAsync(ct)
        ?? throw new KeyNotFoundException("Invitation not found");

    /// <summary>Preserves the Web adapters' existing exception and message contracts.</summary>
    private static Exception WebError(GroupRefusal refusal) => refusal.Code switch
    {
        "invitee_unavailable" => new ArgumentException("InviteeUserId must identify an existing user", refusal),
        "already_member" => new InvalidOperationException("User already an active member"),
        "pending_exists" => new InvalidOperationException("A pending invitation already exists for this user in the specified group"),
        "invitation_expired" => new InvalidOperationException("Invitation expired"),
        "invitation_not_pending" => new InvalidOperationException("Invitation is not pending"),
        "admission_ineligible" => new InvalidOperationException("Invitation predates departure or membership history is unavailable"),
        "invitation_group_unavailable" => new InvalidOperationException("The group associated with this invitation is no longer available"),
        "invitation_unaddressed" => new UnauthorizedAccessException("Only the designated invitee can respond to this invitation"),
        "group_unavailable" => new KeyNotFoundException("Group not found"),
        "invitation_unavailable" => new KeyNotFoundException("Invitation not found"),
        _ => new UnauthorizedAccessException("Manager or Owner permissions required")
    };
}
