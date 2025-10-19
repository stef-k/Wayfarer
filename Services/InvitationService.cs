using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>
/// EF-backed invitation workflows.
/// </summary>
public class InvitationService : IInvitationService
{
    private readonly ApplicationDbContext _db;

    public InvitationService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<GroupInvitation> InviteUserAsync(Guid groupId, string inviterUserId, string? inviteeUserId, string? inviteeEmail, DateTime? expiresAt, CancellationToken ct = default)
    {
        await EnsureOwnerOrManagerAsync(groupId, inviterUserId, ct);

        if (string.IsNullOrWhiteSpace(inviteeUserId) && string.IsNullOrWhiteSpace(inviteeEmail))
            throw new ArgumentException("Either inviteeUserId or inviteeEmail must be provided");

        var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Replace("/", "_").Replace("+", "-");

        var inv = new GroupInvitation
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            InviterUserId = inviterUserId,
            InviteeUserId = inviteeUserId,
            InviteeEmail = inviteeEmail,
            Token = token,
            Status = GroupInvitation.InvitationStatuses.Pending,
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow
        };

        await _db.GroupInvitations.AddAsync(inv, ct);
        await AddAuditAsync(inviterUserId, "InviteCreate", $"Invited {inviteeUserId ?? inviteeEmail} to group {groupId}", ct);
        await _db.SaveChangesAsync(ct);
        return inv;
    }

    public async Task<GroupMember> AcceptAsync(string token, string acceptorUserId, CancellationToken ct = default)
    {
        var inv = await _db.GroupInvitations.FirstOrDefaultAsync(i => i.Token == token, ct) ?? throw new KeyNotFoundException("Invitation not found");
        if (inv.Status != GroupInvitation.InvitationStatuses.Pending) throw new InvalidOperationException("Invitation is not pending");
        if (inv.ExpiresAt.HasValue && inv.ExpiresAt.Value < DateTime.UtcNow) throw new InvalidOperationException("Invitation expired");

        inv.Status = GroupInvitation.InvitationStatuses.Accepted;
        inv.RespondedAt = DateTime.UtcNow;
        inv.InviteeUserId ??= acceptorUserId;

        // Ensure membership exists and is Active (revive if previously Left/Removed)
        var existing = await _db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == inv.GroupId && m.UserId == acceptorUserId, ct);
        GroupMember? member = existing;
        if (existing == null)
        {
            member = new GroupMember
            {
                Id = Guid.NewGuid(),
                GroupId = inv.GroupId,
                UserId = acceptorUserId,
                Role = GroupMember.Roles.Member,
                Status = GroupMember.MembershipStatuses.Active,
                JoinedAt = DateTime.UtcNow
            };
            await _db.GroupMembers.AddAsync(member, ct);
        }
        else if (!string.Equals(existing.Status, GroupMember.MembershipStatuses.Active, StringComparison.Ordinal))
        {
            existing.Status = GroupMember.MembershipStatuses.Active;
            existing.JoinedAt = existing.JoinedAt == default ? DateTime.UtcNow : existing.JoinedAt;
            existing.LeftAt = null;
            if (string.IsNullOrWhiteSpace(existing.Role)) existing.Role = GroupMember.Roles.Member;
        }

        await AddAuditAsync(acceptorUserId, "InviteAccept", $"Accepted invite for group {inv.GroupId}", ct);
        await _db.SaveChangesAsync(ct);
        return member ?? await _db.GroupMembers.FirstAsync(m => m.GroupId == inv.GroupId && m.UserId == acceptorUserId, ct);
    }

    public async Task DeclineAsync(string token, string userId, CancellationToken ct = default)
    {
        var inv = await _db.GroupInvitations.FirstOrDefaultAsync(i => i.Token == token, ct) ?? throw new KeyNotFoundException("Invitation not found");
        if (inv.Status != GroupInvitation.InvitationStatuses.Pending) throw new InvalidOperationException("Invitation is not pending");

        inv.Status = GroupInvitation.InvitationStatuses.Declined;
        inv.RespondedAt = DateTime.UtcNow;
        await AddAuditAsync(userId, "InviteDecline", $"Declined invite for group {inv.GroupId}", ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task RevokeAsync(Guid invitationId, string actorUserId, CancellationToken ct = default)
    {
        var inv = await _db.GroupInvitations.FirstOrDefaultAsync(i => i.Id == invitationId, ct) ?? throw new KeyNotFoundException("Invitation not found");
        await EnsureOwnerOrManagerAsync(inv.GroupId, actorUserId, ct);

        inv.Status = GroupInvitation.InvitationStatuses.Revoked;
        inv.RespondedAt = DateTime.UtcNow;
        await AddAuditAsync(actorUserId, "InviteRevoke", $"Revoked invite {inv.Id}", ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task EnsureOwnerOrManagerAsync(Guid groupId, string actorUserId, CancellationToken ct)
    {
        var membership = await _db.GroupMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == actorUserId && m.Status == GroupMember.MembershipStatuses.Active, ct);
        var isOwnerOrManager = membership != null && (membership.Role == GroupMember.Roles.Owner || membership.Role == GroupMember.Roles.Manager);
        if (!isOwnerOrManager) throw new UnauthorizedAccessException("Manager or Owner permissions required");
    }

    private async Task AddAuditAsync(string userId, string action, string details, CancellationToken ct)
    {
        var audit = new AuditLog
        {
            UserId = userId,
            Action = action,
            Details = details,
            Timestamp = DateTime.UtcNow
        };
        await _db.AuditLogs.AddAsync(audit, ct);
    }
}
