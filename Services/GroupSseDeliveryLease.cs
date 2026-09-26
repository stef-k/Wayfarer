using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Fresh persisted group eligibility, with PostgreSQL locks held through one protected frame.</summary>
public sealed class GroupSseDeliveryLease(IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// Locks group first, then active membership, using FOR SHARE NOWAIT. This conflicts with
    /// archive/status updates and deletion cascades. A prior lease may finish before revocation commits;
    /// after commit no new lease can authorize a former member. Heartbeats never call this authority.
    /// </summary>
    public async Task<IAsyncDisposable?> AcquireAsync(Guid groupId, string userId, CancellationToken token)
    {
        var scope = scopeFactory.CreateAsyncScope();
        IDbContextTransaction? transaction = null;
        var transferred = false;
        Exception? failure = null;
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // Other providers are used only by ordinary tests; production is PostgreSQL.
            if (db.Database.IsNpgsql())
            {
                transaction = await db.Database.BeginTransactionAsync(token);
                var groups = await db.Groups.FromSqlInterpolated($"""
                    SELECT * FROM "Groups" WHERE "Id" = {groupId} AND NOT "IsArchived" FOR SHARE NOWAIT
                    """).AsNoTracking().ToListAsync(token);
                if (groups.Count == 0) return null;
                var members = await db.GroupMembers.FromSqlInterpolated($"""
                    SELECT * FROM "GroupMembers" WHERE "GroupId" = {groupId} AND "UserId" = {userId}
                    AND "Status" = {GroupMember.MembershipStatuses.Active} FOR SHARE NOWAIT
                    """).AsNoTracking().ToListAsync(token);
                if (members.Count == 0) return null;
            }
            else if (!await db.Groups.AnyAsync(g => g.Id == groupId && !g.IsArchived, token)
                || !await db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == userId
                    && m.Status == GroupMember.MembershipStatuses.Active, token))
                return null;
            transferred = true;
            return new Lease(scope, transaction);
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
