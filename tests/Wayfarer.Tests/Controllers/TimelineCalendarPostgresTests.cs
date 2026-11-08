using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using GeoTimeZone;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodaTime;
using Npgsql;
using Point = NetTopologySuite.Geometries.Point;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Controllers;

/// <summary>Real cookie-authorized PostgreSQL calendar reads, continuity, readiness, isolation and resource observations.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed partial class TimelineCalendarPostgresTests(PostgresMigrationTestFixture fixture, ITestOutputHelper output)
    : TestBase, IClassFixture<PostgresMigrationTestFixture>
{
    private const string Route = "/User/Timeline/calendar/";
    private const string Day = "?dateType=day&year=2026&month=10&day=25&viewerZone=Europe%2FAthens&timeBasis=";

    /// <summary>Per-record capture zones, repeated hours and unmapped/candidate facts agree across every private consumer.</summary>
    [PostgresFact]
    public async Task CaptureViewerMembershipAndRichEndpointsAgree()
    {
        var owner = await fixture.CreateUserAsync();
        var tokyo = Row(owner.Id, "2026-10-25T15:30Z", 139.76, 35.68, "Japan");
        var athens = Row(owner.Id, "2026-10-25T16:00Z");
        var fold1 = Row(owner.Id, "2026-10-25T00:30Z"); var fold2 = Row(owner.Id, "2026-10-25T01:30Z");
        var unmapped = Row(owner.Id, "2026-10-25T12:00Z"); unmapped.Coordinates = Point.Empty;
        await SeedAsync(tokyo, athens, fold1, fold2, unmapped);
        await ReadyAsync();
        await using var app = await HostAsync(); using var client = Client(app, owner.Id);
        foreach (var (basis, expected, candidates, mapped) in new[] { ("capture", 3, 1, 3), ("viewer", 5, 0, 4) })
        {
            var period = await GetAsync(client, Route + "period" + Day + basis);
            var detail = await GetAsync(client, Route + "statistics" + Day + basis);
            var analysis = await GetAsync(client, Route + "analysis" + Day + basis);
            Assert.Equal(expected.ToString(), period.GetProperty("counts").GetProperty("knownMatches").GetString());
            Assert.Equal(candidates.ToString(), period.GetProperty("counts").GetProperty("unresolvedCandidates").GetString());
            Assert.Equal(period.GetProperty("counts").GetRawText(), detail.GetProperty("counts").GetRawText());
            Assert.Equal(expected, period.GetProperty("statistics").GetProperty("totalLocations").GetInt32());
            Assert.Equal(expected, detail.GetProperty("statistics").GetProperty("totalLocations").GetInt32());
            Assert.Equal(expected.ToString(), analysis.GetProperty("counts").GetProperty("knownMatches").GetString());
            Assert.Equal(mapped, period.GetProperty("markers").GetArrayLength());
            Assert.Equal("present", period.GetProperty("existence").GetString());
            var folded = period.GetProperty("markers").EnumerateArray().Select(x => x.GetProperty("record"))
                .Where(x => x.GetProperty("locationId").GetInt32() == fold1.Id || x.GetProperty("locationId").GetInt32() == fold2.Id).ToArray();
            Assert.Equal(folded[0].GetProperty("captureTime").GetProperty("local").GetString(), folded[1].GetProperty("captureTime").GetProperty("local").GetString());
            Assert.NotEqual(folded[0].GetProperty("captureTime").GetProperty("offsetSeconds").GetInt32(), folded[1].GetProperty("captureTime").GetProperty("offsetSeconds").GetInt32());
            Assert.All(detail.GetProperty("endpoints").EnumerateArray(), endpoint => Assert.True(endpoint.TryGetProperty("eventUtc", out _)));
        }
        var next = await GetAsync(client, Route + "period" + Day.Replace("day=25", "day=26") + "capture");
        var marker = Assert.Single(next.GetProperty("markers").EnumerateArray()).GetProperty("record");
        Assert.Equal(tokyo.Id, marker.GetProperty("locationId").GetInt32());
        Assert.Equal("Asia/Tokyo", marker.GetProperty("captureTime").GetProperty("zoneId").GetString());
        Assert.Equal("imported-zone", marker.GetProperty("storedTimeZoneId").GetString());
        Assert.Equal("<p>retained notes</p>", marker.GetProperty("notesHtml").GetString());
        var empty = await GetAsync(client, Route + "period" + Day.Replace("day=25", "day=24") + "viewer");
        Assert.Equal("absent", empty.GetProperty("existence").GetString());
    }

    /// <summary>Real Spain/Portugal capture membership retains excluded intermediates and whole mixed/coincident ties.</summary>
    [PostgresFact]
    public async Task SourceEnvelopeRetainsBarriersAndUnplaceableTimeSuppressesClaims()
    {
        Assert.Equal("Europe/Madrid", TimeZoneLookup.GetTimeZone(41.7, -6.0).Result);
        Assert.Equal("Europe/Lisbon", TimeZoneLookup.GetTimeZone(41.7, -7.2).Result);
        var owner = await fixture.CreateUserAsync();
        var rows = new[] { Row(owner.Id,"2026-10-25T22:58Z",-6.0,41.7), Row(owner.Id,"2026-10-25T23:00Z",-6.0,41.7),
            Row(owner.Id,"2026-10-25T23:02Z",-7.2,41.7), Row(owner.Id,"2026-10-25T23:02Z",-7.2,41.7),
            Row(owner.Id,"2026-10-25T23:03Z",-7.201,41.7), Row(owner.Id,"2026-10-25T23:04Z",-6.0,41.7),
            Row(owner.Id,"2026-10-25T23:04Z",-7.2,41.7), Row(owner.Id,"2026-10-25T23:05Z",-7.2,41.7),
            Row(owner.Id,"2026-10-25T23:06Z",-7.201,41.7) };
        await SeedAsync(rows); await ReadyAsync();
        await using var app = await HostAsync(); using var client = Client(app, owner.Id);
        var url = Route + "analysis" + Day + "capture&includeGeometry=true&speedEnabled=false";
        var result = await GetAsync(client, url);
        Assert.Equal("9", result.GetProperty("counts").GetProperty("examinedSourceRows").GetString());
        Assert.Equal("7", result.GetProperty("counts").GetProperty("knownMatches").GetString());
        Assert.Equal("2", result.GetProperty("counts").GetProperty("eligibleEdges").GetString());
        Assert.Equal("complete", result.GetProperty("coverage").GetString());
        var broken = Row(owner.Id,"2026-10-25T23:07Z"); broken.Coordinates = Point.Empty;
        await SeedAsync(broken);
        result = await GetAsync(client, url);
        Assert.Equal("partial", result.GetProperty("coverage").GetString());
        Assert.True(result.GetProperty("distanceMetres").GetDouble() > 0);
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Locations\" SET \"LocalTimestamp\" = '-infinity'::timestamptz WHERE \"Id\" = {broken.Id}");
        await ReadyAsync();
        result = await GetAsync(client, url);
        Assert.Equal("unknown", result.GetProperty("coverage").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("distanceMetres").ValueKind);
        Assert.Equal("unavailable", result.GetProperty("geometry").GetProperty("state").GetString());
    }

    /// <summary>Capture preparation holds visibly while nontext viewer reads remain independent; cookie/bearer/role boundaries stay strict.</summary>
    [PostgresFact]
    public async Task ReadinessAndOwnerAuthorityRemainIndependent()
    {
        var owner = await fixture.CreateUserAsync(); var other = await fixture.CreateUserAsync();
        var row = Row(owner.Id, "2026-10-25T12:00Z"); await SeedAsync(row, Row(other.Id,"2026-10-25T12:00Z"));
        await using (var db = fixture.CreateContext())
            await db.Locations.Where(x => x.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.HistoryTimeVersion, "prior-generation"));
        await using var app = await HostAsync(); using var client = Client(app, owner.Id);
        await ErrorAsync(client, Route + "analysis" + Day + "capture", 503, "historyPreparationRequired");
        var viewer = await GetAsync(client, Route + "analysis" + Day + "viewer");
        Assert.Equal("1", viewer.GetProperty("counts").GetProperty("knownMatches").GetString());
        await ErrorAsync(client, Route + "analysis" + Day + "viewer&owner=" + other.Id, 400, "parametersInvalid");
        using var anonymous = app.GetTestClient(); anonymous.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        await ErrorAsync(anonymous, Route + "analysis" + Day + "viewer", 401);
        using var wrongRole = Client(app, owner.Id, "Manager");
        await ErrorAsync(wrongRole, Route + "analysis" + Day + "viewer", 403);
        await ErrorAsync(client, "/api/location/history?timeBasis=utc&timeMode=interval&viewerZone=UTC&startUtc=2026-01-01T00:00:00Z&endUtc=2027-01-01T00:00:00Z", 401);
    }

    /// <summary>Read-only repeatable-read publication stays coherent across a controlled concurrent delete and event edit.</summary>
    [PostgresFact]
    public async Task PeriodSnapshotSurvivesConcurrentEditAndDelete()
    {
        var owner = await fixture.CreateUserAsync(); var a = Row(owner.Id,"2026-10-25T10:00Z"); var b = Row(owner.Id,"2026-10-25T11:00Z");
        await SeedAsync(a,b); await ReadyAsync();
        var gate = new ReadGate(); await using var app = await HostAsync(gate); using var client = Client(app, owner.Id);
        var read = GetAsync(client, Route + "period" + Day + "capture");
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var sourceGate = new ReadGate(); await using var sourceApp = await HostAsync(sourceGate);
        using var sourceClient = Client(sourceApp, owner.Id);
        var sourceRead = GetAsync(sourceClient, Route + "analysis" + Day + "capture");
        await sourceGate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var edit = fixture.CreateContext())
        {
            await edit.Locations.Where(x => x.Id == a.Id).ExecuteDeleteAsync();
            var moved = await edit.Locations.SingleAsync(x => x.Id == b.Id);
            moved.LocalTimestamp = moved.LocalTimestamp.AddDays(-1); await edit.SaveChangesAsync();
        }
        gate.Release.TrySetResult(); sourceGate.Release.TrySetResult();
        var source = await sourceRead;
        Assert.Equal("2", source.GetProperty("counts").GetProperty("knownMatches").GetString());
        Assert.Equal("1", source.GetProperty("counts").GetProperty("eligibleEdges").GetString());
        var result = await read;
        Assert.Equal(2, result.GetProperty("markers").GetArrayLength());
        Assert.Equal(2, result.GetProperty("statistics").GetProperty("totalLocations").GetInt32());
        var fresh = await GetAsync(client, Route + "period" + Day + "capture");
        Assert.Empty(fresh.GetProperty("markers").EnumerateArray());
    }

    /// <summary>Actual competing reads use the bearer pool, release on disconnect, and never publish a timeout prefix.</summary>
    [PostgresFact]
    public async Task SharedAdmissionCancellationAndDeadlineAreReal()
    {
        var owner = await fixture.CreateUserAsync(); await SeedAsync(Row(owner.Id,"2026-10-25T12:00Z"));
        var gate = new ReadGate(); await using var app = await HostAsync(gate); using var client = Client(app, owner.Id);
        using var cancel = new CancellationTokenSource();
        var pending = client.GetAsync(Route + "analysis" + Day + "viewer", cancel.Token);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var competing = Stopwatch.StartNew();
        await GetAsync(client, Route + "period" + Day + "viewer");
        output.WriteLine($"Competing routed period completed in {competing.Elapsed.TotalMilliseconds:F2} ms while analysis held a permit/snapshot.");
        using (var second = LocationHistoryController.Collections.TryAcquire(owner.Id, out _))
        {
            Assert.NotNull(second);
            await ErrorAsync(client, Route + "period" + Day + "viewer", 429, "historyOwnerAdmissionExceeded");
        }
        var clock = Stopwatch.StartNew(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        output.WriteLine($"Disconnect observed after {clock.Elapsed.TotalMilliseconds:F2} ms; shared owner admission returned 429.");
        gate.Release.TrySetResult();
        await GetAsync(client, Route + "analysis" + Day + "viewer");
        var delay = new ReadGate(true); await using var slowApp = await HostAsync(delay); using var slow = Client(slowApp, owner.Id);
        await ErrorAsync(slow, Route + "analysis" + Day + "viewer", 503, "historyBudgetExceeded");
    }

    /// <summary>Routed 20,100-row capture/viewer observations cover sampling, summary-only allocation and faithful geometry overflow.</summary>
    [PostgresFact]
    public async Task UnsampledAnalysisAndMeasuredGeometryBudgets()
    {
        var owner = await fixture.CreateUserAsync();
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO "Locations" ("UserId","Timestamp","LocalTimestamp","TimeZoneId","Coordinates","Country","Region","Place")
                SELECT {{owner.Id}},'2026-10-24Z'::timestamptz,'2026-10-24Z'::timestamptz+
                    CASE WHEN n <= 20000 THEN n*interval '1 second' ELSE interval '1 day'+(n-20000)*interval '1 second' END,'UTC',
                    ST_SetSRID(ST_MakePoint(23.73+(n%100)*0.00001,37.98),4326)::geography,'Greece','Attica','Athens'
                FROM generate_series(1,20100) n
                """);
        await ReadyAsync();
        await using var app = await HostAsync(); using var client = Client(app, owner.Id);
        foreach (var basis in new[] { "capture", "viewer" })
        {
            var query = Day.Replace("dateType=day", "dateType=month") + basis;
            var periodClock = Stopwatch.StartNew();
            var period = await GetAsync(client, Route + "period" + query);
            output.WriteLine(JsonSerializer.Serialize(new { basis, operation = "period", elapsedMs = periodClock.Elapsed.TotalMilliseconds,
                responseBytes = System.Text.Encoding.UTF8.GetByteCount(period.GetRawText()) }));
            Assert.True(period.GetProperty("markersSampled").GetBoolean());
            Assert.True(period.GetProperty("markers").GetArrayLength() <= 3000);
            foreach (var geometry in new[] { false, true })
            {
                var before = GC.GetTotalAllocatedBytes(); var clock = Stopwatch.StartNew();
                var result = await GetAsync(client, Route + "analysis" + query + "&includeGeometry=" + geometry);
                output.WriteLine(JsonSerializer.Serialize(new { basis, geometry, rows = 20100, elapsedMs = clock.Elapsed.TotalMilliseconds,
                    processAllocatedBytes = GC.GetTotalAllocatedBytes() - before, responseBytes = System.Text.Encoding.UTF8.GetByteCount(result.GetRawText()) }));
                Assert.Equal("20100", result.GetProperty("counts").GetProperty("examinedSourceRows").GetString());
                Assert.Equal("20098", result.GetProperty("counts").GetProperty("eligibleEdges").GetString());
                Assert.Equal("complete", result.GetProperty("coverage").GetString());
                Assert.True(result.GetProperty("distanceMetres").GetDouble() > 0);
                Assert.Equal(geometry ? "unavailable" : "not-requested", result.GetProperty("geometry").GetProperty("state").GetString());
            }
            var drawable = await GetAsync(client, Route + "analysis" + Day.Replace("day=25", "day=24") + basis + "&includeGeometry=true");
            Assert.Equal("available", drawable.GetProperty("geometry").GetProperty("state").GetString());
            Assert.Equal(20000, drawable.GetProperty("geometry").GetProperty("islands")[0].GetArrayLength());
            output.WriteLine($"Faithful 20,000-vertex {basis} response: {System.Text.Encoding.UTF8.GetByteCount(drawable.GetRawText())} bytes.");
            await MeasurePhasesAsync(owner.Id, query);
            await PlanAsync(owner.Id, query);
        }
        await ObserveW2CostAsync(owner.Id);
    }

    /// <summary>Separate admitted-service work from complete serialization using the same reusable relational fixture.</summary>
    private async Task MeasurePhasesAsync(string owner, string query)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext(); context.Request.QueryString = new(query);
        var scope = TimelineCalendarScope.Parse(context.Request.Query, false, SystemClock.Instance.GetCurrentInstant());
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
        var before = GC.GetTotalAllocatedBytes(); var clock = Stopwatch.StartNew();
        await new HistoryReadService(db, new HistoryCursor(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()))
            .PrepareQueryAsync(owner, scope.Query, default);
        var readyMs = clock.Elapsed.TotalMilliseconds; clock.Restart();
        var result = await new TimelineCalendarReadService(db).AnalysisAsync(owner, scope, new(7200,350), true, DateTime.UtcNow, default);
        var evaluationMs = clock.Elapsed.TotalMilliseconds; clock.Restart();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result);
        output.WriteLine(JsonSerializer.Serialize(new { basis = scope.Query.Scope.TimeBasis, readyMs, evaluationIncludingReadAndReductionMs = evaluationMs,
            serializationMs = clock.Elapsed.TotalMilliseconds, bytes = bytes.Length,
            processAllocatedBytes = GC.GetTotalAllocatedBytes() - before, processWorkingSetBytes = Environment.WorkingSet }));
    }

    private async Task PlanAsync(string owner, string query)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext(); context.Request.QueryString = new(query);
        var scope = TimelineCalendarScope.Parse(context.Request.Query, false, SystemClock.Instance.GetCurrentInstant());
        var predicate = new HistoryPredicate(owner, scope.Query, default);
        await using var db = fixture.CreateContext(); await db.Database.OpenConnectionAsync();
        await using var command = ((NpgsqlConnection)db.Database.GetDbConnection()).CreateCommand();
        command.CommandText = "EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) " + TimelineCalendarReadService.SourceSql(predicate);
        command.Parameters.AddRange(predicate.CopyParameters().Cast<NpgsqlParameter>().ToArray());
        output.WriteLine("Natural source plan " + scope.Query.Scope.TimeBasis + ": " + (string)(await command.ExecuteScalarAsync())!);
    }

    private async Task<WebApplication> HostAsync(params IInterceptor[] interceptors)
    {
        await using var db = fixture.CreateContext();
        return await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.RemoveAll<ApplicationDbContext>(); services.AddScoped(_ => fixture.CreateContext(interceptors));
        });
    }
    private static HttpClient Client(WebApplication app, string owner, string role = "User")
    {
        var client = app.GetTestClient(); client.DefaultRequestHeaders.Add("Cookie", IdentityRouteHost.Cookie(app.Services, owner, role));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json"); return client;
    }
    private async Task SeedAsync(params Location[] rows)
    { await using var db = fixture.CreateContext(); db.AddRange(rows); await db.SaveChangesAsync(); }
    private async Task ReadyAsync()
    {
        await using var db = fixture.CreateContext(); var prepare = new LocationHistoryPreparation(db);
        await prepare.BeginAsync("time", false, default); while (!await prepare.PrepareBatchAsync("time", default)) { }
    }
    private static Location Row(string owner, string instant, double longitude = 23.73, double latitude = 37.98, string country = "Greece") => new()
    {
        UserId = owner, TimeZoneId = "imported-zone", Coordinates = new(longitude, latitude) { SRID = 4326 },
        LocalTimestamp = DateTime.Parse(instant, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
        Timestamp = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), Notes = "<p>retained notes</p>", Country = country, Region = "region", Place = "city"
    };
    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url); var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var document = JsonDocument.Parse(body); return document.RootElement.Clone();
    }
    private static async Task ErrorAsync(HttpClient client, string url, int status, string? code = null)
    {
        using var response = await client.GetAsync(url); var body = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == status, $"{response.StatusCode}: {body}");
        Assert.True(response.Headers.CacheControl?.NoStore);
        if (code != null) Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    /// <summary>Hold a production uncertainty/count command inside its actual snapshot; optional SQL delay tests the real deadline.</summary>
    private sealed class ReadGate(bool sqlDelay = false) : DbCommandInterceptor
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Reached.Task.IsCompleted && (command.CommandText.Contains("SELECT count(*) FILTER") || command.CommandText.Contains("SELECT EXISTS (SELECT FROM \"Locations\" l")))
            {
                Reached.TrySetResult();
                if (sqlDelay) command.CommandText = "DO $$ BEGIN PERFORM pg_sleep(20); END $$; " + command.CommandText;
                else
                {
                    try { await Release.Task.WaitAsync(cancellationToken); }
                    catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
                }
            }
            return result;
        }
    }
}
