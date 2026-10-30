using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Services.LocationMutations;
using Xunit;
using Wayfarer.Tests.Infrastructure;

namespace Wayfarer.Tests.Controllers;

/// <summary>A12: bounded cleanup progress, busy-account deferral and retained evidence eligibility.</summary>
public sealed partial class LocationMutationPostgresTests
{
    /// <summary>Twenty real owner transactions stop the run; a persisted cursor advances beyond a busy owner on the next wake.</summary>
    [PostgresFact]
    public async Task CleanupDefersBusyOwnerAndAdvancesWithinTransactionBudget()
    {
        var owners = new List<string>();
        for (var index = 0; index < 21; index++) owners.Add((await fixture.CreateUserAsync()).Id);
        owners.Sort(StringComparer.Ordinal);
        var expiry = DateTime.UtcNow.AddDays(-32);
        await using (var db = fixture.CreateContext())
        {
            foreach (var owner in owners) db.LocationMutationOperations.Add(Receipt(owner, expiry));
            await db.SaveChangesAsync();
        }
        await using var busy = fixture.CreateContext();
        await using var fence = await LocationDeletionFence.BeginAsync(busy, owners[0], default);
        await using var cleanup = fixture.CreateContext();
        var first = await new LocationMutationCleanup(cleanup).RunAsync(default);
        Assert.Equal(19, first.Deleted); Assert.True(first.FollowUp); Assert.NotNull(first.Cursor);
        var next = await new LocationMutationCleanup(cleanup).RunAsync(default, first.Cursor);
        Assert.Equal(1, next.Deleted); Assert.Null(next.Cursor);
        Assert.True(await cleanup.LocationMutationOperations.AnyAsync(row => row.UserId == owners[0]));
        await fence!.RollbackAsync();
        var final = await new LocationMutationCleanup(cleanup).RunAsync(default);
        Assert.Equal(1, final.Deleted);
        Assert.False(await cleanup.LocationMutationOperations.AnyAsync(row => owners.Contains(row.UserId)));
    }

    /// <summary>Pages remain bounded at 200; a future retention threshold and ingestion markers survive cleanup.</summary>
    [PostgresFact]
    public async Task CleanupPagesDoNotRemoveFreshReceiptsOrDeletionMarkers()
    {
        var (owner, _, _) = await SeedAsync();
        var old = DateTime.UtcNow.AddDays(-32);
        await using var db = fixture.CreateContext();
        for (var index = 0; index < 201; index++) db.LocationMutationOperations.Add(Receipt(owner, old));
        var retained = Receipt(owner, old); retained.ReceiptRetainUntilUtc = DateTime.UtcNow.AddHours(1);
        db.LocationMutationOperations.Add(retained);
        db.Set<LocationDeletionMarker>().Add(new() { UserId = owner, IdempotencyKey = Guid.NewGuid() });
        await db.SaveChangesAsync();
        var result = await new LocationMutationCleanup(db).RunAsync(default);
        Assert.Equal(201, result.Deleted);
        Assert.Equal(retained.Id, await db.LocationMutationOperations.Where(row => row.UserId == owner).Select(row => row.Id).SingleAsync());
        Assert.Single(await db.Set<LocationDeletionMarker>().Where(row => row.UserId == owner).ToListAsync());
    }

    /// <summary>Compact old binding without a Location FK or request body.</summary>
    private static LocationMutationOperation Receipt(string owner, DateTime expiry) => new()
    {
        UserId = owner, BaseId = Guid.NewGuid(), ActionId = Guid.NewGuid(), LocationId = 421,
        Adapter = "owner", Kind = "delete", Normalization = "location-values-1", Fingerprint = "sha256:" + new string('a', 64),
        ExpiresAtUtc = expiry, AcceptedAtUtc = expiry.AddMinutes(-15), ExecuteBeforeUtc = expiry,
        ReceiptRetainUntilUtc = expiry.AddDays(30), ExecutorNonce = Guid.NewGuid(), State = "pending"
    };
}
