using Microsoft.EntityFrameworkCore;
using Point = NetTopologySuite.Geometries.Point;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Services.LocationEnrichment;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Proves PostgreSQL is the unique, optimistic workflow authority.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationEnrichmentWorkflowPostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresFact]
    public async Task OneWorkflowPerUserAndDifferentUsersRemainIndependent()
    {
        var first = await fixture.CreateUserAsync();
        var second = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        db.LocationEnrichmentWorkflows.Add(LocationEnrichmentWorkflow.Create(first.Id, DateTime.UtcNow));
        db.LocationEnrichmentWorkflows.Add(LocationEnrichmentWorkflow.Create(second.Id, DateTime.UtcNow));
        await db.SaveChangesAsync();

        await using var duplicate = fixture.CreateContext();
        duplicate.LocationEnrichmentWorkflows.Add(LocationEnrichmentWorkflow.Create(first.Id, DateTime.UtcNow));
        await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
    }

    [PostgresFact]
    public async Task StaleWorkflowUpdateIsRejected()
    {
        var user = await fixture.CreateUserAsync();
        await using (var setup = fixture.CreateContext())
        {
            setup.Add(LocationEnrichmentWorkflow.Create(user.Id, DateTime.UtcNow));
            await setup.SaveChangesAsync();
        }

        await using var first = fixture.CreateContext();
        await using var stale = fixture.CreateContext();
        var current = await first.LocationEnrichmentWorkflows.SingleAsync(item => item.UserId == user.Id);
        var outdated = await stale.LocationEnrichmentWorkflows.SingleAsync(item => item.UserId == user.Id);
        current.Start(DateTime.UtcNow);
        await first.SaveChangesAsync();
        outdated.Cancel(DateTime.UtcNow);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
    }

    [PostgresFact]
    public async Task DeletingWorkflowMetadataPreservesOwnedLocation()
    {
        var user = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var location = new Location
        {
            UserId = user.Id,
            Timestamp = DateTime.UtcNow,
            LocalTimestamp = DateTime.UtcNow,
            TimeZoneId = "UTC",
            Coordinates = new Point(20, 10) { SRID = 4326 }
        };
        db.Locations.Add(location);
        db.LocationEnrichmentWorkflows.Add(LocationEnrichmentWorkflow.Create(user.Id, DateTime.UtcNow));
        await db.SaveChangesAsync();
        db.LocationEnrichmentWorkflows.Remove(await db.LocationEnrichmentWorkflows.SingleAsync(item => item.UserId == user.Id));
        await db.SaveChangesAsync();

        Assert.True(await db.Locations.AnyAsync(item => item.Id == location.Id));
    }

    [PostgresFact]
    public async Task LeaseIsCommittedVisibleAndIndependentPerUser()
    {
        var first = await fixture.CreateUserAsync();
        var second = await fixture.CreateUserAsync();
        int firstEpoch;
        int secondEpoch;
        await using (var setup = fixture.CreateContext())
        {
            var firstWorkflow = LocationEnrichmentWorkflow.Create(first.Id, DateTime.UtcNow);
            firstWorkflow.Start(DateTime.UtcNow);
            var secondWorkflow = LocationEnrichmentWorkflow.Create(second.Id, DateTime.UtcNow);
            secondWorkflow.Start(DateTime.UtcNow);
            firstEpoch = firstWorkflow.Epoch; secondEpoch = secondWorkflow.Epoch;
            setup.AddRange(firstWorkflow, secondWorkflow);
            await setup.SaveChangesAsync();
        }
        var owner = new LocationEnrichmentExecutionAuthority(new FixtureFactory(fixture));

        var firstLease = await owner.TryAcquireAsync(first.Id, firstEpoch);
        var losingLease = await owner.TryAcquireAsync(first.Id, firstEpoch);
        var independentLease = await owner.TryAcquireAsync(second.Id, secondEpoch);

        Assert.NotNull(firstLease);
        Assert.Null(losingLease);
        Assert.NotNull(independentLease);
        await using var observer = fixture.CreateContext();
        var committed = await observer.LocationEnrichmentWorkflows.AsNoTracking()
            .SingleAsync(item => item.UserId == first.Id);
        Assert.Equal(firstLease.Value.LeaseId, committed.ExecutionLeaseId);
        Assert.True(committed.ExecutionLeaseExpiresAtUtc > DateTime.UtcNow.AddSeconds(15));
    }

    private sealed class FixtureFactory(PostgresImportTestFixture fixture)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateContext();
    }
}
