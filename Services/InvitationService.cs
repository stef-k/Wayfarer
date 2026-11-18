using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Services;

/// <summary>Issues and responds to registered-account invitations inside the Group write boundary.</summary>
public partial class InvitationService(ApplicationDbContext db) : IInvitationService
{
    /// <summary>Checks management, target availability, membership, then the live pending slot.</summary>
    internal async Task<GroupInvitation> IssueAsync(Guid groupId, string actor, string invitee, DateTime? expiresAt,
        CancellationToken ct, Func<CancellationToken, Task<bool>>? bearer = null)
    {
        await using var transaction = await GroupWrite.BeginAsync(db, groupId, actor, ct, bearer, invitee,
            () => db.GroupInvitations.Where(i => i.GroupId == groupId && i.InviteeUserId == invitee
                && i.Status == GroupInvitation.InvitationStatuses.Pending).Select(i => i.InviterUserId).ToArrayAsync(ct));
        await GroupRead.ScopeAsync(db, groupId, actor, manage: true, ct);
        if (!await db.Users.AnyAsync(u => u.Id == invitee && u.IsActive, ct))
            throw new GroupRefusal(404, "invitee_unavailable");
        if (await db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == invitee
            && m.Status == GroupMember.MembershipStatuses.Active, ct))
            throw new GroupRefusal(409, "already_member");

        var now = GroupMembershipEpisode.AtStoredPrecision(DateTime.UtcNow);
        var prior = await GroupWrite.FreshAsync(db, db.GroupInvitations.Where(i => i.GroupId == groupId
            && i.InviteeUserId == invitee && i.Status == GroupInvitation.InvitationStatuses.Pending), ct);
        if (prior is not null)
        {
            if (GroupInvitationQueries.Status(prior, now) == GroupInvitation.InvitationStatuses.Pending)
                throw new GroupRefusal(409, "pending_exists");
            prior.Status = GroupInvitation.InvitationStatuses.Expired;
            // Free the filtered unique slot before inserting, inside this same transaction.
            await db.SaveChangesAsync(ct);
        }

        var invitation = new GroupInvitation
        {
            Id = Guid.NewGuid(), GroupId = groupId, InviterUserId = actor, InviteeUserId = invitee,
            Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace("/", "_").Replace("+", "-").TrimEnd('='),
            ExpiresAt = expiresAt, CreatedAt = now
        };
        db.GroupInvitations.Add(invitation);
        AddAudit(actor, "InviteCreate", $"Invited {invitee} to group {groupId}");
        try
        {
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (GroupWrite.IsUnique(ex, "IX_GroupInvitation_GroupId_InviteeUserId_Pending"))
        {
            throw new GroupRefusal(409, "pending_exists");
        }
        return invitation;
    }

    /// <summary>Accepts the addressed invitation, comparing the qualified disclosure for bearer confirmation.</summary>
    internal async Task<GroupMember> AdmitAsync(Guid id, string actor, GroupAdmissionDisclosure? expected,
        CancellationToken ct, Func<CancellationToken, Task<bool>>? bearer = null)
    {
        var routing = await RouteAsync(id, ct);
        await using var transaction = await GroupWrite.BeginAsync(db, routing.GroupId, actor, ct, bearer, routing.InviterUserId);
        var (invitation, group) = await RecipientScopeAsync(id, actor, ct);
        var now = GroupMembershipEpisode.AtStoredPrecision(DateTime.UtcNow);
        EnsureLive(invitation, now);
        if (expected is not null && (expected.InvitationId != id || expected.GroupId != group.Id))
            throw new GroupRefusal(400, "review_identity_mismatch");

        var member = await GroupWrite.FreshAsync(db,
            db.GroupMembers.Where(m => m.GroupId == group.Id && m.UserId == actor), ct);
        if (!GroupMembershipEpisode.CanAdmit(member, invitation.CreatedAt, now))
            throw new GroupRefusal(409, member?.Status == GroupMember.MembershipStatuses.Active && expected is null
                ? "already_member" : "admission_ineligible");

        if (expected is not null)
        {
            var current = GroupSharingPolicy.Disclosure(group, id, member)
                ?? throw new GroupRefusal(409, "disclosure_unavailable");
            if (expected != current) throw new GroupRefusal(409, "disclosure_changed");
        }

        if (member is null)
        {
            member = new GroupMember { Id = Guid.NewGuid(), GroupId = group.Id, UserId = actor };
            db.GroupMembers.Add(member);
        }
        member.Status = GroupMember.MembershipStatuses.Active;
        member.Role = GroupMember.Roles.Member;
        member.JoinedAt = now;
        member.LeftAt = null;
        invitation.Status = GroupInvitation.InvitationStatuses.Accepted;
        invitation.RespondedAt = now;
        AddAudit(actor, "InviteAccept", $"Accepted invite for group {group.Id}");
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return member;
    }

