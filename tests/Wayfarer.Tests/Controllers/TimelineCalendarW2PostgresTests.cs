using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NodaTime;
using Npgsql;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Accepted W2 private body reads and full-source membership at the actual routed storage seam.</summary>
public sealed partial class TimelineCalendarPostgresTests
{
    private static readonly Dictionary<string, string> W2Day = new()
    {
        ["dateType"] = "day", ["year"] = "2026", ["month"] = "10", ["day"] = "25",
        ["timeBasis"] = "viewer", ["viewerZone"] = "Europe/Athens"
    };

    /// <summary>Reliable text exclusions and mixed coincident ties remain barriers; unplaceable exclusions suppress claims.</summary>
    [PostgresFact]
    public async Task FilteredPostSourceRetainsAllOwnerBarriers()
    {
        var owner = await fixture.CreateUserAsync(); var other = await fixture.CreateUserAsync();
        var a = Row(owner.Id, "2026-10-25T10:00Z"); var b = Row(owner.Id, "2026-10-25T10:01Z");
        var c = Row(owner.Id, "2026-10-25T10:02Z");
        a.Notes = c.Notes = "blue door"; b.Notes = "excluded";
        await SeedAsync(a, b, c, Row(other.Id, "2026-10-25T10:03Z")); await ReadyTextAsync();
        await using var app = await HostAsync(); using var client = Client(app, owner.Id); await PostTokenAsync(client);
        var values = new Dictionary<string, string>(W2Day) { ["q"] = "blue door", ["includeGeometry"] = "true" };
        var result = await PostAsync(client, "analysis", values);
        Assert.Equal("private-calendar/2", result.GetProperty("calendarContract").GetString());
        Assert.Equal("3", result.GetProperty("counts").GetProperty("examinedSourceRows").GetString());
        Assert.Equal("2", result.GetProperty("counts").GetProperty("knownMatches").GetString());
        Assert.Equal("0", result.GetProperty("counts").GetProperty("eligibleEdges").GetString());
        await using (var db = fixture.CreateContext())
        {
            var middle = await db.Locations.SingleAsync(x => x.Id == b.Id);
            middle.LocalTimestamp = c.LocalTimestamp; await db.SaveChangesAsync();
        }
        result = await PostAsync(client, "analysis", values);
        Assert.Equal("0", result.GetProperty("counts").GetProperty("eligibleEdges").GetString());
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Locations\" SET \"LocalTimestamp\" = '-infinity'::timestamptz WHERE \"Id\" = {b.Id}");
        result = await PostAsync(client, "analysis", values);
        Assert.Equal("unknown", result.GetProperty("coverage").GetString());
        Assert.Equal("unavailable", result.GetProperty("geometry").GetProperty("state").GetString());
    }

    /// <summary>Hostile literal text stays owner-scoped across records, detailed statistics, month membership and analysis.</summary>
    [PostgresFact]
    public async Task RecordsAndAvailabilityUsePrivateFilteredScope()
    {
        var owner = await fixture.CreateUserAsync(); var other = await fixture.CreateUserAsync();
        const string literal = "literal_%' OR 1=1 --";
        var first = Row(owner.Id, "2026-10-25T10:00Z"); first.Notes = literal + " blue";
        var unmapped = Row(owner.Id, "2026-10-25T11:00Z"); unmapped.Notes = first.Notes; unmapped.Coordinates = NetTopologySuite.Geometries.Point.Empty;
        var outside = Row(owner.Id, "2026-10-26T10:00Z"); outside.Notes = first.Notes;
        var foreign = Row(other.Id, "2026-10-25T10:00Z"); foreign.Notes = first.Notes;
        await SeedAsync(first, unmapped, outside, foreign); await ReadyTextAsync();
        await using var app = await HostAsync(); using var client = Client(app, owner.Id); await PostTokenAsync(client);
        var values = new Dictionary<string, string>(W2Day) { ["q"] = literal, ["pageSize"] = "1" };
        var page = await PostAsync(client, "records", values);
        Assert.Equal(unmapped.Id, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("locationId").GetInt32());
        Assert.Equal("live-request", page.GetProperty("consistency").GetString());
        values["continuation"] = page.GetProperty("nextContinuation").GetString()!;
        page = await PostAsync(client, "records", values);
        Assert.Equal(first.Id, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("locationId").GetInt32());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextContinuation").ValueKind);
        values["q"] = "other";
        await PostAsync(client, "records", values, 400, "continuationInvalid");
        // Exercise all three aggregate call sites using the same hostile value and selected owner/window.
        var detail = await PostAsync(client, "statistics", new Dictionary<string, string>(W2Day) { ["q"] = literal });
        Assert.Equal(2, detail.GetProperty("statistics").GetProperty("totalLocations").GetInt32());
        Assert.Equal(2, Assert.Single(detail.GetProperty("statistics").GetProperty("countries").EnumerateArray()).GetProperty("visitCount").GetInt32());
        var month = new Dictionary<string, string>(W2Day); month.Remove("dateType"); month.Remove("day"); month["q"] = literal;
        var marks = await PostAsync(client, "availability", month);
        Assert.Equal(new[] { "2026-10-25", "2026-10-26" }, marks.GetProperty("presentDates").EnumerateArray().Select(x => x.GetString()));
        Assert.False(marks.GetProperty("hasUnresolvedCandidates").GetBoolean());
        month["timeBasis"] = "capture";
        marks = await PostAsync(client, "availability", month);
        Assert.True(marks.GetProperty("hasUnresolvedCandidates").GetBoolean());
        var clock = Stopwatch.StartNew();
        var analysis = await PostAsync(client, "analysis", new Dictionary<string, string>(W2Day) { ["q"] = literal });
        Assert.Equal("3", analysis.GetProperty("counts").GetProperty("examinedSourceRows").GetString());
        Assert.Equal("2", analysis.GetProperty("counts").GetProperty("knownMatches").GetString());
        output.WriteLine($"Filtered routed analysis: {clock.Elapsed.TotalMilliseconds:F2} ms; three owner source rows, independent snapshots.");
        using var noToken = Client(app, owner.Id);
        await PostAsync(noToken, "records", W2Day, 400);
        await PostAsync(client, "period?q=private", W2Day, 400, "parametersInvalid");
    }

    /// <summary>Catalog redirects canonicalize before cursor binding; unknown area remains a barrier and text readiness holds.</summary>
    [PostgresFact]
    public async Task ActivityRedirectsAreaCandidatesAndTextReadinessRemainExplicit()
    {
        var owner = await fixture.CreateUserAsync();
        int activity;
        await using (var db = fixture.CreateContext())
        {
            var type = new Wayfarer.Models.ActivityType { Name = "W2 retained activity", Description = "W2 fixture" };
            db.Add(type); await db.SaveChangesAsync(); activity = type.Id;
            db.Add(new Wayfarer.Models.ActivityTypeIdRedirect { LegacyId = 2000000000, ActivityTypeId = activity }); await db.SaveChangesAsync();
        }
        var a = Row(owner.Id, "2026-10-25T10:00Z"); var b = Row(owner.Id, "2026-10-25T10:01Z"); var c = Row(owner.Id, "2026-10-25T10:02Z");
        a.ActivityTypeId = c.ActivityTypeId = activity; b.Coordinates = NetTopologySuite.Geometries.Point.Empty;
        await SeedAsync(a,b,c); await ReadyTextAsync();
        await using var app = await HostAsync(); using var client = Client(app, owner.Id); await PostTokenAsync(client);
        var values = new Dictionary<string, string>(W2Day) { ["activityIds"] = "2000000000", ["includeGeometry"] = "true" };
        var result = await PostAsync(client, "analysis", values);
        Assert.Equal(activity, result.GetProperty("scope").GetProperty("activityIds")[0].GetInt32());
        Assert.Equal("3", result.GetProperty("counts").GetProperty("examinedSourceRows").GetString());
        Assert.Equal("0", result.GetProperty("counts").GetProperty("eligibleEdges").GetString());
        values["activityIds"] = "1999999999"; await PostAsync(client, "analysis", values, 400, "activityInvalid");
        values.Remove("activityIds"); values["west"] = "23"; values["east"] = "24"; values["south"] = "37"; values["north"] = "39";
        result = await PostAsync(client, "analysis", values);
        Assert.Equal("partial", result.GetProperty("coverage").GetString());
        Assert.Equal("1", result.GetProperty("counts").GetProperty("unresolvedPlaceableCandidates").GetString());
        await using (var db = fixture.CreateContext())
            await db.Locations.Where(x => x.Id == a.Id).ExecuteUpdateAsync(change => change.SetProperty(x => x.HistoryTextVersion, "prior-generation"));
        await PostAsync(client, "period", W2Day);
        await PostAsync(client, "period", new Dictionary<string, string>(W2Day) { ["q"] = "retained" }, 503, "historyPreparationRequired");
        await PostAsync(client, "period", new Dictionary<string, string>(W2Day) { ["q"] = "   " }, 503, "historyPreparationRequired");
    }

    /// <summary>Actual MVC/EF route diagnostics omit private form values on successful and rejected reads.</summary>
    [PostgresFact]
    public async Task PrivateBodiesAndScopesDoNotEnterRouteLogs()
    {
        const string privateValue = "W2-private-body-sentinel-73c318";
        var owner = await fixture.CreateUserAsync(); await SeedAsync(Row(owner.Id,"2026-10-25T10:00Z")); await ReadyTextAsync();
        var logs = new CalendarLogs(); await using var seed = fixture.CreateContext();
        await using var app = await IdentityRouteHost.StartAsync(seed, CreateTestDirectory(), services =>
        {
            services.RemoveAll<Wayfarer.Models.ApplicationDbContext>(); services.AddScoped(_ => fixture.CreateContext());
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        });
        using var client = Client(app, owner.Id); await PostTokenAsync(client);
        await PostAsync(client, "period", new Dictionary<string,string>(W2Day) { ["q"] = privateValue });
        await PostAsync(client, "analysis", new Dictionary<string,string>(W2Day) { ["q"] = privateValue, ["west"] = "bad-bound" }, 400, "parametersInvalid");
        Assert.NotEmpty(logs.Messages);
        Assert.DoesNotContain(logs.Messages, message => message.Contains(privateValue) || message.Contains("bad-bound") || message.Contains("\"q\":") || message.Contains("\"scope\":"));
    }

    /// <summary>Retain formatted diagnostics only; no test changes production logging policy.</summary>
    private sealed class CalendarLogs : ILoggerProvider
    {
        internal System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CalendarLogger(Messages);
        public void Dispose() { }
        private sealed class CalendarLogger(System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, error));
        }
    }

    /// <summary>One existing 20,100-row fixture observes filtered analysis, compact paging and month aggregation without a new benchmark harness.</summary>
    private async Task ObserveW2CostAsync(string owner)
    {
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Locations\" SET \"Notes\" = CASE WHEN \"Id\" % 1000 = 0 THEN 'W2 cost phrase' ELSE 'excluded fixture' END WHERE \"UserId\" = {owner}");
        await ReadyTextAsync();
        var observer = new CalendarCommands(); await using var app = await HostAsync(observer); using var client = Client(app, owner); await PostTokenAsync(client);
        var values = new Dictionary<string, string>(W2Day) { ["dateType"] = "month", ["q"] = "W2 cost phrase" };
        foreach (var operation in new[] { "analysis", "records", "availability" })
        {
            var body = new Dictionary<string, string>(values);
            if (operation == "availability") { body.Remove("dateType"); body.Remove("day"); }
            var before = GC.GetTotalAllocatedBytes(); var clock = Stopwatch.StartNew();
            var response = await PostAsync(client, operation, body);
            output.WriteLine(JsonSerializer.Serialize(new { operation, filteredFixtureRows = 20100, elapsedMs = clock.Elapsed.TotalMilliseconds,
                processAllocatedBytes = GC.GetTotalAllocatedBytes() - before, responseBytes = System.Text.Encoding.UTF8.GetByteCount(response.GetRawText()) }));
            if (operation == "analysis") Assert.Equal("20100", response.GetProperty("counts").GetProperty("examinedSourceRows").GetString());
        }
        var context = new DefaultHttpContext(); context.Request.QueryString = new("?" + string.Join('&', values.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value))));
        var scope = TimelineCalendarScope.ParsePost(context.Request.Query, "analysis", SystemClock.Instance.GetCurrentInstant());
        var predicate = new HistoryPredicate(owner, scope.Query, default);
        observer.Queries.Add((TimelineCalendarReadService.SourceSql(predicate), predicate.CopyParameters().Cast<NpgsqlParameter>().ToArray()));
        await using var planDb = fixture.CreateContext(); await planDb.Database.OpenConnectionAsync();
        foreach (var (sql, parameters) in observer.Queries)
        {
            await using var command = ((NpgsqlConnection)planDb.Database.GetDbConnection()).CreateCommand();
            command.CommandText = "EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) " + sql;
            command.Parameters.AddRange(parameters);
            // Synthetic fixture values are retained here; production logging never records private body/parameter values.
            output.WriteLine("W2 observed SQL: " + sql);
            output.WriteLine("W2 synthetic parameters: " + JsonSerializer.Serialize(parameters.Select(p => new { p.ParameterName, p.NpgsqlDbType, p.Value })));
            output.WriteLine("W2 natural plan: " + (string)(await command.ExecuteScalarAsync())!);
        }
    }

    /// <summary>Capture actual compact-record/month commands only, with cloned typed parameters for the one fixture plan observation.</summary>
    private sealed class CalendarCommands : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        internal List<(string Sql, NpgsqlParameter[] Parameters)> Queries { get; } = [];
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData data,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken token = default)
        {
            if (command.CommandText.Contains("AS \"Uuid\"") || command.CommandText.Contains("SELECT DISTINCT"))
                Queries.Add((command.CommandText, command.Parameters.Cast<NpgsqlParameter>().Select(p => (NpgsqlParameter)((ICloneable)p).Clone()).ToArray()));
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Use established owner-wide text preparation, including the current catalog generation prerequisite.</summary>
    private async Task ReadyTextAsync()
    {
        await ReadyAsync();
        await using var db = fixture.CreateContext(); var prepare = new LocationHistoryPreparation(db);
        await prepare.BeginAsync("text", false, default); while (!await prepare.PrepareBatchAsync("text", default)) { }
    }

    /// <summary>Retain the Identity cookie while acquiring real framework antiforgery evidence.</summary>
    private static async Task PostTokenAsync(HttpClient client)
    {
        var cookie = client.DefaultRequestHeaders.GetValues("Cookie").Single();
        var token = await IdentityRouteHost.AntiforgeryAsync(client);
        var antiforgery = client.DefaultRequestHeaders.GetValues("Cookie").Single();
        client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Add("Cookie", cookie + "; " + antiforgery);
        client.DefaultRequestHeaders.Add("RequestVerificationToken", token);
    }

    /// <summary>Fixed routed assertions preserve controlled errors and no-store without logging private request values.</summary>
    private static async Task<JsonElement> PostAsync(HttpClient client, string operation, Dictionary<string, string> values,
        int status = 200, string? code = null)
    {
        using var response = await client.PostAsync(Route + operation, new FormUrlEncodedContent(values));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == status, $"{response.StatusCode}: {body}");
        if (body.Length == 0) return default;
        using var document = JsonDocument.Parse(body); var result = document.RootElement.Clone();
        if (code != null) Assert.Equal(code, result.GetProperty("code").GetString());
        Assert.True(response.Headers.CacheControl?.NoStore);
        return result;
    }
}
