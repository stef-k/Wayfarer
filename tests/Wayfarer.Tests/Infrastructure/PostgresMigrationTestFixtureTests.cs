using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using System.Runtime.ExceptionServices;
using Xunit;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>Proves migration-history work is isolated from the persistent PostgreSQL fixture.</summary>
[Collection(PostgresMigrationTestCollection.Name)]
public sealed class PostgresMigrationTestFixtureTests(PostgresMigrationTestFixture fixture)
{
    private const string PersistentDatabase = "wayfarer_import_tests";
    private const string PreviousMigration = "20260818161609_ExternalRoutingCredentialRequirement";

    /// <summary>Proves the fixture owns a guarded database distinct from the persistent fixture.</summary>
    [PostgresFact]
    public async Task InitializeAsync_UsesGuardedDisposableDatabase()
    {
        fixture.RequireAvailable();

        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        Assert.StartsWith(PostgresMigrationTestFixture.DatabasePrefix, builder.Database, StringComparison.Ordinal);
        Assert.NotEqual(PersistentDatabase, builder.Database);
    }

    /// <summary>Proves an empty disposable database reaches the latest real migration and model.</summary>
    [PostgresFact]
    public async Task InitializeAsync_AppliesLatestMigrationFromEmptyState()
    {
        fixture.RequireAvailable();

        await using var context = fixture.CreateContext();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
    }

    /// <summary>Proves representative historical migration work can be repeated in one fixture.</summary>
    [PostgresFact]
    public async Task MigrationCycle_DownAndUp_CanExecuteTwice()
    {
        fixture.RequireAvailable();
        await using var context = fixture.CreateContext();
        var migrator = context.GetService<IMigrator>();
        Exception? primary = null;

        try
        {
            for (var cycle = 0; cycle < 2; cycle++)
            {
                await migrator.MigrateAsync(PreviousMigration);
                await migrator.MigrateAsync();
            }

            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        }
        catch (Exception failure) { primary = failure; }
        finally
        {
            try { await migrator.MigrateAsync(); }
            catch when (primary is not null)
            {
                primary.Data["PostgresMigrationRestore"] = "Latest migration restoration also failed.";
            }
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    /// <summary>Proves disposable migration cycles cannot alter persistent migration or attribute history.</summary>
    [PostgresFact]
    public async Task FixtureLifetime_LeavesPersistentMigrationAndAttributeHistoryUnchanged()
    {
        var before = await ReadPersistentSnapshotAsync();
        fixture.RequireAvailable();
        await using var context = fixture.CreateContext();
        var migrator = context.GetService<IMigrator>();
        Exception? primary = null;

        try
        {
            await migrator.MigrateAsync(PreviousMigration);
            await migrator.MigrateAsync();
            Assert.Equal(before, await ReadPersistentSnapshotAsync());
        }
        catch (Exception failure) { primary = failure; }
        finally
        {
            try { await migrator.MigrateAsync(); }
            catch when (primary is not null)
            {
                primary.Data["PostgresMigrationRestore"] = "Latest migration restoration also failed.";
            }
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    /// <summary>Proves cleanup fails closed when its target is not the fixture-owned database.</summary>
    [Fact]
    public void ValidateCleanupTarget_RejectsUnexpectedDatabaseName()
    {
        var owned = $"{PostgresMigrationTestFixture.DatabasePrefix}{Guid.NewGuid():N}";

        Assert.Throws<InvalidOperationException>(() =>
            PostgresMigrationTestFixture.ValidateCleanupTarget(owned, $"{PostgresMigrationTestFixture.DatabasePrefix}{Guid.NewGuid():N}"));
        Assert.Throws<InvalidOperationException>(() =>
            PostgresMigrationTestFixture.ValidateCleanupTarget(owned, PersistentDatabase));
    }

    private static async Task<PersistentSnapshot> ReadPersistentSnapshotAsync()
    {
        var value = Environment.GetEnvironmentVariable("WAYFARER_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(value))
            throw Xunit.Sdk.SkipException.ForSkip("Set WAYFARER_TEST_POSTGRES_CONNECTION to run PostgreSQL fixture tests.");
        var builder = new NpgsqlConnectionStringBuilder(value);
        if (!string.Equals(builder.Database, PersistentDatabase, StringComparison.Ordinal))
            throw new InvalidOperationException("The PostgreSQL test connection must name the guarded persistent database.");

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT count(*) FROM public."__EFMigrationsHistory"),
                count(*) FILTER (WHERE a.attisdropped),
                coalesce(max(a.attnum), 0)
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND a.attnum > 0
            """;
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new PersistentSnapshot(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt16(2));
    }

    private sealed record PersistentSnapshot(long MigrationCount, long DroppedAttributeCount, short HighestAttributeNumber);
}
