using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Orders Group decisions with Identity writes and account cascades.</summary>
internal static class GroupWrite
{
    /// <summary>
    /// Lock the affected accounts before the Group, then read membership and invitation state.
    /// Identity deletion owns the account row before its cascades can touch the Group.
    /// </summary>
    internal static async Task<IDbContextTransaction?> BeginAsync(ApplicationDbContext db, Guid? groupId,
        string actor, CancellationToken ct, Func<CancellationToken, Task<bool>>? bearer = null,
        string? target = null, Func<Task<string[]>>? participants = null)
    {
        if (!db.Database.IsRelational()) return null;

        while (true)
        {
            var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            try
            {
                var accounts = await AccountsAsync();
                var locked = db.Database.IsNpgsql()
                    ? await db.Database.SqlQuery<string>($"""
                        SELECT "Id" AS "Value" FROM "AspNetUsers" WHERE "Id" = ANY ({accounts}) ORDER BY "Id" FOR SHARE
                        """).ToListAsync(ct)
                    : accounts.ToList();
                if (groupId is { } id) await GroupService.LockMembershipAsync(db, id, ct);

                // A Group writer may have committed a new owner or participant while we waited.
                var currentAccounts = await AccountsAsync();
                if (currentAccounts.All(accounts.Contains) && !await db.Users.AnyAsync(
                    u => currentAccounts.Contains(u.Id) && !locked.Contains(u.Id), ct))
                {
                    await RequireActorAsync(db, actor, ct, bearer);
                    return transaction;
                }
            }
            catch (Exception failure)
            {
                try { await transaction.DisposeAsync(); }
                catch (Exception cleanup) { failure.Data["GroupWriteCleanupFailure"] = cleanup.GetType().Name; }
                throw;
            }
            await transaction.DisposeAsync();
        }

        async Task<string[]> AccountsAsync()
        {
            var owner = groupId.HasValue
                ? await db.Groups.Where(g => g.Id == groupId).Select(g => g.OwnerUserId).SingleOrDefaultAsync(ct)
                : null;
            var extra = participants is null ? [] : await participants();
            return extra.Concat(new[] { actor, target, owner }).OfType<string>()
                .Distinct(StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>Account activity and bearer authority are checked after locks, using current data.</summary>
    private static async Task RequireActorAsync(ApplicationDbContext db, string actor, CancellationToken ct,
        Func<CancellationToken, Task<bool>>? bearer)
    {
        if (!await db.Users.AnyAsync(u => u.Id == actor && u.IsActive, ct)
            || (bearer != null && db.Database.IsNpgsql() && !await bearer(ct)))
            throw new GroupRefusal(401, "invalid_bearer");
    }

    /// <summary>Loads a write row again even if an MVC action already tracked it before a lock wait.</summary>
    internal static async Task<T?> FreshAsync<T>(ApplicationDbContext db, IQueryable<T> query, CancellationToken ct)
        where T : class
    {
        var row = await query.SingleOrDefaultAsync(ct);
        if (row is not null) await db.Entry(row).ReloadAsync(ct);
        return row;
    }

    /// <summary>Only the named uniqueness constraint maps to the associated domain conflict.</summary>
    internal static bool IsUnique(DbUpdateException exception, string constraint) =>
        exception.InnerException is Npgsql.PostgresException { SqlState: "23505" } error
        && error.ConstraintName == constraint;
}

/// <summary>An expected Group contract refusal safe to expose as a bounded public code.</summary>
internal sealed class GroupRefusal(int status, string code) : Exception(code)
{
    internal int Status { get; } = status;
    internal string Code { get; } = code;
}
