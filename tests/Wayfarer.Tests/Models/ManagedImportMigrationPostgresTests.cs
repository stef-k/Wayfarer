using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Models;

/// <summary>Qualifies owner access paths and active uniqueness only in an owned disposable database.</summary>
public sealed class ManagedImportMigrationPostgresTests(PostgresMigrationTestFixture fixture, ITestOutputHelper output)
    : IClassFixture<PostgresMigrationTestFixture>
{
    /// <summary>Upgrade and downgrade retain general history lookup while upgrade adds managed execution uniqueness.</summary>
    [PostgresFact]
    public async Task UpgradePreservesOwnerIndexAlongsideManagedActiveConstraint()
    {
        var owner = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        var current = Array.FindIndex(migrations, migration => migration.EndsWith("_AddManagedLocationImportCustody", StringComparison.Ordinal));
        Assert.True(current > 0);
        await migrator.MigrateAsync(migrations[current - 1]);
        await AssertIndexesAsync(db, activeExpected: false);
        await migrator.MigrateAsync();
        await AssertIndexesAsync(db, activeExpected: true);
        Assert.False(db.Database.HasPendingModelChanges());

        // One small owner fixture proves predicate coverage; it makes no selectivity or latency claim.
        var states = new[] { ImportStatus.InProgress, ImportStatus.Stopped, ImportStatus.Completed };
        foreach (var managed in new[] { false, true })
        foreach (var state in states)
            db.LocationImports.Add(new LocationImport
            {
                UserId = owner.Id, ManagedUpload = managed, Status = state,
                FileType = LocationImportFileType.Csv, FilePath = "imports/" + Guid.NewGuid().ToString("N") + ".csv",
                TotalRecords = 0, LastProcessedIndex = 0
            });
        await db.SaveChangesAsync();
        Assert.Equal(6, await db.LocationImports.CountAsync(row => row.UserId == owner.Id));
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN SELECT * FROM \"LocationImports\" WHERE \"UserId\" = @owner ORDER BY \"CreatedAt\" DESC";
            command.Parameters.AddWithValue("owner", owner.Id);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) output.WriteLine(reader.GetString(0));
            // A sequential scan on this tiny fixture is valid; catalog assertions establish the access path.
        }
        db.LocationImports.Add(new LocationImport
        {
            UserId = owner.Id, ManagedUpload = true, Status = ImportStatus.InProgress,
            FileType = LocationImportFileType.Csv, FilePath = "imports/" + Guid.NewGuid().ToString("N") + ".csv",
            TotalRecords = 0, LastProcessedIndex = 0
        });
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("UX_ManagedImport_ActiveOwner", Assert.IsType<PostgresException>(duplicate.InnerException).ConstraintName);
        db.ChangeTracker.Clear();
        await db.LocationImports.Where(row => row.UserId == owner.Id).ExecuteDeleteAsync();
        await migrator.MigrateAsync(migrations[current - 1]);
        await AssertIndexesAsync(db, activeExpected: false);
        await migrator.MigrateAsync();
        await AssertIndexesAsync(db, activeExpected: true);
    }

    /// <summary>Inspect actual index validity, uniqueness, key and predicate without prescribing planner choices.</summary>
    private async Task AssertIndexesAsync(ApplicationDbContext db, bool activeExpected)
    {
        var indexes = await db.Database.SqlQueryRaw<IndexEvidence>("""
            SELECT c.relname AS "Name", i.indisunique AS "Unique", i.indisvalid AS "Valid",
                i.indisready AS "Ready", pg_get_expr(i.indpred, i.indrelid) AS "Predicate",
                pg_get_indexdef(i.indexrelid, 1, true) AS "Key"
            FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
            WHERE i.indrelid = '"LocationImports"'::regclass
            """).ToListAsync();
        foreach (var index in indexes)
            output.WriteLine($"{index.Name}: key={index.Key}; unique={index.Unique}; valid={index.Valid}; ready={index.Ready}; predicate={index.Predicate ?? "<unfiltered>"}");
        var owner = Assert.Single(indexes, index => index.Name == "IX_LocationImports_UserId");
        Assert.Equal("\"UserId\"", owner.Key);
        Assert.Null(owner.Predicate);
        Assert.False(owner.Unique);
        Assert.True(owner.Valid && owner.Ready);
        var active = indexes.SingleOrDefault(index => index.Name == "UX_ManagedImport_ActiveOwner");
        if (!activeExpected) { Assert.Null(active); return; }
        Assert.NotNull(active);
        Assert.Equal("\"UserId\"", active.Key);
        Assert.True(active.Unique && active.Valid && active.Ready);
        Assert.Contains("\"ManagedUpload\"", active.Predicate);
        Assert.Contains("In Progress", active.Predicate);
        Assert.Contains("Stopping", active.Predicate);
    }

    /// <summary>Provider catalog facts returned by the schema inspection query.</summary>
    private sealed class IndexEvidence
    {
        public required string Name { get; set; }
        public required string Key { get; set; }
        public bool Unique { get; set; }
        public bool Valid { get; set; }
        public bool Ready { get; set; }
        public string? Predicate { get; set; }
    }
}
