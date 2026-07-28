using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Executes the transport-profile migration invariants on the opt-in isolated PostgreSQL database.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class TransportProfilePostgresTests
{
    private const string PreviousMigration = "20260726085113_AddTileTrafficMode";
    private readonly PostgresImportTestFixture _fixture;

    /// <summary>Initializes provider tests over the guarded shared fixture.</summary>
    public TransportProfilePostgresTests(PostgresImportTestFixture fixture) => _fixture = fixture;

    /// <summary>Proves Mode-only writers attach an inactive compatibility profile without rewriting public mode text.</summary>
    [PostgresFact]
    public async Task SegmentInsert_AttachesUnknownCompatibilityProfile_AndPreservesMode()
    {
        _fixture.RequireAvailable();
        var user = await _fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Transport profile fixture" };
        var mode = $"Legacy Mode {Guid.NewGuid():N}";
        var segment = new Segment { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id, Mode = mode };
        _fixture.RegisterTrip(trip.Id);

        await using (var context = _fixture.CreateContext())
        {
            context.Trips.Add(trip);
            context.Segments.Add(segment);
            await context.SaveChangesAsync();
        }

        await using var verification = _fixture.CreateContext();
        var stored = await verification.Segments.AsNoTracking().SingleAsync(item => item.Id == segment.Id);
        var profile = await verification.Set<TransportProfile>().AsNoTracking().SingleAsync(item => item.Id == stored.TransportProfileId);
        _fixture.RegisterTransportProfile(profile.Id);
        Assert.Equal(mode, stored.Mode);
        Assert.False(profile.IsActive);
        Assert.False(profile.IsSeeded);
        Assert.Null(profile.PlanningSpeedKmh);
        Assert.NotEqual(0u, profile.RowVersion);
    }

    /// <summary>Executes downgrade and upgrade transactionally over representative legacy values.</summary>
    [PostgresFact]
    public async Task MigrationUp_ReconcilesLegacyModesWithoutChangingTheirText_AndRollsBackCleanly()
    {
        _fixture.RequireAvailable();
        var user = await _fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Legacy migration fixture" };
        _fixture.RegisterTrip(trip.Id);
        await using var context = _fixture.CreateContext();
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

    /// <summary>Proves Mode remains authoritative across every trigger update shape.</summary>
    [PostgresFact]
    public async Task SegmentTrigger_EnforcesModeAuthorityForInsertAndUpdates()
    {
        _fixture.RequireAvailable();
        var user = await _fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trigger fixture" };
        var segment = new Segment { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id, Mode = "walk" };
        _fixture.RegisterTrip(trip.Id);
        await using var context = _fixture.CreateContext();
        context.AddRange(trip, segment);
        await context.SaveChangesAsync();
        var walkId = await context.Set<TransportProfile>().Where(profile => profile.Key == "walk").Select(profile => profile.Id).SingleAsync();
        var carId = await context.Set<TransportProfile>().Where(profile => profile.Key == "car").Select(profile => profile.Id).SingleAsync();

        await context.Database.ExecuteSqlInterpolatedAsync($"""UPDATE public."Segments" SET "TransportProfileId" = {carId} WHERE "Id" = {segment.Id}""");
        Assert.Equal(walkId, await ProfileIdAsync(context, segment.Id));

        var unknown = $"  Mixed Καράβι?! {new string('界', 90)} {Guid.NewGuid():N}  ";
        await context.Database.ExecuteSqlInterpolatedAsync($"""UPDATE public."Segments" SET "Mode" = {unknown} WHERE "Id" = {segment.Id}""");
        var unknownId = await ProfileIdAsync(context, segment.Id);
        Assert.NotNull(unknownId);
        Assert.Equal(unknown, await context.Segments.Where(item => item.Id == segment.Id).Select(item => item.Mode).SingleAsync());
        Assert.InRange(await context.Set<TransportProfile>().Where(profile => profile.Id == unknownId).Select(profile => profile.Label.Length).SingleAsync(), 1, 120);

        await context.Database.ExecuteSqlInterpolatedAsync($"""UPDATE public."Segments" SET "Mode" = {'c' + "ar"}, "TransportProfileId" = {unknownId} WHERE "Id" = {segment.Id}""");
        Assert.Equal(carId, await ProfileIdAsync(context, segment.Id));
        await context.Database.ExecuteSqlInterpolatedAsync($"""UPDATE public."Segments" SET "Mode" = {string.Empty}, "TransportProfileId" = {carId} WHERE "Id" = {segment.Id}""");
        Assert.Null(await ProfileIdAsync(context, segment.Id));
        await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE public."Segments" SET "Mode" = {null as string} WHERE "Id" = {segment.Id}"""));
    }

    /// <summary>Proves concurrent writers reuse one compatibility profile for the same unknown mode.</summary>
    [PostgresFact]
    public async Task SegmentInsert_ConcurrentSameUnknownMode_UsesOneProfile()
    {
        _fixture.RequireAvailable();
        var firstUser = await _fixture.CreateUserAsync();
        var secondUser = await _fixture.CreateUserAsync();
        var mode = $"Concurrent unknown {Guid.NewGuid():N}";
        var firstTrip = new Trip { Id = Guid.NewGuid(), UserId = firstUser.Id, Name = "First concurrency fixture" };
        var secondTrip = new Trip { Id = Guid.NewGuid(), UserId = secondUser.Id, Name = "Second concurrency fixture" };
        _fixture.RegisterTrip(firstTrip.Id);
        _fixture.RegisterTrip(secondTrip.Id);
        await using var first = _fixture.CreateContext();
        await using var second = _fixture.CreateContext();
        first.Add(new Segment { Id = Guid.NewGuid(), Trip = firstTrip, TripId = firstTrip.Id, UserId = firstUser.Id, Mode = mode });
        second.Add(new Segment { Id = Guid.NewGuid(), Trip = secondTrip, TripId = secondTrip.Id, UserId = secondUser.Id, Mode = mode });

        await Task.WhenAll(first.SaveChangesAsync(), second.SaveChangesAsync());

        await using var verification = _fixture.CreateContext();
        var segments = await verification.Segments.Where(item => item.TripId == firstTrip.Id || item.TripId == secondTrip.Id).ToListAsync();
        var profileId = Assert.Single(segments.Select(item => item.TransportProfileId).Distinct());
        var profile = await verification.Set<TransportProfile>().SingleAsync(item => item.Id == profileId);
        _fixture.RegisterTransportProfile(profile.Id);
    }

    /// <summary>Proves the restrictive FK and real xmin token reject unsafe mutations.</summary>
    [PostgresFact]
    public async Task ProviderInvariants_RejectReferencedDeleteAndStaleXminUpdate()
    {
        _fixture.RequireAvailable();
        var user = await _fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Provider invariant fixture" };
        var profile = new TransportProfile { Id = Guid.NewGuid(), Key = $"fixture-{Guid.NewGuid():N}", Label = "Fixture", Category = "Test" };
        var segment = new Segment { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id, Mode = profile.Key, TransportProfileId = profile.Id };
        _fixture.RegisterTrip(trip.Id);
        _fixture.RegisterTransportProfile(profile.Id);
        await using (var seed = _fixture.CreateContext())
        {
            seed.AddRange(profile, trip, segment);
            await seed.SaveChangesAsync();
        }

        await using var deleteContext = _fixture.CreateContext();
        deleteContext.Remove(await deleteContext.Set<TransportProfile>().SingleAsync(item => item.Id == profile.Id));
        await Assert.ThrowsAsync<DbUpdateException>(() => deleteContext.SaveChangesAsync());

        await using var first = _fixture.CreateContext();
        await using var second = _fixture.CreateContext();
        var firstCopy = await first.Set<TransportProfile>().SingleAsync(item => item.Id == profile.Id);
        var staleCopy = await second.Set<TransportProfile>().SingleAsync(item => item.Id == profile.Id);
        firstCopy.Label = "First update";
        await first.SaveChangesAsync();
        staleCopy.Label = "Stale update";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    /// <summary>Proves a deterministic compatibility UUID collision fails instead of attaching the wrong profile.</summary>
    [PostgresFact]
    public async Task SegmentInsert_DerivedIdCollision_FailsWithoutGuessingProfile()
    {
        _fixture.RequireAvailable();
        var user = await _fixture.CreateUserAsync();
        var mode = $"Collision mode {Guid.NewGuid():N}";
        await using var context = _fixture.CreateContext();
        var derivedId = await DerivedProfileIdAsync(context, mode.Trim().ToLowerInvariant());
        var collidingProfile = new TransportProfile
        {
            Id = derivedId, Key = $"collision-holder-{Guid.NewGuid():N}", Label = "Collision holder", Category = "Test"
        };
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Collision fixture" };
        _fixture.RegisterTransportProfile(collidingProfile.Id);
        _fixture.RegisterTrip(trip.Id);
        context.AddRange(collidingProfile, trip);
        await context.SaveChangesAsync();
        context.Add(new Segment { Id = Guid.NewGuid(), TripId = trip.Id, UserId = user.Id, Mode = mode });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    /// <summary>Proves PostgreSQL rejects non-normalized keys and non-finite planning speeds.</summary>
    [PostgresTheory]
    [InlineData(" Invalid ", 10d)]
    [InlineData("valid-key", 0d)]
    [InlineData("valid-key", -1d)]
    [InlineData("valid-key", double.NaN)]
    [InlineData("valid-key", double.PositiveInfinity)]
    public async Task Constraints_RejectInvalidKeyAndInfiniteSpeed(string key, double speed)
    {
        _fixture.RequireAvailable();
        await using var context = _fixture.CreateContext();
        context.Set<TransportProfile>().Add(new TransportProfile
        {
            Id = Guid.NewGuid(), Key = key, Label = "Invalid", Category = "Test", PlanningSpeedKmh = speed
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    private static Task<Guid?> ProfileIdAsync(ApplicationDbContext context, Guid segmentId) =>
        context.Segments.AsNoTracking().Where(segment => segment.Id == segmentId).Select(segment => segment.TransportProfileId).SingleAsync();

    private static async Task<bool> ColumnExistsAsync(ApplicationDbContext context, string table, string column)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @table AND column_name = @column)";
        command.Parameters.Add(new NpgsqlParameter("table", table));
        command.Parameters.Add(new NpgsqlParameter("column", column));
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<Guid> DerivedProfileIdAsync(ApplicationDbContext context, string key)
    {
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT md5('transport-profile:' || @key)::uuid";
        command.Parameters.Add(new NpgsqlParameter("key", key));
        return (Guid)(await command.ExecuteScalarAsync())!;
    }
}
