using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Models;

/// <summary>Qualifies the atomic cleanup on the established disposable migration database.</summary>
[Collection(PostgresMigrationTestCollection.Name)]
public sealed class ActivityCatalogMigrationPostgresTests(PostgresMigrationTestFixture fixture)
{
    /// <summary>Last migration before the coordinated catalog transition.</summary>
    internal const string Previous = "20261008200336_RetireGroupInvitationEmail";

    /// <summary>Moves cross-owner FKs only, preserves prose and IDs, and qualifies retry/Down/re-Up.</summary>
    [PostgresFact]
    public async Task CleanupPreservesMetadataDescriptionsAndQueuedIds()
    {
        var owner = await fixture.CreateUserAsync();
        var other = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var migrator = db.GetService<IMigrator>();
        try
        {
            await migrator.MigrateAsync(Previous);
            await SeedLegacyAsync(db);
            var first = Row(owner.Id, 102);
            var second = Row(other.Id, 105);
            db.Locations.AddRange(first, second);
            await db.SaveChangesAsync();
            var before = await MetadataAsync(db);
            await db.Database.ExecuteSqlRawAsync("SELECT setval(pg_get_serial_sequence('public.\"ActivityTypes\"', 'Id'), 500, true)");

            await migrator.MigrateAsync();
            db.ChangeTracker.Clear();
            Assert.Equal(before, await MetadataAsync(db));
            Assert.Equal(new[] { 101, 104 }, await db.Locations.OrderBy(l => l.Id).Select(l => l.ActivityTypeId!.Value).ToArrayAsync());
            Assert.Equal("Walking", (await db.ActivityTypes.SingleAsync(a => a.Id == 101)).Name);
            Assert.Equal("Primary\n\nPreserved description from Activity 102 ( WALKING ):\n other \n\nPreserved description from Activity 103 (walking):\nOTHER",
                (await db.ActivityTypes.SingleAsync(a => a.Id == 101)).Description);
            Assert.Equal("Greek", (await db.ActivityTypes.SingleAsync(a => a.Id == 104)).Description);
            Assert.Equal(new[] { 102, 103, 105, 111 }, await db.Set<ActivityTypeIdRedirect>().OrderBy(r => r.LegacyId).Select(r => r.LegacyId).ToArrayAsync());
            Assert.Equal(7, await db.ActivityTypes.CountAsync());
            Assert.Equal(101, await new ActivityCatalog(db).ResolveIdAsync(102));
            await migrator.MigrateAsync(); // EF history is a no-op.
            var created = new ActivityType { Name = "GPS" };
            db.ActivityTypes.Add(created);
            await db.SaveChangesAsync();
            Assert.True(created.Id > 500);
            var reuse = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"ActivityTypes\" (\"Id\", \"Name\") VALUES (102, 'Retired')"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, reuse.SqlState);

            await migrator.MigrateAsync(Previous);
            Assert.Equal(4, await db.Set<ActivityTypeIdRedirect>().CountAsync());
            Assert.Equal(101, await new ActivityCatalog(db).ResolveIdAsync(102));
            Assert.Equal(before, await MetadataAsync(db));
            await migrator.MigrateAsync(); // Cleanup itself must also be idempotent.
            Assert.Equal("Primary\n\nPreserved description from Activity 102 ( WALKING ):\n other \n\nPreserved description from Activity 103 (walking):\nOTHER",
                await db.ActivityTypes.Where(a => a.Id == 101).Select(a => a.Description).SingleAsync());
        }
        finally { await CleanAsync(db, owner.Id, other.Id); }
    }

    /// <summary>Unknown FKs, invalid names, index capacity and a late write failure all roll back fully.</summary>
    [PostgresTheory]
    [InlineData("invalid")]
    [InlineData("reference")]
    [InlineData("capacity")]
    [InlineData("injected")]
    public async Task FailureLeavesOriginalCatalogAndReferences(string failure)
    {
        var owner = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var migrator = db.GetService<IMigrator>();
        try
        {
            await migrator.MigrateAsync(Previous);
            await SeedLegacyAsync(db);
            db.Locations.Add(Row(owner.Id, 102));
            await db.SaveChangesAsync();
            if (failure == "invalid")
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ActivityTypes\" SET \"Name\" = {"A\u0001B"} WHERE \"Id\" = 103");
            if (failure == "reference")
                await db.Database.ExecuteSqlRawAsync("CREATE TABLE activity_extra_reference (id integer REFERENCES \"ActivityTypes\"(\"Id\"))");
            if (failure == "capacity")
                await db.Database.ExecuteSqlRawAsync("UPDATE \"ActivityTypes\" SET \"Name\" = (SELECT string_agg(md5(g::text), '') FROM generate_series(1, 200) g) WHERE \"Id\" = 103");
            if (failure == "injected")
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE FUNCTION public.activity_test_fail() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN RAISE EXCEPTION 'injected cleanup failure'; END $$;
                    CREATE TRIGGER activity_test_fail BEFORE DELETE ON "ActivityTypes"
                        FOR EACH ROW EXECUTE FUNCTION public.activity_test_fail();
                    """);
            var before = await CatalogJsonAsync(db);
            var metadata = await MetadataAsync(db);
            var error = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync());
            if (failure == "capacity") Assert.Equal(PostgresErrorCodes.ProgramLimitExceeded, error.SqlState);
            else Assert.Contains(failure == "invalid" ? "ACTIVITY_INVALID" : failure == "reference" ? "ACTIVITY_UNACCOUNTED" : "injected", error.MessageText);
            Assert.Equal(before, await CatalogJsonAsync(db));
            Assert.Equal(metadata, await MetadataAsync(db));
            Assert.Equal(102, await db.Locations.Select(l => l.ActivityTypeId).SingleAsync());
            Assert.Empty(await db.Set<ActivityTypeIdRedirect>().ToListAsync());
            Assert.DoesNotContain("20261013152203_ActivityCatalogIntegrity", await db.Database.GetAppliedMigrationsAsync());
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS activity_extra_reference; DROP TRIGGER IF EXISTS activity_test_fail ON \"ActivityTypes\"; DROP FUNCTION IF EXISTS public.activity_test_fail()");
            await db.Database.ExecuteSqlRawAsync("UPDATE \"ActivityTypes\" SET \"Name\" = 'walking' WHERE \"Id\" = 103");
            await migrator.MigrateAsync(); // Retry after the real cause is removed.
            Assert.Equal(101, await db.Locations.Select(l => l.ActivityTypeId).SingleAsync());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS activity_extra_reference; DROP TRIGGER IF EXISTS activity_test_fail ON \"ActivityTypes\"; DROP FUNCTION IF EXISTS public.activity_test_fail()");
            await CleanAsync(db, owner.Id);
        }
    }

    /// <summary>Seeds representative pre-schema duplicates through raw SQL while the new key is absent.</summary>
    internal static Task SeedLegacyAsync(ApplicationDbContext db) => db.Database.ExecuteSqlRawAsync("""
        INSERT INTO "ActivityTypes" ("Id", "Name", "Description") VALUES
            (101, ' Walking ', 'Primary'), (102, ' WALKING ', ' other '), (103, 'walking', 'OTHER'),
            (104, 'Πεζοπορία', NULL), (105, U&'\00A0ΠΕΖΟΠΟΡΊΑ\3000', 'Greek'),
            (106, 'Eating Out', NULL), (107, 'Eating  Out', NULL), (108, E'Eating\tOut', NULL),
            (109, U&'Eating\00A0Out', NULL), (110, repeat('α', 700), NULL),
            (111, 'wAlKiNg', ' other ');
        """);

    /// <summary>Captures every non-FK database field, including geometry and nullable metadata.</summary>
    private static Task<string> MetadataAsync(ApplicationDbContext db) => db.Database.SqlQueryRaw<string>(
        "SELECT coalesce(jsonb_agg(to_jsonb(l) - 'ActivityTypeId' ORDER BY \"Id\")::text, '[]') AS \"Value\" FROM \"Locations\" l").SingleAsync();

    /// <summary>Captures the old schema directly without selecting the not-yet-generated EF property.</summary>
    private static Task<string> CatalogJsonAsync(ApplicationDbContext db) => db.Database.SqlQueryRaw<string>(
        "SELECT jsonb_agg(to_jsonb(a) ORDER BY \"Id\")::text AS \"Value\" FROM \"ActivityTypes\" a").SingleAsync();

    /// <summary>Creates stored metadata whose only cleanup mutation may be its Activity FK.</summary>
    internal static Location Row(string owner, int? activity) => new()
    {
        UserId = owner, ActivityTypeId = activity, Coordinates = new Point(20, 10) { SRID = 4326 },
        Timestamp = DateTime.UtcNow.AddDays(-1), LocalTimestamp = DateTime.UtcNow.AddDays(-1),
        TimeZoneId = "UTC", IdempotencyKey = Guid.NewGuid(), Notes = "Preserved", LocationType = " custom ",
        DeviceModel = "fixture", Source = "migration", Address = "address", Altitude = 50
    };

    /// <summary>Restores schema and deletes only data in this fixture-owned disposable database.</summary>
    internal static async Task CleanAsync(ApplicationDbContext db, params string[] owners)
    {
        db.ChangeTracker.Clear();
        await db.Users.Where(u => owners.Contains(u.Id)).ExecuteDeleteAsync();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"ActivityTypeIdRedirects\"; DELETE FROM \"ActivityTypes\"");
        await db.Database.MigrateAsync();
    }
}
