using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Confirmation purposes, expiry, canonical identity and ownership fail closed without mutation.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class RegionDeletionConfirmationPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Tokens are mandatory, content-bound, choice-bound, expiring and disjoint from legacy DELETE.</summary>
    [PostgresFact]
    public async Task ConfirmationAndLegacyPurposes_AreIndependent()
    {
        var g = await RegionDeletionTestGraph.SeedAsync(fixture);
        var time = new Clock();
        await using var db = fixture.CreateContext();
        var confirmation = new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider(), time);
        var service = new PlaceRegionLifecycleService(db, confirmation);
        var preview = await service.PreviewRegionDeletionAsync(g.TripId, g.SourceId, g.UserId, new("keep", "keep"), default);
        var mismatch = await Assert.ThrowsAsync<RegionDeletionRejectedException>(() => service.ConfirmRegionDeletionAsync(g.TripId, g.SourceId, g.UserId, new("delete", "keep", preview.Confirmation.Token), default));
        Assert.Equal("region-deletion-preview-stale", mismatch.Code);
        var legacy = await service.DeleteRegionAsync(g.TripId, g.SourceId, g.UserId, preview.Confirmation.Token, default);
        Assert.False(legacy.Succeeded); Assert.Equal("lifecycle-confirmation-stale", legacy.Warning!.Code);
        Assert.Equal(400, (await Assert.ThrowsAsync<RegionDeletionRejectedException>(() => service.ConfirmRegionDeletionAsync(g.TripId, g.SourceId, g.UserId, new("delete", "delete", legacy.Warning.ConfirmationToken), default))).Status);
        time.Now = time.Now.AddMinutes(11);
        Assert.Equal("region-deletion-preview-stale", (await Assert.ThrowsAsync<RegionDeletionRejectedException>(() => service.ConfirmRegionDeletionAsync(g.TripId, g.SourceId, g.UserId, new("keep", "keep", preview.Confirmation.Token), default))).Code);
        Assert.True(await db.Regions.AnyAsync(r => r.Id == g.SourceId));
    }

    /// <summary>Near names/order-zero are not canonical; exact historical nonzero rows retain their identity.</summary>
    [PostgresFact]
    public async Task CanonicalIdentityAndMalformedOwnership()
    {
        var g = await RegionDeletionTestGraph.SeedAsync(fixture, false);
        await using var db = fixture.CreateContext();
        var near = new Region { Id = Guid.NewGuid(), TripId = g.TripId, UserId = g.UserId, Name = "unassigned places", DisplayOrder = 0 };
        db.Regions.Add(near); await db.SaveChangesAsync();
        var service = new PlaceRegionLifecycleService(db, RegionDeletionTestGraph.Confirmation());
        var preview = await service.PreviewRegionDeletionAsync(g.TripId, g.SourceId, g.UserId, new("keep", "keep"), default);
        Assert.Equal("create", preview.Destination.Mode); Assert.NotEqual(near.Id, preview.Destination.RegionId);
        var wrong = await fixture.CreateUserAsync();
        var malformed = new Region { Id = Guid.NewGuid(), TripId = g.TripId, UserId = wrong.Id, Name = "Unassigned Places", DisplayOrder = 11 };
        db.Regions.Add(malformed); await db.SaveChangesAsync();
        Assert.Equal("unassigned_region_conflict", (await Assert.ThrowsAsync<RegionDeletionRejectedException>(() => service.PreviewRegionDeletionAsync(g.TripId, g.SourceId, g.UserId, new("keep", "keep"), default))).Code);
        await db.Regions.Where(r => r.Id == malformed.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.UserId, g.UserId));
        var valid = await service.PreviewRegionDeletionAsync(g.TripId, g.SourceId, g.UserId, new("keep", "keep"), default);
        Assert.Equal("existing", valid.Destination.Mode); Assert.Equal(malformed.Id, valid.Destination.RegionId);
        Assert.Equal(403, (await Assert.ThrowsAsync<RegionDeletionRejectedException>(() => service.PreviewRegionDeletionAsync(g.TripId, malformed.Id, g.UserId, new("keep", "keep"), default))).Status);
        await db.Places.Where(p => p.Id == g.PlaceIds[0]).ExecuteUpdateAsync(s => s.SetProperty(p => p.UserId, wrong.Id));
        Assert.Equal("unassigned_region_conflict", (await Assert.ThrowsAsync<RegionDeletionRejectedException>(() => service.PreviewRegionDeletionAsync(g.TripId, g.SourceId, g.UserId, new("keep", "keep"), default))).Code);
    }

    /// <summary>A failed rollback of a stale, unapplied attempt retains both failures and prevents later saves.</summary>
    [PostgresFact]
    public async Task UnappliedRollbackFailure_RetainsPrimaryFailureAndRetiresContext()
    {
        var g = await RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext(new FailedRollback());
        var service = new PlaceRegionLifecycleService(db, RegionDeletionTestGraph.Confirmation());
        var preview = await service.PreviewRegionDeletionAsync(g.TripId, g.SourceId, g.UserId, new("keep", "keep"), default);
        await using (var writer = fixture.CreateContext())
            await writer.Places.Where(p => p.Id == g.PlaceIds[0]).ExecuteUpdateAsync(s => s.SetProperty(p => p.Notes, "Concurrent edit"));
        var failures = await Assert.ThrowsAsync<AggregateException>(() => service.ConfirmRegionDeletionAsync(
            g.TripId, g.SourceId, g.UserId, new("keep", "keep", preview.Confirmation.Token), default));
        Assert.Contains(failures.InnerExceptions, e => e is RegionDeletionRejectedException { Code: "region-deletion-preview-stale" });
        Assert.Contains(failures.InnerExceptions, e => e is IOException);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => db.SaveChangesAsync());
        await using var verify = fixture.CreateContext();
        Assert.True(await verify.Regions.AnyAsync(r => r.Id == g.SourceId));
        Assert.Equal(2, await verify.Areas.CountAsync(a => a.RegionId == g.SourceId));
    }

    private sealed class FailedRollback : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction,
            TransactionEventData data, InterceptionResult result, CancellationToken token = default) =>
            throw new IOException("Injected rollback failure.");
    }

    /// <summary>Approval expiring during lock acquisition must be rechecked before any mutation.</summary>
    [PostgresFact]
    public async Task ExpiryDuringLockAcquisition_IsStale()
    {
        var g = await RegionDeletionTestGraph.SeedAsync(fixture);
        var clock = new Clock();
        await using var db = fixture.CreateContext(new ExpireAtLock(clock));
        var service = new PlaceRegionLifecycleService(db, new(new EphemeralDataProtectionProvider(), clock));
        var preview = await service.PreviewRegionDeletionAsync(g.TripId, g.SourceId, g.UserId, new("keep", "keep"), default);
        Assert.Equal("region-deletion-preview-stale", (await Assert.ThrowsAsync<RegionDeletionRejectedException>(() =>
            service.ConfirmRegionDeletionAsync(g.TripId, g.SourceId, g.UserId, new("keep", "keep", preview.Confirmation.Token), default))).Code);
        Assert.True(await db.Regions.AnyAsync(r => r.Id == g.SourceId));
    }

    private sealed class ExpireAtLock(Clock clock) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken token = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal)) clock.Now = clock.Now.AddMinutes(11);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
