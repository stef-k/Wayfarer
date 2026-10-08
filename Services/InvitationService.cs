using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>EF-backed invitations to registered users, serialized with membership departures.</summary>
public class InvitationService : IInvitationService
{
    private readonly ApplicationDbContext _db;

    /// <summary>Uses the request's database context for each atomic invitation workflow.</summary>
    public InvitationService(ApplicationDbContext db) => _db = db;

    /// <summary>
    /// Invites one registered user who is not an active member. Group-first locking orders
    /// creation with removal/leave; the pending-recipient unique index remains a final guard.
    /// </summary>
    /// <param name="groupId">Destination group.</param>
    /// <param name="inviterUserId">Active Group Owner or Manager issuing the invitation.</param>
    /// <param name="inviteeUserId">Required existing account ID selected by username/display-name search.</param>
    /// <param name="expiresAt">Optional expiration timestamp.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<GroupInvitation> InviteUserAsync(Guid groupId, string inviterUserId, string inviteeUserId,
        DateTime? expiresAt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(inviteeUserId))
            throw new ArgumentException("InviteeUserId required");

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await GroupService.LockMembershipAsync(_db, groupId, ct);
        await EnsureOwnerOrManagerAsync(groupId, inviterUserId, ct);
        if (!await _db.Users.AnyAsync(u => u.Id == inviteeUserId, ct))
            throw new ArgumentException("InviteeUserId must identify an existing user");
        if (await _db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == inviteeUserId
            && m.Status == GroupMember.MembershipStatuses.Active, ct))
            throw new InvalidOperationException("User already an active member");
        if (await _db.GroupInvitations.AnyAsync(i => i.GroupId == groupId && i.InviteeUserId == inviteeUserId
            && i.Status == GroupInvitation.InvitationStatuses.Pending, ct))
            throw new InvalidOperationException("A pending invitation already exists for this user in the specified group");

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("/", "_").Replace("+", "-").TrimEnd('=');
        var invitation = new GroupInvitation
        {
            Id = Guid.NewGuid(), GroupId = groupId, InviterUserId = inviterUserId,
            InviteeUserId = inviteeUserId, Token = token, ExpiresAt = expiresAt,
            Status = GroupInvitation.InvitationStatuses.Pending, CreatedAt = DateTime.UtcNow
        };
        await _db.GroupInvitations.AddAsync(invitation, ct);
        await AddAuditAsync(inviterUserId, "InviteCreate", $"Invited {inviteeUserId} to group {groupId}", ct);
        try
        {
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            throw new InvalidOperationException("A pending invitation already exists for this user in the specified group", ex);
        }
        return invitation;
    }

    /// <summary>
    /// Only the exact designated recipient may accept. A departure invalidates all older
    /// invitations independently of their status. Reactivation grants only Member authority
    /// and retains personal sharing preferences. The Group lock is held through commit.
    /// </summary>
    /// <param name="token">Invitation token.</param>
    /// <param name="acceptorUserId">Authenticated recipient ID.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<GroupMember> AcceptAsync(string token, string acceptorUserId, CancellationToken ct = default)
    {
        // Resolve immutable routing data before starting the locking transaction; never trust its state.
        var routing = await _db.GroupInvitations.AsNoTracking().FirstOrDefaultAsync(i => i.Token == token, ct)
            ?? throw new KeyNotFoundException("Invitation not found");
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var invitation = await LockInvitationAsync(routing, ct);
        EnsureRecipient(invitation, acceptorUserId);
        EnsurePending(invitation);
        var now = DateTime.UtcNow;
        if (invitation.ExpiresAt.HasValue && invitation.ExpiresAt.Value < now)
            throw new InvalidOperationException("Invitation expired");
        if (!await _db.Groups.AnyAsync(g => g.Id == invitation.GroupId && !g.IsArchived, ct))
            throw new InvalidOperationException("The group associated with this invitation is no longer available");

        var member = await _db.GroupMembers.FirstOrDefaultAsync(
            m => m.GroupId == invitation.GroupId && m.UserId == acceptorUserId, ct);
        if (member is not null)
        {
            // Controllers or earlier operations may already track this row. Reload after acquiring the lock.
            await _db.Entry(member).ReloadAsync(ct);
            if (member.Status == GroupMember.MembershipStatuses.Active)
                throw new InvalidOperationException("User already an active member");
            if (member.Status is not (GroupMember.MembershipStatuses.Left or GroupMember.MembershipStatuses.Removed)
                || member.JoinedAt == default || member.LeftAt is null || member.LeftAt < member.JoinedAt
                || member.LeftAt > now || invitation.CreatedAt <= member.LeftAt)
                throw new InvalidOperationException("Invitation predates departure or membership history is unavailable");
        }
        else
        {
            member = new GroupMember
            {
                Id = Guid.NewGuid(), GroupId = invitation.GroupId, UserId = acceptorUserId
            };
            await _db.GroupMembers.AddAsync(member, ct);
        }

        member.Status = GroupMember.MembershipStatuses.Active;
        member.Role = GroupMember.Roles.Member;
        member.JoinedAt = now;
        member.LeftAt = null;
        invitation.Status = GroupInvitation.InvitationStatuses.Accepted;
        invitation.RespondedAt = now;
        await AddAuditAsync(acceptorUserId, "InviteAccept", $"Accepted invite for group {invitation.GroupId}", ct);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return member;
    }

    /// <summary>Only an explicitly addressed recipient can decline; unresolved legacy rows fail closed.</summary>
    public async Task DeclineAsync(string token, string userId, CancellationToken ct = default)
    {
        var routing = await _db.GroupInvitations.AsNoTracking().FirstOrDefaultAsync(i => i.Token == token, ct)
            ?? throw new KeyNotFoundException("Invitation not found");
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var invitation = await LockInvitationAsync(routing, ct);
        EnsureRecipient(invitation, userId);
        EnsurePending(invitation);
        invitation.Status = GroupInvitation.InvitationStatuses.Declined;
        invitation.RespondedAt = DateTime.UtcNow;
        await AddAuditAsync(userId, "InviteDecline", $"Declined invite for group {invitation.GroupId}", ct);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>Active Owners/Managers can revoke a pending invitation, including unresolved historical rows.</summary>
    public async Task<InvitationRevocation> RevokeAsync(Guid invitationId, string actorUserId, CancellationToken ct = default)
    {
        var routing = await _db.GroupInvitations.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invitationId, ct)
            ?? throw new KeyNotFoundException("Invitation not found");
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var invitation = await LockInvitationAsync(routing, ct);
        await EnsureOwnerOrManagerAsync(invitation.GroupId, actorUserId, ct);
        EnsurePending(invitation);
        invitation.Status = GroupInvitation.InvitationStatuses.Revoked;
        invitation.RespondedAt = DateTime.UtcNow;
        await AddAuditAsync(actorUserId, "InviteRevoke", $"Revoked invite {invitation.Id}", ct);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new InvitationRevocation(invitation.GroupId, invitation.InviteeUserId);
    }

    /// <summary>
    /// Locks Group before invitation, matching membership mutation and SSE ordering.
    /// ReadCommitted reads after the lock see a departure that committed while this request waited.
    /// Reload avoids accepting cached state from a controller's earlier lookup.
    /// </summary>
    private async Task<GroupInvitation> LockInvitationAsync(GroupInvitation routing, CancellationToken ct)
    {
        await GroupService.LockMembershipAsync(_db, routing.GroupId, ct);
        var invitation = await _db.GroupInvitations.FirstOrDefaultAsync(i => i.Id == routing.Id, ct)
            ?? throw new KeyNotFoundException("Invitation not found");
        await _db.Entry(invitation).ReloadAsync(ct);
        return invitation;
    }

    /// <summary>Neither a token nor a null/blank legacy recipient establishes account authority.</summary>
    private static void EnsureRecipient(GroupInvitation invitation, string userId)
    {
        if (string.IsNullOrWhiteSpace(invitation.InviteeUserId) || string.IsNullOrWhiteSpace(userId)
            || !string.Equals(invitation.InviteeUserId, userId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Only the designated invitee can respond to this invitation");
    }

    /// <summary>Only pending invitations can transition.</summary>
    private static void EnsurePending(GroupInvitation invitation)
    {
        if (invitation.Status != GroupInvitation.InvitationStatuses.Pending)
            throw new InvalidOperationException("Invitation is not pending");
    }

    /// <summary>Ownership metadata does not grant residual permission after departure or archiving.</summary>
    private async Task EnsureOwnerOrManagerAsync(Guid groupId, string actorUserId, CancellationToken ct)
    {
        if (!await _db.Groups.AnyAsync(g => g.Id == groupId && !g.IsArchived, ct))
            throw new KeyNotFoundException("Group not found");
        var membership = await _db.GroupMembers.AsNoTracking().FirstOrDefaultAsync(m => m.GroupId == groupId
            && m.UserId == actorUserId && m.Status == GroupMember.MembershipStatuses.Active, ct);
        if (!GroupLocationVisibility.CanManage(membership))
            throw new UnauthorizedAccessException("Manager or Owner permissions required");
    }

    /// <summary>Adds the audit entry to the caller's atomic save.</summary>
    private async Task AddAuditAsync(string userId, string action, string details, CancellationToken ct) =>
        await _db.AuditLogs.AddAsync(new AuditLog
        {
            UserId = userId, Action = action, Details = details, Timestamp = DateTime.UtcNow
        }, ct);

    /// <summary>Recognizes PostgreSQL's final duplicate-recipient constraint guard.</summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
