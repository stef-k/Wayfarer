using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Executes transport-profile migration history on a disposable PostgreSQL database.</summary>
[Collection(PostgresMigrationTestCollection.Name)]
public sealed class TransportProfileMigrationPostgresTests(PostgresMigrationTestFixture fixture)
{
    private const string PreviousMigration = "20260726085113_AddTileTrafficMode";

    /// <summary>Executes downgrade and upgrade transactionally over representative legacy values.</summary>
    [PostgresFact]
    public async Task MigrationUp_ReconcilesLegacyModesWithoutChangingTheirText_AndRollsBackCleanly()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Legacy migration fixture" };
        await using var context = fixture.CreateContext();
        context.Trips.Add(trip);
        await context.SaveChangesAsync();
        var modes = new[]
        {
            new string('x', 112),
            new string('y', 140),
            "  MiXeD Καράβι / rail?!  ",
            $"{new string('界', 81)}!?"
        };
        var ids = modes.Select(_ => Guid.NewGuid()).ToArray();

        await using var transaction = await context.Database.BeginTransactionAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        for (var index = 0; index < modes.Length; index++)
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""INSERT INTO public."Segments" ("Id", "UserId", "TripId", "Mode", "DisplayOrder") VALUES ({ids[index]}, {user.Id}, {trip.Id}, {modes[index]}, {index})""");
        }

        await migrator.MigrateAsync();

        var reconciled = await context.Segments.AsNoTracking().Where(segment => ids.Contains(segment.Id)).OrderBy(segment => segment.DisplayOrder).ToListAsync();
        Assert.Equal(modes, reconciled.Select(segment => segment.Mode));
        var profileIds = reconciled.Select(segment => segment.TransportProfileId!.Value).ToArray();
        var profiles = await context.Set<TransportProfile>().AsNoTracking().Where(profile => profileIds.Contains(profile.Id)).ToListAsync();
        Assert.All(profiles, profile => Assert.InRange(profile.Label.Length, 1, 120));
        Assert.Contains(profiles, profile => profile.Label.Length == 120);

        await migrator.MigrateAsync(PreviousMigration);
        Assert.False(await ColumnExistsAsync(context, "Segments", "TransportProfileId"));
        await transaction.RollbackAsync();
    }


    private static async Task<bool> ColumnExistsAsync(ApplicationDbContext context, string table, string column)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @table AND column_name = @column)";
        command.Parameters.Add(new NpgsqlParameter("table", table));
        command.Parameters.Add(new NpgsqlParameter("column", column));
        return (bool)(await command.ExecuteScalarAsync())!;
    }

}
