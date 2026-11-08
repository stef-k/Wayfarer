using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Wayfarer.Migrations;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>The shipped upgrade SQL runs against a session-local copy, never changing another fixture's history.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationKeyMigrationPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Null-only random assignment is atomic, repeatable and non-destructive, including valid zero keys.</summary>
    [PostgresFact]
    public async Task NullOnlyUpgradePreservesEveryOtherValueAndRetriesAfterRollback()
    {
        var owner = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var rows = new[] { Row(owner.Id, null), Row(owner.Id, null), Row(owner.Id, Guid.Empty), Row(owner.Id, Guid.NewGuid()) };
        db.Locations.AddRange(rows); await db.SaveChangesAsync();
        var migration = new AssignStoredLocationKeys();
        var operation = Assert.IsType<SqlOperation>(Assert.Single(migration.UpOperations));
        Assert.False(operation.SuppressTransaction); Assert.Empty(migration.DownOperations);
        var commands = db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, db.Model);
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await Execute("CREATE TEMP TABLE \"Locations\" (LIKE public.\"Locations\" INCLUDING ALL)");
        try
        {
            // Empty installations and completed repeats do no work.
            foreach (var command in commands) await Execute(command.CommandText);
            await using (var copy = new NpgsqlCommand("INSERT INTO pg_temp.\"Locations\" SELECT * FROM public.\"Locations\" WHERE \"UserId\" = @owner", connection))
            { copy.Parameters.AddWithValue("owner", owner.Id); await copy.ExecuteNonQueryAsync(); }
            var before = await Snapshot();
            // Inject a failure after PostgreSQL has evaluated assignment; the transaction must roll back all rows.
            await Execute($$"""
                CREATE FUNCTION pg_temp.reject_location_key() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN IF NEW."Id" = {{rows[1].Id}} THEN RAISE EXCEPTION 'synthetic upgrade failure'; END IF; RETURN NEW; END $body$;
                CREATE TRIGGER reject_key BEFORE UPDATE ON pg_temp."Locations" FOR EACH ROW EXECUTE FUNCTION pg_temp.reject_location_key();
                """);
            await using (var failed = await connection.BeginTransactionAsync())
            {
                await Assert.ThrowsAsync<PostgresException>(async () =>
                {
                    foreach (var command in commands) await Execute(command.CommandText, failed);
                });
                await failed.RollbackAsync();
            }
            Assert.Equal(before, await Snapshot());
            await Execute("DROP TRIGGER reject_key ON pg_temp.\"Locations\"");
            await using (var retry = await connection.BeginTransactionAsync())
            {
                foreach (var command in commands) await Execute(command.CommandText, retry);
                await retry.CommitAsync();
            }
            var after = await Snapshot();
            using var a = JsonDocument.Parse(before); using var b = JsonDocument.Parse(after);
            var prior = a.RootElement.EnumerateArray().ToArray(); var current = b.RootElement.EnumerateArray().ToArray();
            Assert.Equal(4, current.Length);
            for (var index = 0; index < current.Length; index++)
                foreach (var field in prior[index].EnumerateObject().Where(p => p.Name != "IdempotencyKey"))
                    Assert.Equal(field.Value.GetRawText(), current[index].GetProperty(field.Name).GetRawText());
            var keys = current.Select(row => row.GetProperty("IdempotencyKey").GetGuid()).ToArray();
            Assert.Equal(4, keys.Distinct().Count()); Assert.Equal(Guid.Empty, keys[2]); Assert.Equal(rows[3].IdempotencyKey, keys[3]);
            foreach (var command in commands) await Execute(command.CommandText);
            Assert.Equal(after, await Snapshot());
            Assert.Null((await db.Locations.AsNoTracking().SingleAsync(row => row.Id == rows[0].Id)).IdempotencyKey);
        }
        finally { await Execute("DROP TABLE IF EXISTS pg_temp.\"Locations\"; DROP FUNCTION IF EXISTS pg_temp.reject_location_key()"); }

        async Task Execute(string sql, NpgsqlTransaction? transaction = null)
        { await using var command = new NpgsqlCommand(sql, connection, transaction); await command.ExecuteNonQueryAsync(); }
        async Task<string> Snapshot()
        {
            await using var command = new NpgsqlCommand("SELECT jsonb_agg(to_jsonb(l) ORDER BY \"Id\")::text FROM pg_temp.\"Locations\" l", connection);
            return (string)(await command.ExecuteScalarAsync())!;
        }
    }

    /// <summary>Two indistinguishably nearby rows remain distinct, with nontrivial independent original and derived values.</summary>
    private static Location Row(string owner, Guid? key) => new() { UserId = owner, IdempotencyKey = key,
        Coordinates = new(23.73, 37.98) { SRID = 4326 }, TimeZoneId = "Europe/Athens", Timestamp = DateTime.UnixEpoch,
        LocalTimestamp = DateTime.UnixEpoch, Notes = "<p>History / Αθήνα</p>", Address = "", FullAddress = "independent",
        OriginalLocalTimestamp = "2026-11-08T15:00:00.123456789", OriginalTimeZoneId = "GMT+03:00", OriginalUtcOffsetSeconds = 10800,
        SubmittedAtUtc = DateTimeOffset.UnixEpoch, BatteryLevel = 0, IsCharging = false, DeviceModel = "retained" };
}
