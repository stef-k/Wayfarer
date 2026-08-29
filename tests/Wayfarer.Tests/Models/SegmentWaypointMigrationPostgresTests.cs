using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Executes segment-waypoint migration history on a disposable PostgreSQL database.</summary>
[Collection(PostgresMigrationTestCollection.Name)]
public sealed class SegmentWaypointMigrationPostgresTests(PostgresMigrationTestFixture fixture)
{
    private const string PreviousMigration = "20260728152323_AdminManagedTransportProfiles";

    /// <summary>Executes exact-base upgrade and downgrade while preserving every legacy Segment value.</summary>
    [PostgresFact]
    public async Task MigrationUpAndDown_PreservesLegacySegments_AndOnlyAddsWaypointSchema()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var tripId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        await using var context = fixture.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO public."Trips" ("Id", "UserId", "Name", "IsPublic", "ShareProgressEnabled", "UpdatedAt") VALUES ({tripId}, {user.Id}, {"Legacy waypoint fixture"}, FALSE, FALSE, CURRENT_TIMESTAMP)""");
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO public."Segments" ("Id", "UserId", "TripId", "Mode", "EstimatedDuration", "EstimatedDistanceKm", "DisplayOrder", "Notes") VALUES ({segmentId}, {user.Id}, {tripId}, {"walk"}, {TimeSpan.FromMinutes(37)}, {12.345d}, {4}, {"legacy notes"})""");

        await migrator.MigrateAsync();

        var segment = await context.Segments.AsNoTracking().SingleAsync(item => item.Id == segmentId);
        Assert.Equal("walk", segment.Mode);
        Assert.Equal(TimeSpan.FromMinutes(37), segment.EstimatedDuration);
        Assert.Equal(12.345d, segment.EstimatedDistanceKm);
        Assert.Equal(4, segment.DisplayOrder);
        Assert.Equal("legacy notes", segment.Notes);
        Assert.Empty(await context.Set<SegmentWaypoint>().Where(item => item.SegmentId == segmentId).ToListAsync());

        await migrator.MigrateAsync(PreviousMigration);
        Assert.False(await TableExistsAsync(context, "SegmentWaypoints"));
        Assert.True(await TableExistsAsync(context, "Segments"));
        await migrator.MigrateAsync();
        Assert.True(await TableExistsAsync(context, "SegmentWaypoints"));
        await transaction.RollbackAsync();
    }


    private static async Task<bool> TableExistsAsync(ApplicationDbContext context, string table)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @table)";
        command.Parameters.Add(new NpgsqlParameter("table", table));
        return (bool)(await command.ExecuteScalarAsync())!;
    }

}
