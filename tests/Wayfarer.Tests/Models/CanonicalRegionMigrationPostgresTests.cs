using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Configuration;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Qualifies historical conflict refusal and durable canonical identity on a disposable database.</summary>
public sealed class CanonicalRegionMigrationPostgresTests(PostgresMigrationTestFixture fixture)
    : IClassFixture<PostgresMigrationTestFixture>
{
    /// <summary>A failed migration changes neither historical rows nor migration history; repair remains a human decision.</summary>
    [PostgresFact]
    public async Task HistoricalDuplicatesAbortUnchangedThenValidIndexEnforcesIdentity()
    {
        var owner = await fixture.CreateUserAsync();
        var other = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        var current = migrations[^1];
        Assert.EndsWith("_CanonicalUnassignedRegion", current);
        await migrator.MigrateAsync(migrations[^2]);
        var trip = new Trip { Id = Guid.NewGuid(), UserId = owner.Id, Name = "Historical duplicates" };
        var first = new Region
        {
            Id = Guid.NewGuid(), Trip = trip, UserId = owner.Id,
            Name = "Unassigned Places", DisplayOrder = 17, Notes = "Retain first"
        };
        var second = new Region
        {
            Id = Guid.NewGuid(), Trip = trip, UserId = other.Id,
            Name = "Unassigned Places", DisplayOrder = 0, Notes = "Retain second"
        };
        db.Regions.AddRange(first, second);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await db.Regions.AsNoTracking().Where(region => region.TripId == trip.Id)
            .OrderBy(region => region.Id).Select(region => new { region.Id, region.Name, region.UserId, region.DisplayOrder, region.Notes }).ToArrayAsync();
        var timestamp = (await db.Trips.AsNoTracking().SingleAsync(item => item.Id == trip.Id)).UpdatedAt;

        var failure = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync());

        Assert.Equal(("23505", RegionConfiguration.UnassignedIndex), (failure.SqlState, failure.ConstraintName));
        Assert.Contains("duplicate exact Unassigned Places", failure.MessageText);
        Assert.Contains("maintainer-approved reconciliation", failure.Hint);
        Assert.DoesNotContain(current, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(before, await db.Regions.AsNoTracking().Where(region => region.TripId == trip.Id)
            .OrderBy(region => region.Id).Select(region => new { region.Id, region.Name, region.UserId, region.DisplayOrder, region.Notes }).ToArrayAsync());
        Assert.Equal(timestamp, (await db.Trips.AsNoTracking().SingleAsync(item => item.Id == trip.Id)).UpdatedAt);

        // Only this disposable synthetic fixture is reconciled to qualify the successful migration path.
        await db.Regions.Where(region => region.Id == second.Id).ExecuteDeleteAsync();
        await migrator.MigrateAsync();
        Assert.Contains(current, await db.Database.GetAppliedMigrationsAsync());
        var retained = await db.Regions.AsNoTracking().SingleAsync(region => region.Id == first.Id);
        Assert.Equal((17, "Retain first", owner.Id), (retained.DisplayOrder, retained.Notes, retained.UserId));
        await AssertConstraintAsync(db, trip, other.Id);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    /// <summary>Exact duplicates fail for any owner/order; ordinary names, near-names and separate Trips remain valid.</summary>
    private static async Task AssertConstraintAsync(ApplicationDbContext db, Trip trip, string otherOwner)
    {
        var duplicate = new Region
        {
            Id = Guid.NewGuid(), TripId = trip.Id, UserId = otherOwner,
            Name = "Unassigned Places", DisplayOrder = 88
        };
        db.Regions.Add(duplicate);
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(RegionConfiguration.UnassignedIndex, Assert.IsType<PostgresException>(failure.InnerException).ConstraintName);
        db.ChangeTracker.Clear();
        var ordinary = new Region
        {
            Id = Guid.NewGuid(), TripId = trip.Id, UserId = trip.UserId, Name = "Ordinary", DisplayOrder = 0
        };
        db.Regions.AddRange(ordinary,
            new Region { Id = Guid.NewGuid(), TripId = trip.Id, UserId = trip.UserId, Name = "Ordinary", DisplayOrder = 0 },
            new Region { Id = Guid.NewGuid(), TripId = trip.Id, UserId = trip.UserId, Name = "unassigned places", DisplayOrder = 0 },
            new Region { Id = Guid.NewGuid(), TripId = trip.Id, UserId = trip.UserId, Name = " Unassigned Places ", DisplayOrder = 0 },
            new Region
            {
                Id = Guid.NewGuid(), Trip = new Trip { Id = Guid.NewGuid(), UserId = trip.UserId, Name = "Other Trip" },
                UserId = trip.UserId, Name = "Unassigned Places", DisplayOrder = 0
            });
        await db.SaveChangesAsync();
        ordinary.Name = "Unassigned Places";
        var updateFailure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23505", Assert.IsType<PostgresException>(updateFailure.InnerException).SqlState);
        db.ChangeTracker.Clear();
        Assert.Equal(5, await db.Regions.CountAsync(region => region.TripId == trip.Id));
    }
}
