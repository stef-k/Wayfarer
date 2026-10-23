using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Services;

/// <summary>One bounded synthetic preparation/write trial through the production derivation and existing fixture.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationHistoryCostPostgresTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly PostgresMigrationTestFixture fixture = new();

    /// <summary>Uses the established disposable database owner so earlier fixture allocation cannot skew growth.</summary>
    public Task InitializeAsync() => fixture.InitializeAsync();

    /// <summary>Removes only the exact database owned by that fixture.</summary>
    public Task DisposeAsync() => fixture.DisposeAsync();
    [PostgresFact]
    public async Task DistinctContentPreparationAndRepresentativeWriteCosts()
    {
        var owner = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        // Seed canonical originals only, as an existing installation would present them after schema migration.
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "Locations" ("UserId", "Timestamp", "LocalTimestamp", "TimeZoneId", "Coordinates", "Notes",
                "Address", "FullAddress", "ProviderAddressLine1", "AddressNumber", "StreetName", "PostCode", "Place", "Region", "Country", "ResolvedFeatureName")
            SELECT {{owner.Id}}, '2025-01-01Z'::timestamptz + n * interval '5 minutes',
                '2025-01-01Z'::timestamptz + n * interval '5 minutes', 'UTC',
                ST_SetSRID(ST_MakePoint(139.76 + n % 100 * 0.0001, 35.68),4326)::geography,
                '<p>Visit ' || n || ' café Καφές 東京</p><p>Distinct diary ' || md5(n::text) || ' and walking near station ' || n || '</p>',
                n || ' Café road', n || ' Café road Tokyo Japan', n || ' independent provider line', n::text,
                'Café road', '100-' || n, 'Tokyo', 'Kantō', 'Japan', 'Station ' || n
            FROM generate_series(1,10000) n
            """);
        var before = await SizesAsync(db);
        var walBefore = await WalAsync(db);
        var service = new LocationHistoryPreparation(db);
        var batches = new List<double>();
        var total = Stopwatch.StartNew();
        foreach (var kind in new[] { "time", "text" })
        {
            await service.BeginAsync(kind, true, default);
            bool done;
            do
            {
                var batch = Stopwatch.StartNew();
                done = await service.PrepareBatchAsync(kind, default);
                batches.Add(batch.Elapsed.TotalMilliseconds);
            } while (!done);
        }
        total.Stop();
        await db.Database.ExecuteSqlRawAsync("ANALYZE \"Locations\"");
        var after = await SizesAsync(db);
        var walAfter = await WalAsync(db);
        output.WriteLine(JsonSerializer.Serialize(new { Trial = "preparation", Rows = 10000, ElapsedMs = total.Elapsed.TotalMilliseconds,
            MaxBatchMs = batches.Max(), Before = before, After = after, WalBytes = walAfter - walBefore,
            TimeVersion = LocationHistoryDerivation.TimeVersion, TextVersion = LocationHistoryDerivation.TextVersion }));
        foreach (var scenario in new[] { "create/import-100", "notes/address-edit", "enrichment-publication" })
            foreach (var derived in new[] { false, true })
                await WriteCostAsync(db, owner.Id, scenario, derived);
        await CheckMatchingAsync(db, owner.Id);
        Assert.True((await service.StatusAsync("time", default)).Ready);
        Assert.True((await service.StatusAsync("text", default)).Ready);
    }

    /// <summary>Equivalent canonical SQL with/without production derivation, including transaction/lock and WAL cost.</summary>
    private async Task WriteCostAsync(ApplicationDbContext db, string owner, string scenario, bool derived)
    {
        var wal = await WalAsync(db);
        var clock = Stopwatch.StartNew();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var locked = Stopwatch.StartNew();
        var rows = await db.Locations.FromSqlInterpolated($$"""
            SELECT * FROM "Locations" WHERE "UserId" = {{owner}} ORDER BY "Id" LIMIT 100 FOR UPDATE
            """).AsNoTracking().ToListAsync();
        if (scenario == "create/import-100")
        {
            // Each trial is rolled back, so both variants retain the identical fixture and index population.
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO "Locations" ("UserId", "Timestamp", "LocalTimestamp", "TimeZoneId", "Coordinates", "Notes", "Address")
                SELECT "UserId", "Timestamp", "LocalTimestamp", "TimeZoneId", "Coordinates", "Notes", "Address"
                FROM "Locations" WHERE "UserId" = {{owner}} ORDER BY "Id" LIMIT 100
                """);
            rows = await db.Locations.Where(row => row.UserId == owner).OrderByDescending(row => row.Id).Take(100).AsNoTracking().ToListAsync();
        }
        else
        {
            foreach (var row in rows)
            {
                if (scenario == "notes/address-edit")
                { row.Notes = "<p>new café " + row.Id + "</p>"; row.Address = "Edited address " + row.Id; }
                else { row.Place = "New café locality " + row.Id; row.ResolvedFeatureName = "New station " + row.Id; }
                await db.Locations.Where(item => item.Id == row.Id).ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Notes, row.Notes).SetProperty(item => item.Address, row.Address)
                    .SetProperty(item => item.Place, row.Place).SetProperty(item => item.ResolvedFeatureName, row.ResolvedFeatureName));
            }
        }
        if (derived)
            foreach (var row in rows) await LocationHistoryStorage.UpdateAsync(db, row, scenario == "create/import-100", true, default);
        var lockMs = locked.Elapsed.TotalMilliseconds;
        await transaction.RollbackAsync();
        output.WriteLine(JsonSerializer.Serialize(new { Trial = scenario, Derived = derived, Rows = 100,
            ElapsedMs = clock.Elapsed.TotalMilliseconds, TransactionLockMs = lockMs, WalBytes = await WalAsync(db) - wal }));
    }

    /// <summary>Qualifies accent-folded stored/query parity, C-collated literals and actual trigram eligibility.</summary>
    private async Task CheckMatchingAsync(ApplicationDbContext db, string owner)
    {
        foreach (var phrase in new[] { "café", "cafe\u0301", "Καφές", "東京", "100%_\\", "a", "%" })
        {
            var normalized = LocationHistoryDerivation.NormalizeText(phrase);
            var pattern = "%" + normalized.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            var counts = await db.Database.SqlQuery<long>($$"""
                SELECT count(*) AS "Value" FROM "Locations" WHERE "UserId" = {{owner}}
                    AND strpos("HistorySearchText", {{normalized}}) > 0
                """).SingleAsync();
            var like = await db.Database.SqlQuery<long>($$"""
                SELECT count(*) AS "Value" FROM "Locations" WHERE "UserId" = {{owner}}
                    AND "HistorySearchText" LIKE {{pattern}} ESCAPE '\'
                """).SingleAsync();
            Assert.Equal(counts, like);
            output.WriteLine($"Matching {JsonSerializer.Serialize(phrase)} => {counts}; LIKE/strpos agree");
        }
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = off");
        var plan = await db.Database.SqlQueryRaw<string>("""
            EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) SELECT "Id" FROM "Locations"
            WHERE "HistorySearchText" LIKE '%CAFE%' ESCAPE '\'
            """).ToListAsync();
        Assert.Contains(plan, line => line.Contains("IX_Location_HistoryText"));
        foreach (var line in plan) output.WriteLine(line);
    }

    /// <summary>Relation footprints include MVCC growth; no vacuum disguises temporary preparation headroom.</summary>
    private static Task<string> SizesAsync(ApplicationDbContext db) => db.Database.SqlQueryRaw<string>("""
        SELECT json_build_object('tableToastBytes',pg_table_size('"Locations"'),
            'allIndexBytes',pg_indexes_size('"Locations"'), 'historyIndexBytes',
            (SELECT sum(pg_relation_size(indexrelid)) FROM pg_index JOIN pg_class c ON c.oid=indexrelid
             WHERE indrelid='"Locations"'::regclass AND c.relname LIKE 'IX_Location_History%'))::text AS "Value"
        """).SingleAsync();

    /// <summary>Cluster WAL is an observed upper bound if unrelated concurrent work exists.</summary>
    private static Task<decimal> WalAsync(ApplicationDbContext db) => db.Database.SqlQueryRaw<decimal>(
        "SELECT pg_wal_lsn_diff(pg_current_wal_insert_lsn(), '0/0'::pg_lsn) AS \"Value\"").SingleAsync();
}
