using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Point = NetTopologySuite.Geometries.Point;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Services;

/// <summary>Bounded production-predicate plan checks; these are database design evidence, not routed scale qualification.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class HistoryQueryPlanPostgresTests(PostgresMigrationTestFixture fixture, ITestOutputHelper output) : IClassFixture<PostgresMigrationTestFixture>
{
    /// <summary>Fresh creation and reversible upgrade retain the UTC keys and add only the supported status payload.</summary>
    [PostgresFact]
    public async Task UtcCoverageMigrationMatchesModelAndPreservesRows()
    {
        const string original = "CREATE INDEX \"IX_Location_HistoryUtc\" ON public.\"Locations\" USING btree (\"UserId\", \"LocalTimestamp\", \"Id\") INCLUDE (\"Coordinates\", \"CaptureLocalTimestamp\", \"ActivityTypeId\")";
        var corrected = original.Replace("\"ActivityTypeId\")", "\"ActivityTypeId\", \"HistoryTimeStatus\")");
        var owner = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        db.Add(new Location { UserId = owner.Id, TimeZoneId = "UTC", Coordinates = new Point(23.7, 38) { SRID = 4326 },
            LocalTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        await db.SaveChangesAsync();
        var before = await db.Database.SqlQuery<string>($"""
            SELECT row_to_json(l)::text AS "Value" FROM "Locations" l WHERE "UserId"={owner.Id}
            """).SingleAsync();
        Assert.Equal(corrected, await UtcIndexDefinitionAsync(db));
        Assert.True(await new LocationHistoryPreparation(db).IndexesReadyAsync(default));
        Assert.False(db.Database.HasPendingModelChanges());
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261023175322_LocationHistoryPreparation");
        try
        {
            Assert.Equal(original, await UtcIndexDefinitionAsync(db));
            Assert.True(await new LocationHistoryPreparation(db).IndexesReadyAsync(default));
            await migrator.MigrateAsync();
            Assert.Equal(corrected, await UtcIndexDefinitionAsync(db));
            Assert.True(await new LocationHistoryPreparation(db).IndexesReadyAsync(default));
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            var after = await db.Database.SqlQuery<string>($"""
                SELECT row_to_json(l)::text AS "Value" FROM "Locations" l WHERE "UserId"={owner.Id}
                """).SingleAsync();
            Assert.Equal(before, after);
            Assert.True(await db.Database.SqlQueryRaw<bool>("""
                SELECT indisvalid AND indisready AND indpred IS NULL AS "Value" FROM pg_index
                WHERE indexrelid='public."IX_Location_HistoryUtc"'::regclass
                """).SingleAsync());
        }
        finally { await migrator.MigrateAsync(); }
    }

    /// <summary>Inspect the actual provider index rather than inferring it from generated migration text.</summary>
    private static Task<string> UtcIndexDefinitionAsync(ApplicationDbContext db) => db.Database.SqlQueryRaw<string>("""
        SELECT pg_get_indexdef('public."IX_Location_HistoryUtc"'::regclass) AS "Value"
        """).SingleAsync();

    /// <summary>Every supported grid retains all distinct cells, decoded bounds/order, singletons and an unmapped bucket.</summary>
    [PostgresFact]
    public async Task OverviewRetainsEveryCellAcrossAllSupportedGridSizes()
    {
        var owner = await fixture.CreateUserAsync(); await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "Locations" ("UserId","Timestamp","LocalTimestamp","TimeZoneId","Coordinates")
            SELECT {{owner.Id}}, '2025-01-01Z'::timestamptz+g*interval '1 day',
                '2025-01-01Z'::timestamptz+g*interval '1 day', 'UTC',
                ST_SetSRID(ST_MakePoint(-180+(x+0.5)*360/g,-90+(y+0.5)*180/g),4326)::geography
            FROM generate_series(1,16) g CROSS JOIN LATERAL generate_series(0,g-1) x
                CROSS JOIN LATERAL generate_series(0,g-1) y
            UNION ALL
            SELECT {{owner.Id}}, '2025-01-01Z'::timestamptz+g*interval '1 day',
                '2025-01-01Z'::timestamptz+g*interval '1 day', 'UTC', 'POINT EMPTY'::geography
            FROM generate_series(1,16) g
            """);
        var service = new HistoryReadService(db, new HistoryCursor(new EphemeralDataProtectionProvider()));
        for (var grid = 1; grid <= 16; grid++)
        {
            var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(grid);
            var query = HistoryQueryTests.Parse($"?timeBasis=utc&timeMode=interval&viewerZone=UTC&startUtc={HistoryQuery.Utc(start)}&endUtc={HistoryQuery.Utc(start.AddDays(1))}&grid={grid}", true);
            var result = JsonSerializer.SerializeToElement(await service.OverviewAsync(owner.Id, query, default));
            Assert.Equal((grid * grid + 1).ToString(), result.GetProperty("knownMatchCount").GetString());
            Assert.Equal((grid * grid).ToString(), result.GetProperty("mappedMatchCount").GetString());
            Assert.Equal("1", result.GetProperty("unmappedMatchCount").GetString());
            var cells = result.GetProperty("cells").EnumerateArray().ToArray();
            Assert.Equal(grid * grid, cells.Length);
            Assert.Equal(cells.Length, cells.Select(cell => cell.GetProperty("cellKey").GetString()).Distinct().Count());
            for (var index = 0; index < cells.Length; index++)
            {
                var bounds = cells[index].GetProperty("bounds");
                Assert.Equal(-180 + index / grid * 360.0 / grid, bounds.GetProperty("west").GetDouble());
                Assert.Equal(-90 + index % grid * 180.0 / grid, bounds.GetProperty("south").GetDouble());
                Assert.Equal("1", cells[index].GetProperty("recordCount").GetString());
                Assert.True(cells[index].GetProperty("singleton").GetProperty("locationId").GetInt32() > 0);
                Assert.Equal(JsonValueKind.Null, cells[index].GetProperty("singleton").GetProperty("uuid").ValueKind);
            }
        }
        Assert.Throws<HistoryRequestException>(() => HistoryQueryTests.Parse("?timeBasis=utc&timeMode=interval&viewerZone=UTC&startUtc=2025-01-01T00:00:00Z&endUtc=2026-01-01T00:00:00Z&grid=17", true));
    }

    /// <summary>Singleton IDs resolve their own nullable UUIDs; multiple records, unmapped positions and foreign owners stay distinct.</summary>
    [PostgresFact]
    public async Task OverviewSingletonIdentityKeepsNullsAndOwnerIsolation()
    {
        var owner = await fixture.CreateUserAsync(); var foreign = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var instant = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var uuid = Guid.NewGuid();
        var rows = new[] { (-150.0, (Guid?)null), (-60.0, (Guid?)uuid), (30.0, (Guid?)Guid.NewGuid()), (30.0, (Guid?)null), (double.NaN, (Guid?)Guid.NewGuid()) }
            .Select(pair => new Location { UserId = owner.Id, Timestamp = instant, LocalTimestamp = instant, TimeZoneId = "UTC",
                Coordinates = new Point(pair.Item1, double.IsNaN(pair.Item1) ? double.NaN : 10) { SRID = 4326 }, IdempotencyKey = pair.Item2 }).ToArray();
        db.AddRange(rows);
        // Owner-scoped portable aliases may coincide; neither that alias nor the foreign cell can become this owner's identity.
        db.Add(new Location { UserId = foreign.Id, Timestamp = instant, LocalTimestamp = instant, TimeZoneId = "UTC",
            Coordinates = new Point(-150, 10) { SRID = 4326 }, IdempotencyKey = uuid });
        await db.SaveChangesAsync();
        var query = HistoryQueryTests.Parse("?timeBasis=utc&timeMode=interval&viewerZone=UTC&startUtc=2025-01-01T00:00:00Z&endUtc=2026-01-01T00:00:00Z&grid=4", true);
        var result = JsonSerializer.SerializeToElement(await new HistoryReadService(db, new HistoryCursor(new EphemeralDataProtectionProvider()))
            .OverviewAsync(owner.Id, query, default));
        Assert.Equal("5", result.GetProperty("knownMatchCount").GetString());
        Assert.Equal("4", result.GetProperty("mappedMatchCount").GetString());
        Assert.Equal("1", result.GetProperty("unmappedMatchCount").GetString());
        var cells = result.GetProperty("cells").EnumerateArray().ToArray(); Assert.Equal(3, cells.Length);
        var singletons = cells.Where(cell => cell.GetProperty("recordCount").GetString() == "1")
            .Select(cell => cell.GetProperty("singleton")).ToDictionary(row => row.GetProperty("locationId").GetInt32());
        Assert.Equal(new[] { rows[0].Id, rows[1].Id }, singletons.Keys.Order());
        Assert.Equal(JsonValueKind.Null, singletons[rows[0].Id].GetProperty("uuid").ValueKind);
        Assert.Equal(uuid.ToString("D"), singletons[rows[1].Id].GetProperty("uuid").GetString());
        Assert.Equal(JsonValueKind.Null, Assert.Single(cells, cell => cell.GetProperty("recordCount").GetString() == "2").GetProperty("singleton").ValueKind);
    }

    /// <summary>Owner/time, persisted wall-time, folded text and geometry paths remain eligible after exact residuals.</summary>
    [PostgresFact]
    public async Task ProductionPredicatesRetainIndexPathsAndBoundedDatabaseWork()
    {
        var owner = await fixture.CreateUserAsync(); await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "Locations" ("UserId","Timestamp","LocalTimestamp","TimeZoneId","Coordinates","Notes","Address")
            SELECT {{owner.Id}}, '2025-01-01Z'::timestamptz+n*interval '1 hour',
                '2025-01-01Z'::timestamptz+n*interval '1 hour', 'UTC',
                ST_SetSRID(ST_MakePoint(139.76 + (n%100)*0.0001,35.68),4326)::geography,
                '<p>Diary '||n||' café '||md5(n::text)||'</p>', CASE WHEN n=5000 THEN 'rare station' ELSE 'ordinary address '||n END
            FROM generate_series(1,10000) n
            """);
        var preparation = new LocationHistoryPreparation(db);
        foreach (var kind in new[] { "time", "text" })
        {
            await preparation.BeginAsync(kind, false, default);
            while (!await preparation.PrepareBatchAsync(kind, default)) { }
        }
        await db.Database.ExecuteSqlRawAsync("ANALYZE \"Locations\"");
        var utc = "?timeBasis=utc&timeMode=interval&viewerZone=UTC&startUtc=2025-07-28T00:00:00Z&endUtc=2025-07-29T00:00:00Z";
        var capture = "?timeBasis=capture&timeMode=days&viewerZone=UTC&fromDate=2025-07-28&throughDate=2025-07-28";
        var viewer = "?timeBasis=viewer&timeMode=days&viewerZone=Europe%2FAthens&fromDate=2025-07-28&throughDate=2025-07-28";
        foreach (var (queryText, expected) in new[] { (utc, "IX_Location_HistoryUtc"), (viewer, "IX_Location_HistoryUtc"), (capture, "IX_Location_HistoryCapture"),
                     (utc.Replace("2025-07-28", "2025-01-01").Replace("2025-07-29", "2026-12-31") + "&q=rare%20station", "IX_Location_HistoryText"),
                     (utc.Replace("2025-07-28", "2025-01-01").Replace("2025-07-29", "2026-12-31") + "&west=139.7600&south=35.67&east=139.7601&north=35.69", "IX_Location_HistoryGeometry") })
        {
            var query = HistoryQueryTests.Parse(queryText); var predicate = new HistoryPredicate(owner.Id, query, default);
            var sql = $"SELECT l.\"Id\" FROM \"Locations\" l WHERE {predicate.Where} AND {predicate.Matches} ORDER BY l.\"LocalTimestamp\" DESC,l.\"Id\" DESC LIMIT 101";
            var explain = "EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) " + sql;
            var natural = await db.Database.SqlQueryRaw<string>(explain, predicate.CopyParameters()).ToListAsync();
            output.WriteLine(JsonSerializer.Serialize(new { Scope = query.Scope, SQL = sql, Parameters = predicate.Parameters.Select(p => new { p.ParameterName, p.Value }), Natural = natural }));
            var candidateSql = $"EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) SELECT count(*) FROM \"Locations\" l WHERE {predicate.Where} AND {predicate.Truth} IS NULL";
            var candidatePlan = await db.Database.SqlQueryRaw<string>(candidateSql, predicate.CopyParameters()).ToListAsync();
            output.WriteLine("Whole-scope uncertainty: " + string.Join(Environment.NewLine, candidatePlan));
            // Eligibility is separate from natural cost choice on a small fixture. Do not force production planner settings.
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = off; SET LOCAL enable_indexscan = off");
            var eligible = await db.Database.SqlQueryRaw<string>(explain, predicate.CopyParameters()).ToListAsync();
            output.WriteLine(string.Join(Environment.NewLine, eligible));
            // The planner may prefer another applicable index; verify the intended predicate's own path separately if necessary.
            Assert.Contains(natural.Concat(eligible), line => line.Contains(expected));
            Assert.DoesNotContain(natural, line => line.Contains("external merge"));
            await transaction.RollbackAsync();
        }
        // Inspect the executed overview, rather than a simpler stand-in aggregation.
        var tap = new OverviewCapture();
        await using var overviewDb = fixture.CreateContext(tap);
        var overview = HistoryQueryTests.Parse(utc.Replace("2025-07-28", "2025-01-01").Replace("2025-07-29", "2026-12-31"), true);
        await new HistoryReadService(overviewDb, new HistoryCursor(new EphemeralDataProtectionProvider()))
            .OverviewAsync(owner.Id, overview, default);
        var plan = await db.Database.SqlQueryRaw<string>("EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) " + tap.Sql, tap.Parameters).ToListAsync();
        output.WriteLine("Executed overview: " + string.Join(Environment.NewLine, plan));
        var grouping = Assert.Single(plan, line => line.Contains("Group Key:"));
        Assert.DoesNotContain("st_x", grouping, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("st_y", grouping, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("st_isempty", grouping, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(plan, line => line.Contains("external merge"));
    }

    /// <summary>Retain the actual production command and typed values for a natural plan replay.</summary>
    private sealed class OverviewCapture : DbCommandInterceptor
    {
        internal string Sql { get; private set; } = "";
        internal object[] Parameters { get; private set; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("WITH matches AS"))
            {
                Sql = command.CommandText;
                Parameters = command.Parameters.Cast<NpgsqlParameter>().Select(parameter => (object)parameter.Clone()).ToArray();
            }
            return ValueTask.FromResult(result);
        }
    }
}
