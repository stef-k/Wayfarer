using System.Data;
using System.Security.Cryptography;
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

    /// <summary>
    /// Creates a new invitation for a user to join a group.
    /// Uses a database transaction with RepeatableRead isolation to prevent race conditions
    /// where duplicate invitations could be created between the check and insert.
    /// </summary>
    /// <param name="groupId">The group to invite the user to.</param>
    /// <param name="inviterUserId">The user creating the invitation (must be Owner or Manager).</param>
    /// <param name="inviteeUserId">The user ID being invited (optional if email provided).</param>
    /// <param name="inviteeEmail">The email of the user being invited (optional if userId provided).</param>
    /// <param name="expiresAt">Optional expiration date for the invitation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created GroupInvitation entity.</returns>
    /// <exception cref="ArgumentException">Thrown when neither inviteeUserId nor inviteeEmail is provided.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a pending invitation already exists for this user.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the inviter lacks required permissions.</exception>
    public async Task<GroupInvitation> InviteUserAsync(Guid groupId, string inviterUserId, string? inviteeUserId, string? inviteeEmail, DateTime? expiresAt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(inviteeUserId) && string.IsNullOrWhiteSpace(inviteeEmail))
            throw new ArgumentException("Either inviteeUserId or inviteeEmail must be provided");

        // Use RepeatableRead isolation to prevent race conditions:
        // - Ensures the duplicate check and insert are atomic
        // - Prevents another thread from creating a duplicate invitation mid-operation
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

        try
        {
            // Step 1: Verify permissions within transaction
            await EnsureOwnerOrManagerAsync(groupId, inviterUserId, ct);

            // Step 2: Check for existing pending invitation for the same group and user
            if (!string.IsNullOrWhiteSpace(inviteeUserId))
            {
                var existingPendingInvitation = await _db.GroupInvitations
                    .AnyAsync(i => i.GroupId == groupId
                        && i.InviteeUserId == inviteeUserId
                        && i.Status == GroupInvitation.InvitationStatuses.Pending, ct);

                if (existingPendingInvitation)
                    throw new InvalidOperationException("A pending invitation already exists for this user in the specified group");
            }

            // Step 3: Create the invitation with cryptographically secure token
            var tokenBytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(tokenBytes);
            }
            var token = Convert.ToBase64String(tokenBytes)
                .Replace("/", "_")
                .Replace("+", "-")
                .TrimEnd('=');

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

            // Step 4: Add audit log entry
            await AddAuditAsync(inviterUserId, "InviteCreate", $"Invited {inviteeUserId ?? inviteeEmail} to group {groupId}", ct);

            // Step 5: Save changes and commit transaction
            // Wrap in try-catch to handle unique constraint violation from concurrent requests
            try
            {
                await _db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                throw new InvalidOperationException("A pending invitation already exists for this user in the specified group");
            }

            return inv;
        }
        catch (Exception)
        {
            // Transaction will be automatically rolled back when disposed if not committed
            // Re-throw to preserve the original exception for proper error handling upstream
            throw;
        }
    }

    /// <summary>
    /// Accepts a pending invitation and creates or revives group membership.
    /// Uses a database transaction with RepeatableRead isolation to prevent race conditions
    /// where the invitation could be revoked or the group deleted between validation and commit.
    /// Only the designated invitee can accept the invitation.
    /// </summary>
    /// <param name="token">The unique invitation token.</param>
    /// <param name="acceptorUserId">The user accepting the invitation (must match InviteeUserId if set).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created or updated GroupMember entity.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when invitation is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when invitation is not pending, expired, or group no longer exists.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the user is not the designated invitee.</exception>
    public async Task<GroupMember> AcceptAsync(string token, string acceptorUserId, CancellationToken ct = default)
    {
        // Use RepeatableRead isolation to prevent race conditions:
        // - Ensures the invitation status check and update are atomic
        // - Prevents another thread from revoking the invitation mid-operation
        // - Ensures group existence check remains valid through the transaction
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

        try
        {
            // Step 1: Fetch and validate invitation within transaction (row-level lock acquired)
            var inv = await _db.GroupInvitations.FirstOrDefaultAsync(i => i.Token == token, ct)
                ?? throw new KeyNotFoundException("Invitation not found");

            // Validate that the user is the designated invitee (must check before setting InviteeUserId)
            if (!string.IsNullOrWhiteSpace(inv.InviteeUserId) && inv.InviteeUserId != acceptorUserId)
                throw new UnauthorizedAccessException("Only the designated invitee can accept this invitation");

            if (inv.Status != GroupInvitation.InvitationStatuses.Pending)
            {
                throw new InvalidOperationException("Invitation is not pending");
            }

            if (inv.ExpiresAt.HasValue && inv.ExpiresAt.Value < DateTime.UtcNow)
            {
                throw new InvalidOperationException("Invitation expired");
            }

            // Step 2: Verify the group still exists before creating membership
            var groupExists = await _db.Groups.AnyAsync(g => g.Id == inv.GroupId, ct);
            if (!groupExists)
            {
                throw new InvalidOperationException("The group associated with this invitation no longer exists");
            }

            // Step 3: Update invitation status
            inv.Status = GroupInvitation.InvitationStatuses.Accepted;
            inv.RespondedAt = DateTime.UtcNow;
            inv.InviteeUserId ??= acceptorUserId;

            // Step 4: Ensure membership exists and is Active (revive if previously Left/Removed)
            var existing = await _db.GroupMembers.FirstOrDefaultAsync(
                m => m.GroupId == inv.GroupId && m.UserId == acceptorUserId, ct);

            GroupMember member;
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
            else
            {
                member = existing;
                if (!string.Equals(existing.Status, GroupMember.MembershipStatuses.Active, StringComparison.Ordinal))
                {
                    existing.Status = GroupMember.MembershipStatuses.Active;
                    existing.JoinedAt = existing.JoinedAt == default ? DateTime.UtcNow : existing.JoinedAt;
                    existing.LeftAt = null;
                    if (string.IsNullOrWhiteSpace(existing.Role))
                    {
                        existing.Role = GroupMember.Roles.Member;
                    }
                }
            }

            // Step 5: Add audit log entry
            await AddAuditAsync(acceptorUserId, "InviteAccept", $"Accepted invite for group {inv.GroupId}", ct);

            // Step 6: Save changes and commit transaction
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return member;
        }
        catch (Exception)
        {
            // Transaction will be automatically rolled back when disposed if not committed
            // Re-throw to preserve the original exception for proper error handling upstream
            throw;
        }
    }

    /// <summary>
    /// Declines a pending invitation. Uses a transaction to prevent race conditions
    /// where the invitation could be accepted or revoked by another operation.
    /// Only the designated invitee can decline the invitation.
    /// </summary>
    /// <param name="token">The unique invitation token.</param>
    /// <param name="userId">The user declining the invitation (must match InviteeUserId if set).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="KeyNotFoundException">Thrown when invitation is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when invitation is not pending.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the user is not the designated invitee.</exception>
    public async Task DeclineAsync(string token, string userId, CancellationToken ct = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

        try
        {
            var inv = await _db.GroupInvitations.FirstOrDefaultAsync(i => i.Token == token, ct)
                ?? throw new KeyNotFoundException("Invitation not found");

            // Validate that the user is the designated invitee
            if (!string.IsNullOrWhiteSpace(inv.InviteeUserId) && inv.InviteeUserId != userId)
                throw new UnauthorizedAccessException("Only the designated invitee can decline this invitation");

            if (inv.Status != GroupInvitation.InvitationStatuses.Pending)
                throw new InvalidOperationException("Invitation is not pending");

            inv.Status = GroupInvitation.InvitationStatuses.Declined;
            inv.RespondedAt = DateTime.UtcNow;
            await AddAuditAsync(userId, "InviteDecline", $"Declined invite for group {inv.GroupId}", ct);
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception)
        {
            // Transaction will be automatically rolled back when disposed if not committed
            throw;
        }
    }

    /// <summary>
    /// Revokes a pending invitation. Requires Owner or Manager permissions.
    /// Uses a transaction to prevent race conditions where the invitation
    /// could be accepted or declined by another operation.
    /// </summary>
    /// <param name="invitationId">The invitation ID to revoke.</param>
    /// <param name="actorUserId">The user revoking the invitation (must be Owner or Manager).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="KeyNotFoundException">Thrown when invitation is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when invitation is not pending.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the actor lacks required permissions.</exception>
    public async Task RevokeAsync(Guid invitationId, string actorUserId, CancellationToken ct = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

        try
        {
            var inv = await _db.GroupInvitations.FirstOrDefaultAsync(i => i.Id == invitationId, ct)
                ?? throw new KeyNotFoundException("Invitation not found");

            await EnsureOwnerOrManagerAsync(inv.GroupId, actorUserId, ct);

            if (inv.Status != GroupInvitation.InvitationStatuses.Pending)
                throw new InvalidOperationException("Invitation is not pending");

            inv.Status = GroupInvitation.InvitationStatuses.Revoked;
            inv.RespondedAt = DateTime.UtcNow;
            await AddAuditAsync(actorUserId, "InviteRevoke", $"Revoked invite {inv.Id}", ct);
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception)
        {
            // Transaction will be automatically rolled back when disposed if not committed
            throw;
        }
    }

    /// <summary>
    /// Validates that the actor has Owner or Manager permissions for the specified group.
    /// Checks both the GroupMembers table (for delegated roles) and Group.OwnerUserId (for actual ownership).
    /// First verifies that the group exists before checking permissions.
    /// </summary>
    /// <param name="groupId">The group to check permissions for.</param>
    /// <param name="actorUserId">The user attempting the action.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="KeyNotFoundException">Thrown when the group does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the user lacks required permissions.</exception>
    private async Task EnsureOwnerOrManagerAsync(Guid groupId, string actorUserId, CancellationToken ct)
    {
        // First check if the group exists
        var group = await _db.Groups.AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group == null) throw new KeyNotFoundException("Group not found");

        // Check if user is the actual group owner via Group.OwnerUserId
        var isActualOwner = group.OwnerUserId == actorUserId;

        // Check membership table for delegated Owner/Manager roles
        var membership = await _db.GroupMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == actorUserId && m.Status == GroupMember.MembershipStatuses.Active, ct);
        var hasOwnerRole = membership?.Role == GroupMember.Roles.Owner;
        var hasManagerRole = membership?.Role == GroupMember.Roles.Manager;

        // Allow if user is actual owner OR has Owner/Manager role in membership
        if (!(isActualOwner || hasOwnerRole || hasManagerRole)) throw new UnauthorizedAccessException("Manager or Owner permissions required");
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

    /// <summary>
    /// Determines if a DbUpdateException was caused by a unique constraint violation in PostgreSQL.
    /// </summary>
    /// <param name="ex">The DbUpdateException to check.</param>
    /// <returns>True if the exception represents a unique constraint violation (PostgreSQL error code 23505).</returns>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        var innerException = ex.InnerException;
        while (innerException != null)
        {
            if (innerException is Npgsql.PostgresException pgEx && pgEx.SqlState == "23505")
                return true;
            innerException = innerException.InnerException;
        }
        return false;
    }
}
