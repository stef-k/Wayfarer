using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>
/// Service for managing group invitations and responses.
/// </summary>
public interface IInvitationService
{
    Task<GroupInvitation> InviteUserAsync(Guid groupId, string inviterUserId, string? inviteeUserId, string? inviteeEmail, DateTime? expiresAt, CancellationToken ct = default);
    Task<GroupMember> AcceptAsync(string token, string acceptorUserId, CancellationToken ct = default);
    Task DeclineAsync(string token, string userId, CancellationToken ct = default);
    Task RevokeAsync(Guid invitationId, string actorUserId, CancellationToken ct = default);
}

