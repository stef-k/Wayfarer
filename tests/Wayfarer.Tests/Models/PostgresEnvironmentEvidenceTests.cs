using Npgsql;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Models;

/// <summary>Records isolated provider versions and verifies lifecycle fixture cleanup.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class PostgresEnvironmentEvidenceTests(PostgresImportTestFixture fixture, ITestOutputHelper output)
{
    /// <summary>Reports PostgreSQL/PostGIS versions and proves no lifecycle fixture rows remain.</summary>
    [PostgresFact]
    public async Task IsolatedProvider_ReportsVersionsAndHasNoLifecycleFixtureResidue()
    {
        fixture.RequireAvailable();
        await using var context = fixture.CreateContext();
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT current_setting('server_version'), postgis_full_version(),
              (SELECT count(*) FROM "__EFMigrationsHistory"),
              (SELECT count(*) FROM "AspNetUsers" WHERE "Id" LIKE 'import-fixture-%'),
              (SELECT count(*) FROM "Trips" WHERE "Name" IN (
                'Lifecycle concurrency', 'Destructive concurrency', 'Dependency drift', 'Lock order',
                'Recovery', 'Malformed lifecycle', 'Malformed Region lifecycle', 'Region matrix',
                'Mixed Region matrix', 'Lifecycle transition', 'Lifecycle fixture'))
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var postgres = reader.GetString(0);
        var postgis = reader.GetString(1);
        var migrationCount = reader.GetInt64(2);
        var fixtureUsers = reader.GetInt64(3);
        var lifecycleTrips = reader.GetInt64(4);
        output.WriteLine($"PostgreSQL: {postgres}");
        output.WriteLine($"PostGIS: {postgis}");
        output.WriteLine($"Applied migrations: {migrationCount}");
        output.WriteLine($"Fixture users remaining: {fixtureUsers}");
        output.WriteLine($"Named lifecycle trips remaining: {lifecycleTrips}");
        Assert.True(migrationCount > 0);
        Assert.Equal(0, fixtureUsers);
        Assert.Equal(0, lifecycleTrips);
    }
}
