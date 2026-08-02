using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Executes issue 405 migration and profile reconciliation against guarded PostgreSQL/PostGIS.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class SegmentMeasurementPostgresTests(PostgresImportTestFixture fixture)
{
    private const string PreviousMigration = "20260802085255_AddSegmentWaypoints";

    /// <summary>Executes exact-base Up, backfill, constraint, Down, and re-Up without recalculating legacy values.</summary>
    [PostgresFact]
    public async Task Migration_ExactBaseBackfillsConstrainsDowngradesAndReapplies()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var tripId = Guid.NewGuid();
        var manualId = Guid.NewGuid();
        var automaticId = Guid.NewGuid();
        fixture.RegisterTrip(tripId);
        await using var context = fixture.CreateContext();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO public."Trips" ("Id", "UserId", "Name", "IsPublic", "ShareProgressEnabled", "UpdatedAt") VALUES ({tripId}, {user.Id}, {"Measurement migration fixture"}, FALSE, FALSE, CURRENT_TIMESTAMP)""");
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO public."Segments" ("Id", "UserId", "TripId", "Mode", "EstimatedDuration", "EstimatedDistanceKm", "DisplayOrder", "Notes") VALUES ({manualId}, {user.Id}, {tripId}, {"walk"}, {TimeSpan.FromSeconds(91)}, {12.345d}, {1}, {"manual"})""");
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO public."Segments" ("Id", "UserId", "TripId", "Mode", "EstimatedDuration", "EstimatedDistanceKm", "DisplayOrder", "Notes") VALUES ({automaticId}, {user.Id}, {tripId}, {"walk"}, {null as TimeSpan?}, {7.654d}, {2}, {"automatic"})""");

        await migrator.MigrateAsync();

        var values = await context.Segments.AsNoTracking().Where(item => item.TripId == tripId)
            .OrderBy(item => item.DisplayOrder).ToArrayAsync();
        Assert.Equal(EstimatedDurationSource.Manual, values[0].EstimatedDurationSource);
        Assert.Equal(TimeSpan.FromSeconds(91), values[0].EstimatedDuration);
        Assert.Equal(12.345d, values[0].EstimatedDistanceKm);
        Assert.Equal(EstimatedDurationSource.Automatic, values[1].EstimatedDurationSource);
        Assert.Null(values[1].EstimatedDuration);
        Assert.Equal(7.654d, values[1].EstimatedDistanceKm);
        await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE public."Segments" SET "EstimatedDurationSource" = 2 WHERE "Id" = {automaticId}"""));

        await migrator.MigrateAsync(PreviousMigration);
        Assert.False(await ColumnExistsAsync(context, "Segments", "EstimatedDurationSource"));
        await migrator.MigrateAsync();
        Assert.True(await ColumnExistsAsync(context, "Segments", "EstimatedDurationSource"));
        Assert.Equal(EstimatedDurationSource.Manual,
            (await context.Segments.AsNoTracking().SingleAsync(item => item.Id == manualId)).EstimatedDurationSource);
    }

    /// <summary>Executes positive speed change and clearing atomically while preserving Manual duration.</summary>
    [PostgresFact]
    public async Task ProfileSpeedChangeAndClear_ReconcilesAutomaticAndPreservesManual()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var tripId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        fixture.RegisterTrip(tripId);
        fixture.RegisterTransportProfile(profileId);
        await using (var seed = fixture.CreateContext())
        {
            var trip = new Trip { Id = tripId, UserId = user.Id, Name = "Profile measurement fixture" };
            var region = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = tripId, UserId = user.Id, Name = "region" };
            var from = new Place { Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = user.Id, Name = "from", Location = new Point(0, 0) { SRID = 4326 } };
            var to = new Place { Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = user.Id, Name = "to", Location = new Point(0.1, 0) { SRID = 4326 } };
            var profile = new TransportProfile { Id = profileId, Key = $"measure-{Guid.NewGuid():N}"[..30], Label = "Measure", Category = "Test", PlanningSpeedKmh = 5, IsActive = false };
            seed.AddRange(trip, region, from, to, profile,
                Segment(trip, profile, from, to, EstimatedDurationSource.Automatic, TimeSpan.FromHours(1)),
                Segment(trip, profile, from, to, EstimatedDurationSource.Manual, TimeSpan.FromSeconds(91)));
            await seed.SaveChangesAsync();
        }

        await using (var context = fixture.CreateContext())
        {
            var changed = await TransportProfileMeasurementReconciler.ReconcileAsync(
                context, profileId, 10, user.Id, CancellationToken.None);
            Assert.True(changed.Succeeded);
            Assert.Equal((2, 1, 1), (changed.TotalReferences, changed.AutomaticReferences, changed.ManualReferences));
        }
        await using (var context = fixture.CreateContext())
        {
            var cleared = await TransportProfileMeasurementReconciler.ReconcileAsync(
                context, profileId, null, user.Id, CancellationToken.None);
            Assert.True(cleared.Succeeded);
        }
        await using (var verify = fixture.CreateContext())
        {
            var segments = await verify.Segments.AsNoTracking().Where(item => item.TripId == tripId).ToArrayAsync();
            Assert.Null(segments.Single(item => item.EstimatedDurationSource == EstimatedDurationSource.Automatic).EstimatedDuration);
            Assert.Equal(TimeSpan.FromSeconds(91), segments.Single(item => item.EstimatedDurationSource == EstimatedDurationSource.Manual).EstimatedDuration);
        }
    }

    private static Segment Segment(Trip trip, TransportProfile profile, Place from, Place to, EstimatedDurationSource source, TimeSpan duration) => new()
    {
        Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = trip.UserId,
        FromPlace = from, FromPlaceId = from.Id, ToPlace = to, ToPlaceId = to.Id,
        Mode = profile.Key, TransportProfile = profile, TransportProfileId = profile.Id,
        EstimatedDurationSource = source, EstimatedDuration = duration
    };

    private static async Task<bool> ColumnExistsAsync(ApplicationDbContext context, string table, string column)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name=@table AND column_name=@column)";
        command.Parameters.Add(new NpgsqlParameter("table", table));
        command.Parameters.Add(new NpgsqlParameter("column", column));
        if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync();
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