    /// <summary>Declines the exact addressed live invitation without requiring a sharing disclosure.</summary>
    internal async Task<Guid> DeclineAsync(Guid id, string actor, CancellationToken ct,
        Func<CancellationToken, Task<bool>>? bearer = null)
    {
        var routing = await RouteAsync(id, ct);
        await using var transaction = await GroupWrite.BeginAsync(db, routing.GroupId, actor, ct, bearer, routing.InviterUserId);
        var (invitation, _) = await RecipientScopeAsync(id, actor, ct);
        var now = DateTime.UtcNow;
        EnsureLive(invitation, now);
        invitation.Status = GroupInvitation.InvitationStatuses.Declined;
        invitation.RespondedAt = now;
        AddAudit(actor, "InviteDecline", $"Declined invite for group {invitation.GroupId}");
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return invitation.GroupId;
    }

    /// <summary>Checks the exact Group scope before invitation lifecycle, retaining Web legacy cleanup.</summary>
    internal async Task<InvitationRevocation> RevokeAsync(Guid groupId, Guid id, string actor, bool legacyCleanup,
        CancellationToken ct, Func<CancellationToken, Task<bool>>? bearer = null)
    {
        await using var transaction = await GroupWrite.BeginAsync(db, groupId, actor, ct, bearer,
            participants: () => RevocationParticipantsAsync(groupId, id, ct));
        await GroupRead.ScopeAsync(db, groupId, actor, manage: true, ct);
        var invitation = await GroupWrite.FreshAsync(db,
            db.GroupInvitations.Where(i => i.Id == id && i.GroupId == groupId), ct)
            ?? throw new GroupRefusal(404, "invitation_unavailable");

        var now = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(invitation.InviteeUserId))
        {
            if (!legacyCleanup) throw new GroupRefusal(404, "invitation_unavailable");
            if (invitation.Status != GroupInvitation.InvitationStatuses.Pending)
                throw new GroupRefusal(409, "invitation_not_pending");
        }
        else
        {
            EnsureLive(invitation, now);
        }
        invitation.Status = GroupInvitation.InvitationStatuses.Revoked;
        invitation.RespondedAt = now;
        AddAudit(actor, "InviteRevoke", $"Revoked invite {id}");
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return new(invitation.GroupId, invitation.InviteeUserId);
    }

    /// <summary>Protects both accounts whose deletion could update or remove the invitation.</summary>
    private Task<string[]> RevocationParticipantsAsync(Guid groupId, Guid id, CancellationToken ct)
    {
        var scoped = db.GroupInvitations.Where(i => i.Id == id && i.GroupId == groupId);
        return scoped.Select(i => i.InviterUserId)
            .Union(scoped.Where(i => i.InviteeUserId != null).Select(i => i.InviteeUserId!)).ToArrayAsync(ct);
    }

    /// <summary>Resolves immutable routing only. Transactions reread authority and invitation state.</summary>
    private async Task<GroupInvitation> RouteAsync(Guid id, CancellationToken ct) =>
        await db.GroupInvitations.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct)
        ?? throw new GroupRefusal(404, "invitation_unavailable");

    /// <summary>Requires the addressed account and an available Group before exposing invitation state.</summary>
    private async Task<(GroupInvitation Invitation, Group Group)> RecipientScopeAsync(Guid id, string actor,
        CancellationToken ct)
    {
        var invitation = await GroupWrite.FreshAsync(db, db.GroupInvitations.Where(i => i.Id == id), ct)
            ?? throw new GroupRefusal(404, "invitation_unavailable");
        if (invitation.InviteeUserId != actor) throw new GroupRefusal(404, "invitation_unaddressed");
        var group = await db.Groups.AsNoTracking()
            .SingleOrDefaultAsync(g => g.Id == invitation.GroupId && !g.IsArchived, ct)
            ?? throw new GroupRefusal(404, "invitation_group_unavailable");
        return (invitation, group);
    }

    /// <summary>Uses the same elapsed-Pending projection for lifecycle refusals and readback.</summary>
    private static void EnsureLive(GroupInvitation invitation, DateTime now)
    {
        if (GroupInvitationQueries.Refusal(invitation, now) is { } code) throw new GroupRefusal(409, code);
    }

    /// <summary>Writes the audit with the durable invitation transition.</summary>
    private void AddAudit(string actor, string action, string details) =>
        db.AuditLogs.Add(new AuditLog { UserId = actor, Action = action, Details = details, Timestamp = DateTime.UtcNow });
}
