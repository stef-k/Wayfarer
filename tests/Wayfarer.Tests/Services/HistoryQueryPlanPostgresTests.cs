using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Services;

/// <summary>Bounded production-predicate plan checks; these are database design evidence, not routed scale qualification.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class HistoryQueryPlanPostgresTests(PostgresMigrationTestFixture fixture, ITestOutputHelper output) : IClassFixture<PostgresMigrationTestFixture>
{
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
    }
}
