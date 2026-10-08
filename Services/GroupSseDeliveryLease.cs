using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Services;

/// <summary>Fresh persisted group eligibility, with PostgreSQL locks held through one protected frame.</summary>
public sealed class GroupSseDeliveryLease(IServiceScopeFactory scopeFactory)
{
    /// <summary>Fresh membership-only eligibility for callers without a location frame.</summary>
    public async Task<IAsyncDisposable?> AcquireAsync(Guid groupId, string userId, CancellationToken token) =>
        (await AcquireCoreAsync(groupId, userId, null, token)).Lease;

    /// <summary>
    /// Checks the source of location/deletion metadata after any queued write. An invisible
    /// source suppresses the entire frame; revoked recipients lose the stream itself.
    /// </summary>
    public Task<(IAsyncDisposable? Lease, bool Allowed)> AcquireEventAsync(
        Guid groupId, string userId, string data, CancellationToken token) =>
        AcquireCoreAsync(groupId, userId, JsonSerializer.Deserialize<GroupSseEventDto>(data)
            ?? throw new JsonException("Missing group event"), token);

    /// <summary>
    /// Locks group first, then recipient and source in user-ID order with FOR SHARE NOWAIT.
    /// Locks conflict with policy, opt-out, membership and archive writes until the bounded
    /// frame flush completes. A prior lease may finish before revocation commits; queued
    /// or subsequent leases must see committed state. Heartbeats bypass this authority.
    /// </summary>
    private async Task<(IAsyncDisposable? Lease, bool Allowed)> AcquireCoreAsync(
        Guid groupId, string userId, GroupSseEventDto? payload, CancellationToken token)
    {
        var scope = scopeFactory.CreateAsyncScope();
        IDbContextTransaction? transaction = null;
        var transferred = false;
        Exception? failure = null;
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var isLocation = payload?.Type is "location" or "location-deleted";
            var sourceId = isLocation ? payload!.UserId : userId;
            Group? group;
            List<GroupMember> members;
            // Other providers are used only by ordinary tests; production is PostgreSQL.
            if (db.Database.IsNpgsql())
            {
                transaction = await db.Database.BeginTransactionAsync(token);
                group = (await db.Groups.FromSqlInterpolated($"""
                    SELECT * FROM "Groups" WHERE "Id" = {groupId} AND NOT "IsArchived" FOR SHARE NOWAIT
                    """).AsNoTracking().ToListAsync(token)).SingleOrDefault();
                if (group is null) return (null, false);
                members = await db.GroupMembers.FromSqlInterpolated($"""
                    SELECT * FROM "GroupMembers" WHERE "GroupId" = {groupId}
                    AND ("UserId" = {userId} OR "UserId" = {sourceId})
                    AND "Status" = {GroupMember.MembershipStatuses.Active}
                    ORDER BY "UserId" FOR SHARE NOWAIT
                    """).AsNoTracking().ToListAsync(token);
            }
            else
            {
                group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId && !g.IsArchived, token);
                members = await db.GroupMembers.AsNoTracking().Where(m => m.GroupId == groupId
                    && (m.UserId == userId || m.UserId == sourceId)
                    && m.Status == GroupMember.MembershipStatuses.Active).ToListAsync(token);
            }
            var recipient = members.FirstOrDefault(m => m.UserId == userId);
            if (group is null || recipient is null) return (null, false);
            var allowed = payload is null || (isLocation
                ? GroupLocationVisibility.CanSee(group, recipient, members.FirstOrDefault(m => m.UserId == sourceId))
                : payload.Type is "visibility-changed" or "member-joined" or "member-left" or "member-removed"
                    or "invite-created" or "invite-declined" or "invite-revoked");
            transferred = true;
            return (new Lease(scope, transaction), allowed);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            if (!transferred)
            {
                try { await new Lease(scope, transaction).DisposeAsync(); }
                catch when (failure is not null) { /* Preserve the acquisition failure. */ }
            }
        }
    }

    /// <summary>Read-only transaction disposal rolls back and releases row locks before the scope.</summary>
    private sealed class Lease(AsyncServiceScope scope, IDbContextTransaction? transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { if (transaction is not null) await transaction.DisposeAsync(); }
            finally { await scope.DisposeAsync(); }
        }
    }
}
