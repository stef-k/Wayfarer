using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>
/// Service for managing group invitations and responses.
/// </summary>
public interface IInvitationService
{
    /// <summary>Invites one existing registered account; active members cannot be invited again.</summary>
    Task<GroupInvitation> InviteUserAsync(Guid groupId, string inviterUserId, string inviteeUserId, DateTime? expiresAt, CancellationToken ct = default);
    /// <summary>Accepts only for the addressed recipient and only after their latest departure.</summary>
    Task<GroupMember> AcceptAsync(string token, string acceptorUserId, CancellationToken ct = default);
    /// <summary>Declines only for the explicitly addressed recipient.</summary>
    Task DeclineAsync(string token, string userId, CancellationToken ct = default);
    /// <summary>Revokes a pending invitation under active Owner/Manager authority.</summary>
    Task<InvitationRevocation> RevokeAsync(Guid invitationId, string actorUserId, CancellationToken ct = default);
}

/// <summary>Authoritative identities retained after an invitation revocation commits.</summary>
public sealed record InvitationRevocation(Guid GroupId, string? InviteeUserId);
