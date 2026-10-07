using Npgsql;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Models;

/// <summary>Records isolated provider versions and verifies lifecycle fixture cleanup.</summary>
[Collection(PostgresEnvironmentEvidenceTestCollection.Name)]
public sealed class PostgresEnvironmentEvidenceTests(PostgresImportTestFixture fixture, ITestOutputHelper output)
{
    /// <summary>Proves PostgreSQL 18, reports provider versions, and verifies lifecycle fixture cleanup.</summary>
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
                'Mixed Region matrix', 'Lifecycle transition', 'Lifecycle fixture')),
              current_setting('server_version_num')::integer
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var postgres = reader.GetString(0);
        var postgis = reader.GetString(1);
        var migrationCount = reader.GetInt64(2);
        var fixtureUsers = reader.GetInt64(3);
        var lifecycleTrips = reader.GetInt64(4);
        var postgresMajor = reader.GetInt32(5) / 10000;
        output.WriteLine($"PostgreSQL: {postgres}");
        output.WriteLine($"PostGIS: {postgis}");
        output.WriteLine($"Applied migrations: {migrationCount}");
        output.WriteLine($"Fixture users remaining: {fixtureUsers}");
        output.WriteLine($"Named lifecycle trips remaining: {lifecycleTrips}");
        Assert.Equal(PostgresTestServer.RequiredMajor, postgresMajor);
        Assert.True(migrationCount > 0);
        Assert.Equal(0, fixtureUsers);
        Assert.Equal(0, lifecycleTrips);
    }
}
