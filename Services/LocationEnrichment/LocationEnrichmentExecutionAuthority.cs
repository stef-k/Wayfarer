using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;

namespace Wayfarer.Services.LocationEnrichment;

/// <summary>Owns short, database-clocked acquisition, renewal, validation, and release transactions.</summary>
public sealed class LocationEnrichmentExecutionAuthority(IDbContextFactory<ApplicationDbContext> contexts)
{
    public static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ContactSafetyMargin = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(35);
    public static readonly TimeSpan MinimumContactLifetime = ProviderTimeout + ContactSafetyMargin;

    /// <summary>Acquires a current epoch without retaining its context, connection, or transaction.</summary>
    public async Task<LocationEnrichmentExecutionLease?> TryAcquireAsync(
        string userId, int epoch, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var now = await DatabaseUtcNowAsync(db, cancellationToken);
        var workflow = await LockAsync(db, userId, cancellationToken);
        var lease = workflow?.Epoch == epoch ? workflow.TryAcquireExecutionLease(now, LeaseDuration) : null;
        if (lease.HasValue)
        {
            await db.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
        }
        else if (transaction != null) await transaction.RollbackAsync(cancellationToken);
        return lease;
    }

    /// <summary>Renews one owner before a contact and returns the database-derived expiry.</summary>
    public async Task<LocationEnrichmentExecutionLease?> TryRenewForContactAsync(
        LocationEnrichmentExecutionLease owner, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var now = await DatabaseUtcNowAsync(db, cancellationToken);
        var workflow = await LockAsync(db, owner.UserId, cancellationToken);
        if (workflow?.Epoch != owner.Epoch
            || !workflow.TryRenewExecutionLease(owner.LeaseId, owner.FencingGeneration, now, LeaseDuration))
        {
            if (transaction != null) await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        await db.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        var renewed = new LocationEnrichmentExecutionLease(owner.UserId, owner.Epoch, owner.LeaseId,
            owner.FencingGeneration, workflow.ExecutionLeaseExpiresAtUtc!.Value);
        await using var verify = await contexts.CreateDbContextAsync(cancellationToken);
        var dispatchNow = await DatabaseUtcNowAsync(verify, cancellationToken);
        var persisted = await verify.LocationEnrichmentWorkflows.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == owner.UserId, cancellationToken);
        return persisted?.Epoch == owner.Epoch
            && persisted.HasExecutionLease(owner.LeaseId, owner.FencingGeneration, dispatchNow)
            && persisted.ExecutionLeaseExpiresAtUtc!.Value - dispatchNow >= MinimumContactLifetime
            ? renewed with { ExpiresAtUtc = persisted.ExecutionLeaseExpiresAtUtc.Value } : null;
    }

    /// <summary>Checks the complete fence against database UTC in a disposed short context.</summary>
    public async Task<bool> IsCurrentAsync(LocationEnrichmentExecutionLease owner,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var now = await DatabaseUtcNowAsync(db, cancellationToken);
        var workflow = await db.LocationEnrichmentWorkflows.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == owner.UserId, cancellationToken);
        return workflow?.Epoch == owner.Epoch
            && workflow.HasExecutionLease(owner.LeaseId, owner.FencingGeneration, now);
    }

    /// <summary>Clears only the caller's exact lease and never a replacement owner.</summary>
    public async Task<bool> TryReleaseAsync(LocationEnrichmentExecutionLease owner,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var workflow = await LockAsync(db, owner.UserId, cancellationToken);
        if (workflow?.Epoch != owner.Epoch
            || !workflow.TryReleaseExecutionLease(owner.LeaseId, owner.FencingGeneration))
        {
            if (transaction != null) await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await db.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return true;
    }

    internal static async Task<DateTime> DatabaseUtcNowAsync(
        ApplicationDbContext db, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql()) return DateTime.UtcNow;
        var value = await db.Database.SqlQuery<DateTime>(
            $"SELECT (clock_timestamp() AT TIME ZONE 'UTC') AS \"Value\"").SingleAsync(cancellationToken);
        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    private static async Task<LocationEnrichmentWorkflow?> LockAsync(
        ApplicationDbContext db, string userId, CancellationToken cancellationToken)
        => db.Database.IsNpgsql()
            ? await db.LocationEnrichmentWorkflows.FromSqlInterpolated($$"""
                SELECT *, xmin FROM "LocationEnrichmentWorkflows" WHERE "UserId" = {{userId}} FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken)
            : await db.LocationEnrichmentWorkflows.SingleOrDefaultAsync(
                item => item.UserId == userId, cancellationToken);
}

/// <summary>Creates isolated short-lived contexts for enrichment authority transactions.</summary>
public sealed class LocationEnrichmentDbContextFactory(
    DbContextOptions<ApplicationDbContext> options, IServiceProvider services)
    : IDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext() => new(options, services);
}
