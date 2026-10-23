using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Point = NetTopologySuite.Geometries.Point;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Middleware;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Actual routed PostgreSQL owner reads, snapshots, paging, preparation and geographic coverage.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationHistoryHttpPostgresTests(PostgresMigrationTestFixture fixture) : TestBase, IClassFixture<PostgresMigrationTestFixture>
{
    private const string Route = "/api/location/history";
    private const string Utc = "timeBasis=utc&timeMode=interval&viewerZone=Europe%2FAthens&startUtc=2025-01-01T00:00:00Z&endUtc=2026-01-01T00:00:00Z";
    private const string Capture = "timeBasis=capture&timeMode=days&viewerZone=UTC&fromDate=2025-01-01&throughDate=2025-12-31";

    /// <summary>Equal-time ID order, deleted anchors, live inserts/moves, changed controls and foreign cursors.</summary>
    [PostgresFact]
    public async Task PagesUseProtectedValuesAndDocumentedLiveMembership()
    {
        var owner = await OwnerAsync(); var other = await OwnerAsync();
        var rows = await SeedAsync(owner, 4);
        await using var app = await HostAsync();
        using var client = Client(app, owner);
        var first = await GetAsync(client, Route + "?" + Utc + "&pageSize=2");
        Assert.Equal(new[] { rows[3].Id, rows[2].Id }, Ids(first));
        var cursor = first.GetProperty("nextContinuation").GetString()!;
        await using (var edit = fixture.CreateContext())
        {
            await edit.Locations.Where(row => row.Id == rows[2].Id).ExecuteDeleteAsync();
            // Newly inserted equal-time IDs above the anchor need a beginning refresh.
            edit.Add(Row(owner)); await edit.SaveChangesAsync();
        }
        var second = await GetAsync(client, Route + "?" + Utc + "&pageSize=2&continuation=" + Uri.EscapeDataString(cursor));
        Assert.Equal(new[] { rows[1].Id, rows[0].Id }, Ids(second));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextContinuation").ValueKind);
        Assert.Equal(first.GetProperty("scopeKey").GetString(), second.GetProperty("scopeKey").GetString());
        foreach (var suffix in new[] { "&pageSize=1", "&pageSize=2&q=changed", "&pageSize=2&resultSet=unresolvedCandidates" })
            await ErrorAsync(client, Route + "?" + Utc + suffix + "&continuation=" + Uri.EscapeDataString(cursor), 400, "continuationInvalid");
        using var foreign = Client(app, other);
        await ErrorAsync(foreign, Route + "?" + Utc + "&pageSize=2&continuation=" + Uri.EscapeDataString(cursor), 400, "continuationInvalid");
        await ErrorAsync(client, Route + "?" + Utc + "&pageSize=2&continuation=tampered", 400, "continuationInvalid");
        await using (var edit = fixture.CreateContext())
        {
            // A seen event moved below the cursor may repeat on a later live page.
            var moved = await edit.Locations.SingleAsync(row => row.Id == rows[3].Id);
            moved.LocalTimestamp = moved.LocalTimestamp.AddDays(-1); await edit.SaveChangesAsync();
        }
        var later = await GetAsync(client, Route + "?" + Utc + "&pageSize=2&continuation=" + Uri.EscapeDataString(cursor));
        var last = await GetAsync(client, Route + "?" + Utc + "&pageSize=2&continuation=" + Uri.EscapeDataString(later.GetProperty("nextContinuation").GetString()!));
        Assert.Equal(rows[3].Id, Assert.Single(Ids(last)));
    }

    /// <summary>Real finite year-0001 midnight anchors must not become Npgsql's minus-infinity sentinel.</summary>
    [PostgresFact]
    public async Task FiniteMinimumInstantKeepsEqualTimeCursorTies()
    {
        var owner = await OwnerAsync(); var rows = await SeedAsync(owner, 3);
        await using (var edit = fixture.CreateContext())
            await edit.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE "Locations" SET "LocalTimestamp" = '0001-01-01 00:00:00+00'::timestamptz,
                    "HistoryTimeVersion" = 'legacy-generation' WHERE "UserId" = {{owner}}
                """);
        await using var app = await HostAsync(); using var client = Client(app, owner);
        var query = "?timeBasis=utc&timeMode=interval&viewerZone=UTC&startUtc=0001-01-01T00:00:00Z&endUtc=0001-01-02T00:00:00Z&pageSize=1";
        string? cursor = null;
        foreach (var expected in rows.Reverse())
        {
            var page = await GetAsync(client, Route + query + (cursor == null ? "" : "&continuation=" + Uri.EscapeDataString(cursor)));
            Assert.Equal(expected.Id, Assert.Single(Ids(page)));
            Assert.Equal("0001-01-01T00:00:00.000000Z", page.GetProperty("items")[0].GetProperty("eventUtc").GetString());
            cursor = page.GetProperty("nextContinuation").GetString();
        }
        Assert.Null(cursor);
    }

    /// <summary>Dense, distant and coincident points survive aggregation; map choices work before any chronology read.</summary>
    [PostgresFact]
    public async Task IndependentOverviewRetainsDistantAndCoincidentRecords()
    {
        var owner = await OwnerAsync(); var rows = await SeedAsync(owner, 5);
        await using (var edit = fixture.CreateContext())
        {
            var distant = await edit.Locations.SingleAsync(row => row.Id == rows[4].Id);
            distant.Coordinates = new Point(-74, 40.7) { SRID = 4326 }; await edit.SaveChangesAsync();
        }
        await using var app = await HostAsync(); using var client = Client(app, owner);
        var overview = await GetAsync(client, Route + "/overview?" + Utc + "&grid=4");
        Assert.Equal("5", overview.GetProperty("knownMatchCount").GetString());
        Assert.Equal("0", overview.GetProperty("unmappedMatchCount").GetString());
        var cells = overview.GetProperty("cells").EnumerateArray().ToArray(); Assert.Equal(2, cells.Length);
        var dense = Assert.Single(cells, cell => cell.GetProperty("recordCount").GetString() == "4");
        Assert.Equal(JsonValueKind.Null, dense.GetProperty("singleton").ValueKind);
        var sparse = Assert.Single(cells, cell => cell.GetProperty("recordCount").GetString() == "1");
        Assert.Equal(rows[4].Id, sparse.GetProperty("singleton").GetProperty("locationId").GetInt32());
        var bounds = dense.GetProperty("bounds");
        var area = string.Join("&", new[] { "west", "south", "east", "north" }.Select(key => key + "=" + bounds.GetProperty(key).GetRawText()));
        var choices = await GetAsync(client, Route + "?" + Utc + "&" + area + "&pageSize=2");
        Assert.Equal(2, Ids(choices).Length);
        Assert.NotNull(choices.GetProperty("nextContinuation").GetString());
        var detail = await GetAsync(client, Route + "/" + Ids(choices)[0] + "?viewerZone=UTC");
        Assert.Equal("not-evaluated", detail.GetProperty("filterMembership").GetString());
        Assert.Equal("owner-detail-1", detail.GetProperty("profile").GetString());
    }

    /// <summary>Uncertainty is conserved across whole-scope counts, candidate pages, overview and complete direct detail.</summary>
    [PostgresFact]
    public async Task CandidateCoverageAndPendingPreparationAreDifferent()
    {
        var owner = await OwnerAsync(); var rows = await SeedAsync(owner, 4);
        await using (var edit = fixture.CreateContext())
        {
            var invalid = await edit.Locations.Where(row => row.Id == rows[2].Id || row.Id == rows[3].Id).ToArrayAsync();
            foreach (var row in invalid) row.Coordinates = new Point(double.NaN, double.NaN) { SRID = 4326 };
            await edit.SaveChangesAsync(); await ReadyAsync(edit);
        }
        await using var app = await HostAsync(); using var client = Client(app, owner);
        var list = await GetAsync(client, Route + "?" + Capture);
        Assert.Equal(2, Ids(list).Length); Coverage(list, "2");
        var candidates = await GetAsync(client, Route + "?" + Capture + "&resultSet=unresolvedCandidates&pageSize=1");
        Coverage(candidates, "2");
        Assert.Equal(rows[3].Id, Assert.Single(Ids(candidates)));
        Assert.Equal("undetermined", candidates.GetProperty("items")[0].GetProperty("filterMembership").GetString());
        var next = await GetAsync(client, Route + "?" + Capture + "&resultSet=unresolvedCandidates&pageSize=1&continuation=" + Uri.EscapeDataString(candidates.GetProperty("nextContinuation").GetString()!));
        Coverage(next, "2"); Assert.Equal(rows[2].Id, Assert.Single(Ids(next)));
        var overview = await GetAsync(client, Route + "/overview?" + Capture); Coverage(overview, "2");
        Assert.Equal("2", overview.GetProperty("knownMatchCount").GetString());
        var utcOverview = await GetAsync(client, Route + "/overview?" + Utc);
        Assert.Equal("4", utcOverview.GetProperty("knownMatchCount").GetString());
        Assert.Equal("2", utcOverview.GetProperty("unmappedMatchCount").GetString());
        Assert.Equal("complete", utcOverview.GetProperty("coverage").GetProperty("membership").GetString());
        var area = "&west=139&south=35&east=140&north=36";
        var utcArea = await GetAsync(client, Route + "/overview?" + Utc + area); Coverage(utcArea, "2");
        var detail = await GetAsync(client, Route + "/" + rows[3].Id + "?viewerZone=UTC");
        Assert.Equal("unavailable", detail.GetProperty("record").GetProperty("captureTime").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("record").GetProperty("appBuild").ValueKind);
        Assert.Equal("<p>café station</p>", detail.GetProperty("record").GetProperty("notesHtml").GetString());
        Assert.Equal(rows[3].IdempotencyKey!.Value.ToString("D"), detail.GetProperty("record").GetProperty("uuid").GetString());
        await using (var edit = fixture.CreateContext())
        {
            await edit.Locations.Where(row => row.Id == rows[3].Id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LocalTimestamp, DateTime.MinValue));
        }
        var unknownEvent = await GetAsync(client, Route + "?" + Capture + "&resultSet=unresolvedCandidates&pageSize=1");
        Assert.Equal("unavailable", unknownEvent.GetProperty("items")[0].GetProperty("eventUtc").GetProperty("status").GetString());
        var unavailableDetail = await GetAsync(client, Route + "/" + rows[3].Id + "?viewerZone=UTC");
        Assert.False(unavailableDetail.GetProperty("record").TryGetProperty("eventUtc", out _));
        Assert.True(unavailableDetail.GetProperty("unavailableFields").TryGetProperty("eventUtc", out _));
        await using (var edit = fixture.CreateContext())
        {
            await new LocationHistoryPreparation(edit).BeginAsync("time", true, default);
            await new LocationHistoryPreparation(edit).BeginAsync("text", true, default);
        }
        await ErrorAsync(client, Route + "?" + Capture, 503, "historyPreparationRequired");
        await ErrorAsync(client, Route + "/overview?" + Utc + "&q=cafe", 503, "historyPreparationRequired");
        Assert.Equal(3, Ids(await GetAsync(client, Route + "?" + Utc)).Length);
        // Independent direct detail is still a complete profile while collection generations rebuild.
        await GetAsync(client, Route + "/" + rows[3].Id + "?viewerZone=Europe%2FAthens");
    }

    /// <summary>Strict bearer only; missing/foreign IDs are indistinguishable and new requests recheck current activity/revocation.</summary>
    [PostgresFact]
    public async Task CurrentOwnerAuthorityAndErrorsRemainConfidential()
    {
        var owner = await OwnerAsync(); var other = await OwnerAsync(); var rows = await SeedAsync(owner, 1);
        await using var app = await HostAsync(); using var client = Client(app, other);
        var missing = await ErrorAsync(client, Route + "/2147483647?viewerZone=UTC", 404);
        Assert.Equal(missing, await ErrorAsync(client, Route + "/" + rows[0].Id + "?viewerZone=UTC", 404));
        using var anonymous = app.GetTestClient(); UserConnectionTokenHttpTests.Cookie(anonymous, app.Services, owner);
        await ErrorAsync(anonymous, Route + "?" + Utc, 401);
        anonymous.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer ignored " + owner);
        await ErrorAsync(anonymous, Route + "?" + Utc, 401);
        using var active = Client(app, owner); await GetAsync(active, Route + "?" + Utc);
        await using (var edit = fixture.CreateContext())
            await edit.Users.Where(user => user.Id == owner).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsActive, false));
        await ErrorAsync(active, Route + "?" + Utc, 401);
        await using (var edit = fixture.CreateContext())
        {
            await edit.Users.Where(user => user.Id == owner).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsActive, true));
            await edit.ApiTokens.Where(token => token.UserId == owner).ExecuteDeleteAsync();
        }
        await ErrorAsync(active, Route + "?" + Utc, 401);
        await ErrorAsync(client, Route + "?" + Utc + "&unknown=1", 400, "parametersInvalid");
        await ErrorAsync(client, Route + "/" + rows[0].Id + "?viewerZone=UTC&q=wrong", 400, "parametersInvalid");
    }

    /// <summary>A concurrent delete after counting cannot mix count/rows; an authorized read may finish after revocation.</summary>
    [PostgresFact]
    public async Task SnapshotIsCoherentAndReadsHaveNoPersistentEffects()
    {
        var owner = await OwnerAsync(); var rows = await SeedAsync(owner, 2);
        var gate = new ReadGate();
        await using var app = await HostAsync(gate); using var client = Client(app, owner);
        var pending = client.GetAsync(Route + "?" + Utc);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            await using var edit = fixture.CreateContext();
            await edit.Locations.Where(row => row.Id == rows[0].Id).ExecuteDeleteAsync();
            await edit.Users.Where(user => user.Id == owner).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsActive, false));
        }
        finally { gate.Release.TrySetResult(); }
        using var response = await pending; Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, Ids(body).Length);
        Assert.DoesNotContain("HistorySearchText", body.GetRawText());
        Assert.Empty(gate.Writes);
        await ErrorAsync(client, Route + "?" + Utc, 401);
        await using var verify = fixture.CreateContext();
        Assert.Empty(await verify.PlaceVisitEvents.ToArrayAsync());
        Assert.False(verify.Database.HasPendingModelChanges());
    }

    /// <summary>Owner/global admission is independent of detail; disconnect cancels real SQL and releases permits.</summary>
    [PostgresFact]
    public async Task AdmissionAndCancellationPreserveIndependentDetail()
    {
        var owner = await OwnerAsync(); var rows = await SeedAsync(owner, 1);
        await using var app = await HostAsync(); using var client = Client(app, owner);
        using (LocationHistoryController.Collections.TryAcquire(owner, out _))
        using (LocationHistoryController.Collections.TryAcquire(owner, out _))
        {
            await ErrorAsync(client, Route + "?" + Utc, 429, "historyOwnerAdmissionExceeded");
            await GetAsync(client, Route + "/" + rows[0].Id + "?viewerZone=UTC");
        }
        var global = Enumerable.Range(0, 4).Select(i => LocationHistoryController.Collections.TryAcquire("history-global-" + i, out _)!).ToArray();
        try { await ErrorAsync(client, Route + "?" + Utc, 503, "historyAdmissionUnavailable"); }
        finally { foreach (var permit in global) permit.Dispose(); }
        var gate = new ReadGate();
        await using var cancelling = await HostAsync(gate); using var cancelledClient = Client(cancelling, owner);
        using var cancel = new CancellationTokenSource();
        var pending = cancelledClient.GetAsync(Route + "?" + Utc, cancel.Token);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        cancel.Cancel(); gate.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, LocationHistoryController.Collections.IdentityCount);
    }

    /// <summary>Unfinished work reaches a controlled deadline error, without a successful partial collection.</summary>
    [PostgresFact]
    public async Task CollectionDeadlineCancelsEvaluationBeforeSuccess()
    {
        var owner = await OwnerAsync(); await SeedAsync(owner, 1); var gate = new ReadGate();
        await using var app = await HostAsync(gate); using var client = Client(app, owner);
        var body = await ErrorAsync(client, Route + "/overview?" + Utc, 503, "historyBudgetExceeded");
        Assert.DoesNotContain("coverage", body); Assert.DoesNotContain("cells", body);
        Assert.True(gate.Cancelled.Task.IsCompleted);
        Assert.Equal(0, LocationHistoryController.Collections.IdentityCount);
    }

    /// <summary>Retained redirects normalize map/list identity; redirect changes invalidate a cursor, names do not.</summary>
    [PostgresFact]
    public async Task ActivityIdentityNormalizesAndInvalidatesChangedRedirects()
    {
        var owner = await OwnerAsync(); var rows = await SeedAsync(owner, 3);
        int firstId, secondId;
        await using (var edit = fixture.CreateContext())
        {
            var first = new ActivityType { Name = "Read walking" }; var second = new ActivityType { Name = "Read cycling" };
            edit.AddRange(first, second); await new ActivityCatalog(edit).SaveInitialAsync();
            firstId = first.Id; secondId = second.Id;
            edit.Add(new ActivityTypeIdRedirect { LegacyId = 1000000, ActivityTypeId = firstId });
            foreach (var row in await edit.Locations.Where(row => row.UserId == owner).ToArrayAsync()) row.ActivityTypeId = firstId;
            await edit.SaveChangesAsync();
        }
        await using var app = await HostAsync(); using var client = Client(app, owner);
        var query = Utc + "&activityIds=1000000," + firstId;
        var page = await GetAsync(client, Route + "?" + query + "&pageSize=1");
        Assert.Equal(firstId, Assert.Single(page.GetProperty("scope").GetProperty("activityIds").EnumerateArray()).GetInt32());
        var overview = await GetAsync(client, Route + "/overview?" + query);
        Assert.Equal(page.GetProperty("scopeKey").GetString(), overview.GetProperty("scopeKey").GetString());
        var continued = Route + "?" + query + "&pageSize=1&continuation=" + Uri.EscapeDataString(page.GetProperty("nextContinuation").GetString()!);
        await using (var edit = fixture.CreateContext())
            await edit.ActivityTypes.Where(row => row.Id == firstId).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Name, "Renamed walking"));
        await GetAsync(client, continued);
        await using (var edit = fixture.CreateContext())
            await edit.Set<ActivityTypeIdRedirect>().Where(row => row.LegacyId == 1000000).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ActivityTypeId, secondId));
        await ErrorAsync(client, continued, 400, "continuationInvalid");
        await ErrorAsync(client, Route + "?" + Utc + "&activityIds=2147483647", 400, "activityInvalid");
    }

    /// <summary>Detail outside the browsing scope remains complete, including large Notes, nulls and unavailable legacy values.</summary>
    [PostgresFact]
    public async Task CompleteDetailPreservesFieldsAndClassifiesUnrepresentableValues()
    {
        var owner = await OwnerAsync(); var rows = await SeedAsync(owner, 1);
        var notes = "<p>" + new string('x', 256 * 1024) + " café</p>";
        await using (var edit = fixture.CreateContext())
        {
            var row = await edit.Locations.SingleAsync(row => row.Id == rows[0].Id);
            row.Notes = notes; row.DeviceModel = "private model"; row.ProviderAddressLine1 = "independent line";
            row.Accuracy = double.NaN; row.Altitude = 123.25; await edit.SaveChangesAsync();
            await edit.Locations.Where(row => row.Id == rows[0].Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.HistoryTimeVersion, "previous-time-generation").SetProperty(row => row.Timestamp, DateTime.MinValue));
        }
        await using var app = await HostAsync(); using var client = Client(app, owner);
        var outside = await GetAsync(client, Route + "?" + Utc.Replace("2025-01-01", "2024-01-01").Replace("2026-01-01", "2024-02-01"));
        Assert.Empty(Ids(outside));
        var detail = await GetAsync(client, Route + "/" + rows[0].Id + "?viewerZone=UTC");
        var record = detail.GetProperty("record");
        Assert.Equal(notes, record.GetProperty("notesHtml").GetString());
        Assert.Equal("private model", record.GetProperty("deviceModel").GetString());
        Assert.Equal("independent line", record.GetProperty("providerAddressLine1").GetString());
        Assert.Equal(123.25, record.GetProperty("altitude").GetDouble());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("speed").ValueKind);
        Assert.False(record.TryGetProperty("accuracy", out _)); Assert.False(record.TryGetProperty("storedTimestampUtc", out _));
        Assert.Equal(2, detail.GetProperty("unavailableFields").EnumerateObject().Count());
        Assert.Equal("pendingPreparation", record.GetProperty("captureTime").GetProperty("status").GetString());
        Assert.Equal("imported-zone", record.GetProperty("storedTimeZoneId").GetString());
        Assert.DoesNotContain("historySearchText", detail.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tokenHash", detail.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Fixture-specific real MVC host: production services, fresh request contexts and no startup jobs/providers.</summary>
    private async Task<WebApplication> HostAsync(params IInterceptor[] interceptors)
    {
        await using var db = fixture.CreateContext();
        return await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.RemoveAll<ApplicationDbContext>();
            services.AddScoped(_ => fixture.CreateContext(interceptors));
        }, host => host.UseMiddleware<ApiErrorResponseMiddleware>());
    }

    private async Task<string> OwnerAsync()
    {
        var owner = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        db.Add(new ApiToken { User = null!, UserId = owner.Id, Name = "history fixture", TokenHash = ApiTokenService.HashToken(owner.Id) });
        await db.SaveChangesAsync(); return owner.Id;
    }
    private async Task<Location[]> SeedAsync(string owner, int count)
    {
        var rows = Enumerable.Range(0, count).Select(_ => Row(owner)).ToArray();
        await using var db = fixture.CreateContext(); db.AddRange(rows); await db.SaveChangesAsync(); return rows;
    }
    private static Location Row(string owner) => new()
    {
        UserId = owner, IdempotencyKey = Guid.NewGuid(), Coordinates = new Point(139.76, 35.68) { SRID = 4326 }, TimeZoneId = "imported-zone",
        LocalTimestamp = new DateTime(2025, 10, 10, 3, 0, 0, DateTimeKind.Utc).AddTicks(1234560),
        Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Notes = "<p>café station</p>", Address = "Tokyo station", Source = "import"
    };
    private static async Task ReadyAsync(ApplicationDbContext db)
    {
        var preparation = new LocationHistoryPreparation(db);
        foreach (var kind in new[] { "time", "text" })
        {
            await preparation.BeginAsync(kind, false, default);
            while (!await preparation.PrepareBatchAsync(kind, default)) { }
        }
    }
    private static HttpClient Client(WebApplication app, string owner)
    {
        var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", owner); return client;
    }
    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(body); return json.RootElement.Clone();
    }
    private static async Task<string> ErrorAsync(HttpClient client, string url, int status, string? code = null)
    {
        using var response = await client.GetAsync(url); var body = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == status, body);
        Assert.True(response.Headers.CacheControl?.NoStore);
        if (code != null) Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        if (status == 429) Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter?.Delta);
        return body;
    }
    private static int[] Ids(JsonElement body) => body.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("locationId").GetInt32()).ToArray();
    private static void Coverage(JsonElement body, string count)
    {
        Assert.Equal("incomplete", body.GetProperty("coverage").GetProperty("membership").GetString());
        Assert.Equal(count, body.GetProperty("coverage").GetProperty("unresolvedCandidateCount").GetString());
    }

    /// <summary>Hold the production count SELECT to witness snapshot/cancellation; record any attempted mutation command.</summary>
    private sealed class ReadGate : DbCommandInterceptor
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<string> Writes { get; } = [];
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SELECT count(*) AS \"Value\" FROM \"Locations\" l") && !Reached.Task.IsCompleted)
            {
                Reached.TrySetResult();
                try { await Release.Task.WaitAsync(cancellationToken); cancellationToken.ThrowIfCancellationRequested(); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            if (command.CommandText.Contains("INSERT INTO") || command.CommandText.Contains("UPDATE \"") || command.CommandText.Contains("DELETE FROM")) Writes.Add(command.CommandText);
            return result;
        }
    }
}
