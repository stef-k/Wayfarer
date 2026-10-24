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
using Xunit.Abstractions;

namespace Wayfarer.Tests.Controllers;

/// <summary>Actual routed PostgreSQL owner reads, snapshots, paging, preparation and geographic coverage.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationHistoryHttpPostgresTests(PostgresMigrationTestFixture fixture, ITestOutputHelper output) : TestBase, IClassFixture<PostgresMigrationTestFixture>
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
        var owner = await OwnerAsync(); var rows = await SeedAsync(owner, 69);
        await using (var edit = fixture.CreateContext())
        {
            for (var i = 0; i < 65; i++)
            {
                var row = rows[i];
                edit.Attach(row); row.Coordinates = new Point(23.73, 37.98) { SRID = 4326 };
                row.LocalTimestamp = row.LocalTimestamp.AddDays(-i * 4);
            }
            edit.Attach(rows[68]); rows[68].Coordinates = new Point(-74, 40.7) { SRID = 4326 };
            await edit.SaveChangesAsync();
        }
        await using var app = await HostAsync(); using var client = Client(app, owner);
        var overview = await GetAsync(client, Route + "/overview?" + Utc + "&grid=4");
        Assert.Equal("69", overview.GetProperty("knownMatchCount").GetString());
        Assert.Equal("0", overview.GetProperty("unmappedMatchCount").GetString());
        var cells = overview.GetProperty("cells").EnumerateArray().ToArray(); Assert.Equal(3, cells.Length);
        Assert.Single(cells, cell => cell.GetProperty("recordCount").GetString() == "65");
        var sparse = Assert.Single(cells, cell => cell.GetProperty("recordCount").GetString() == "1");
        Assert.Equal(rows[68].Id, sparse.GetProperty("singleton").GetProperty("locationId").GetInt32());
        var journey = Assert.Single(cells, cell => cell.GetProperty("recordCount").GetString() == "3");
        var scope = Utc;
        // Repeated spatial refinement cannot separate coincident records; exact choices remain necessary.
        foreach (var grid in new[] { 4, 16 })
        {
            scope = Utc + "&" + string.Join("&", journey.GetProperty("bounds").EnumerateObject()
                .Select(edge => edge.Name + "=" + edge.Value.GetRawText()));
            var refined = await GetAsync(client, Route + "/overview?" + scope + "&grid=" + grid);
            Assert.Equal("3", refined.GetProperty("mappedMatchCount").GetString());
            journey = Assert.Single(refined.GetProperty("cells").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, journey.GetProperty("singleton").ValueKind);
            output.WriteLine("Refinement: " + refined.GetRawText());
        }
        scope = Utc + "&" + string.Join("&", journey.GetProperty("bounds").EnumerateObject()
            .Select(edge => edge.Name + "=" + edge.Value.GetRawText()));
        var choices = await GetAsync(client, Route + "?" + scope + "&pageSize=2");
        var next = await GetAsync(client, Route + "?" + scope + "&pageSize=2&continuation="
            + Uri.EscapeDataString(choices.GetProperty("nextContinuation").GetString()!));
        var selected = Ids(choices)[0];
        Assert.Equal(rows.Skip(65).Take(3).Select(row => row.Id).Order(), Ids(choices).Concat(Ids(next)).Order());
        Assert.Equal(JsonValueKind.Null, next.GetProperty("nextContinuation").ValueKind);
        var detail = await GetAsync(client, Route + "/" + selected + "?viewerZone=Europe%2FAthens");
        Assert.Equal(selected, detail.GetProperty("record").GetProperty("locationId").GetInt32());
        Assert.Equal("2026-01-01T00:00:00.000000Z", detail.GetProperty("record").GetProperty("storedTimestampUtc").GetString());
        Assert.Equal("not-evaluated", detail.GetProperty("filterMembership").GetString());
        Assert.Equal("owner-detail-1", detail.GetProperty("profile").GetString());
        // Main chronology is opened only after selected detail; it uses the refined scope independently.
        var first = await GetAsync(client, Route + "?" + scope + "&pageSize=1");
        var ids = new List<int>(Ids(first)); var page = first;
        while (page.GetProperty("nextContinuation").GetString() is { } cursor)
        {
            Assert.True(ids.Count < 3, "Coincident choices must terminate after all three identities.");
            page = await GetAsync(client, Route + "?" + scope + "&pageSize=1&continuation=" + Uri.EscapeDataString(cursor));
            Assert.Equal(first.GetProperty("scopeKey").GetString(), page.GetProperty("scopeKey").GetString());
            ids.AddRange(Ids(page));
        }
        Assert.Equal(Ids(choices).Concat(Ids(next)), ids);
        Assert.Equal(choices.GetProperty("scopeKey").GetString(), first.GetProperty("scopeKey").GetString());
        var refreshed = await GetAsync(client, Route + "/overview?" + scope + "&grid=16");
        Assert.Equal("3", refreshed.GetProperty("knownMatchCount").GetString());
        output.WriteLine("Full-period overview: " + overview.GetRawText());
        output.WriteLine("Choices: " + choices.GetRawText()); output.WriteLine("Detail: " + detail.GetRawText());
    }

    /// <summary>The review's exact floating-point edge remains selectable through its published half-open bounds on unchanged data.</summary>
    [PostgresFact]
    public async Task OverviewPartitionBoundaryKeepsCountedRecordSelectable()
    {
        var owner = await OwnerAsync(); var rows = await SeedAsync(owner, 3);
        await using (var edit = fixture.CreateContext())
        {
            var boundary = await edit.Locations.SingleAsync(row => row.Id == rows[0].Id);
            boundary.Coordinates = new Point(139.70624999999998, 35.68) { SRID = 4326 };
            await edit.SaveChangesAsync();
        }
        await using var app = await HostAsync(); using var client = Client(app, owner);
        var query = Utc.Replace("Europe%2FAthens", "UTC");
        var overview = await GetAsync(client, Route + "/overview?" + query + "&west=139.7&south=35&east=139.8&north=36&grid=16");
        Assert.Equal("3", overview.GetProperty("mappedMatchCount").GetString());
        var cell = Assert.Single(overview.GetProperty("cells").EnumerateArray(), cell => cell.GetProperty("recordCount").GetString() == "1");
        Assert.Equal(rows[0].Id, cell.GetProperty("singleton").GetProperty("locationId").GetInt32());
        var area = string.Join("&", cell.GetProperty("bounds").EnumerateObject().Select(edge => edge.Name + "=" + edge.Value.GetRawText()));
        // No writes occur between aggregation and choices; cross-request live changes are a separate contract.
        var choices = await GetAsync(client, Route + "?" + query + "&" + area);
        Assert.True(Ids(choices).SequenceEqual(new[] { rows[0].Id }), "Overview: " + overview.GetRawText() + " Choices: " + choices.GetRawText());
        Assert.Equal("complete", choices.GetProperty("coverage").GetProperty("membership").GetString());
        Assert.Equal("0", choices.GetProperty("coverage").GetProperty("unresolvedCandidateCount").GetString());
        Assert.Equal(JsonValueKind.Null, choices.GetProperty("nextContinuation").ValueKind);
    }

    /// <summary>One-ULP neighbours on both axes share exact edges, including canonical longitude across the dateline.</summary>
    [PostgresTheory]
    [InlineData(139.7, 139.8, 139.70624999999998)]
    [InlineData(170.1, -169.9, -179.9)]
    public async Task OverviewSharedEdgesHaveOneOwner(double west, double east, double sharedLongitude)
    {
        const double sharedLatitude = 35.6875;
        var owner = await OwnerAsync(); var rows = await SeedAsync(owner, 7);
        (double X, double Y)[] points = [
            (Math.BitDecrement(sharedLongitude), sharedLatitude), (sharedLongitude, sharedLatitude),
            (Math.BitIncrement(sharedLongitude), sharedLatitude), (sharedLongitude, Math.BitDecrement(sharedLatitude)),
            (sharedLongitude, Math.BitIncrement(sharedLatitude)), (east, sharedLatitude), (sharedLongitude, 36)
        ];
        await using (var edit = fixture.CreateContext())
        {
            foreach (var (row, point) in rows.Zip(points))
            {
                edit.Attach(row); row.Coordinates = new Point(point.X, point.Y) { SRID = 4326 };
            }
            await edit.SaveChangesAsync(); await ReadyAsync(edit);
        }
        await using var app = await HostAsync(); using var client = Client(app, owner);
        var area = FormattableString.Invariant($"&west={west:R}&south=35&east={east:R}&north=36");
        var overview = await AssertPartitionChoicesAsync(client, Utc + "&q=cafe&includeUnassigned=true", area, 16, rows.Take(5));
        var cells = overview.GetProperty("cells").EnumerateArray().ToArray();
        var upper = Assert.Single(cells, cell => cell.GetProperty("bounds").GetProperty("west").GetDouble() == sharedLongitude
            && cell.GetProperty("bounds").GetProperty("south").GetDouble() == sharedLatitude);
        Assert.Equal("3", upper.GetProperty("recordCount").GetString());
        Assert.Single(cells, cell => cell.GetProperty("bounds").GetProperty("east").GetDouble() == sharedLongitude);
        Assert.Single(cells, cell => cell.GetProperty("bounds").GetProperty("north").GetDouble() == sharedLatitude);
    }

    /// <summary>World endpoints, both pole inclusions and the +/-180 alias survive full-world and wrapping partitions.</summary>
    [PostgresTheory]
    [InlineData("", 1, 7)]
    [InlineData("", 2, 7)]
    [InlineData("&west=170&south=-90&east=-170&north=90", 2, 5)]
    [InlineData("&west=180&south=-90&east=-179.99999999999997&north=90", 16, 4)]
    public async Task OverviewWorldAndWrappingEdgesKeepExactChoices(string area, int grid, int count)
    {
        var owner = await OwnerAsync(); var rows = await SeedAsync(owner, 7);
        (double X, double Y)[] points = [(-180, -90), (180, -90), (-180, 90), (180, 90), (170, -90), (-170, 90), (0, 0)];
        await using (var edit = fixture.CreateContext())
        {
            foreach (var (row, point) in rows.Zip(points))
            {
                edit.Attach(row); row.Coordinates = new Point(point.X, point.Y) { SRID = 4326 };
            }
            await edit.SaveChangesAsync();
        }
        await using var app = await HostAsync(); using var client = Client(app, owner);
        await AssertPartitionChoicesAsync(client, Utc, area, grid, rows.Take(count));
    }

    /// <summary>On unchanged data, serialized cell bounds recover exactly the parent matches once, with matching counts and refinement.</summary>
    private static async Task<JsonElement> AssertPartitionChoicesAsync(HttpClient client, string query, string area, int grid, IEnumerable<Location> expected)
    {
        var expectedIds = expected.Select(row => row.Id).Order().ToArray();
        var overview = await GetAsync(client, Route + "/overview?" + query + area + "&grid=" + grid);
        Assert.Equal(expectedIds.Length.ToString(), overview.GetProperty("knownMatchCount").GetString());
        Assert.Equal(expectedIds.Length.ToString(), overview.GetProperty("mappedMatchCount").GetString());
        Assert.Equal("0", overview.GetProperty("unmappedMatchCount").GetString());
        var seen = new HashSet<int>();
        foreach (var cell in overview.GetProperty("cells").EnumerateArray())
        {
            var bounds = string.Join("&", cell.GetProperty("bounds").EnumerateObject().Select(edge => edge.Name + "=" + edge.Value.GetRawText()));
            var choices = await GetAsync(client, Route + "?" + query + "&" + bounds);
            Assert.Equal(cell.GetProperty("recordCount").GetString(), Ids(choices).Length.ToString());
            Assert.Equal("complete", choices.GetProperty("coverage").GetProperty("membership").GetString());
            Assert.Equal(JsonValueKind.Null, choices.GetProperty("nextContinuation").ValueKind);
            foreach (var id in Ids(choices)) Assert.True(seen.Add(id), "A record was returned by more than one cell.");
            var refined = await GetAsync(client, Route + "/overview?" + query + "&" + bounds + "&grid=1");
            Assert.Equal(cell.GetProperty("recordCount").GetString(), refined.GetProperty("mappedMatchCount").GetString());
        }
        Assert.Equal(expectedIds, seen.Order());
        Assert.Equal(expectedIds, Ids(await GetAsync(client, Route + "?" + query + area)).Order());
        return overview;
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

    /// <summary>A concurrent delete after counting cannot mix rows, counts or singleton UUIDs; an authorized read may finish after revocation.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotIsCoherentAndReadsHaveNoPersistentEffects(bool overview)
    {
        var owner = await OwnerAsync(); var rows = await SeedAsync(owner, overview ? 1 : 2);
        var gate = new ReadGate();
        await using var app = await HostAsync(gate); using var client = Client(app, owner);
        var pending = client.GetAsync(Route + (overview ? "/overview" : "") + "?" + Utc);
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
        if (overview)
        {
            Assert.Equal("1", body.GetProperty("knownMatchCount").GetString());
            var singleton = Assert.Single(body.GetProperty("cells").EnumerateArray()).GetProperty("singleton");
            Assert.Equal(rows[0].Id, singleton.GetProperty("locationId").GetInt32());
            Assert.Equal(rows[0].IdempotencyKey!.Value.ToString("D"), singleton.GetProperty("uuid").GetString());
        }
        else Assert.Equal(2, Ids(body).Length);
        Assert.DoesNotContain("HistorySearchText", body.GetRawText());
        Assert.Empty(gate.Writes);
        await ErrorAsync(client, Route + "?" + Utc, 401);
        await using var verify = fixture.CreateContext();
        Assert.Empty(await verify.PlaceVisitEvents.ToArrayAsync());
        Assert.False(verify.Database.HasPendingModelChanges());
    }

    /// <summary>Owner/global admission is independent of detail; disconnect cancels pending database work and releases permits.</summary>
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

    /// <summary>Both collections can finish beyond the former ceiling before the approved deadline.</summary>
    [PostgresTheory]
    [InlineData("")]
    [InlineData("/overview")]
    public async Task CollectionsCanCompleteAfterFormerDeadline(string suffix)
    {
        var owner = await OwnerAsync(); await SeedAsync(owner, 1);
        var gate = new ReadGate(delayInPostgres: true, delaySeconds: 6);
        await using var app = await HostAsync(gate); using var client = Client(app, owner);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await GetAsync(client, Route + suffix + "?" + Utc);
        Assert.True(clock.Elapsed.TotalSeconds >= 5.5);
        Assert.Equal(0, LocationHistoryController.Collections.IdentityCount);
    }

    /// <summary>Unfinished work reaches the approved ceiling without a successful partial collection.</summary>
    [PostgresFact]
    public async Task CollectionDeadlineCancelsEvaluationBeforeSuccess()
    {
        var owner = await OwnerAsync(); await SeedAsync(owner, 1); var gate = new ReadGate(delayInPostgres: true);
        await using var app = await HostAsync(gate); using var client = Client(app, owner);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var body = await ErrorAsync(client, Route + "/overview?" + Utc, 503, "historyBudgetExceeded");
        Assert.InRange(clock.Elapsed.TotalSeconds, 14, 25);
        Assert.DoesNotContain("coverage", body); Assert.DoesNotContain("cells", body);
        Assert.True(gate.Reached.Task.IsCompleted);
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
        var notes = "<p>" + string.Join(" ", Enumerable.Range(0, 32768).Select(i => $"Diary {i}: café 東京.")) + "</p>";
        await using (var edit = fixture.CreateContext())
        {
            var row = await edit.Locations.SingleAsync(row => row.Id == rows[0].Id);
            row.Notes = notes; row.DeviceModel = "private model"; row.ProviderAddressLine1 = "independent line";
            row.FullAddress = "10 Station Road, Tokyo"; row.AddressNumber = "10"; row.StreetName = "Station Road";
            row.PostCode = "100-0005"; row.Place = "Tokyo"; row.Region = "Kantō"; row.Country = "Japan";
            row.ResolvedFeatureName = "Station café"; row.ResolvedFeatureType = "amenity";
            row.ReverseGeocodingProvider = "geoapify"; row.ReverseGeocodingStorageMode = "permanent";
            row.ReverseGeocodedAt = new DateTimeOffset(2025, 10, 11, 1, 2, 3, TimeSpan.Zero);
            row.CoordinateOrigin = "sensor"; row.ObservedAgeAtEventMs = 1250;
            row.ObservedAtUtc = new DateTimeOffset(2025, 10, 10, 2, 59, 58, TimeSpan.Zero);
            row.PresenceObservedAtUtc = new DateTime(2025, 10, 11, 3, 0, 0, DateTimeKind.Utc);
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
        Assert.Equal("2025-10-10T03:00:00.123456Z", record.GetProperty("eventUtc").GetString());
        Assert.Equal(rows[0].IdempotencyKey!.Value.ToString("D"), record.GetProperty("uuid").GetString());
        foreach (var (field, value) in new[] { ("fullAddress", "10 Station Road, Tokyo"), ("addressNumber", "10"),
                     ("streetName", "Station Road"), ("postCode", "100-0005"), ("place", "Tokyo"), ("region", "Kantō"),
                     ("country", "Japan"), ("resolvedFeatureName", "Station café"), ("resolvedFeatureType", "amenity"),
                     ("reverseGeocodingProvider", "geoapify"), ("reverseGeocodingStorageMode", "permanent"), ("coordinateOrigin", "sensor") })
            Assert.Equal(value, record.GetProperty(field).GetString());
        Assert.Equal(1250, record.GetProperty("observedAgeAtEventMs").GetInt64());
        Assert.Equal("2025-10-10T02:59:58.000000Z", record.GetProperty("observedAtUtc").GetString());
        Assert.Equal("2025-10-11T03:00:00.000000Z", record.GetProperty("presenceObservedAtUtc").GetString());
        Assert.Equal("2025-10-11T01:02:03.000000Z", record.GetProperty("reverseGeocodedAt").GetString());
        output.WriteLine($"Complete Notes: {System.Text.Encoding.UTF8.GetByteCount(notes)} UTF-8 bytes; detail: {System.Text.Encoding.UTF8.GetByteCount(detail.GetRawText())} bytes.");
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
    private sealed class ReadGate(bool delayInPostgres = false, int delaySeconds = 30) : DbCommandInterceptor
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
                if (delayInPostgres)
                {
                    // A DO command delays SQL without adding a result set or an optimizable join.
                    command.CommandText = $"DO $$ BEGIN PERFORM pg_sleep({delaySeconds}); END $$; " + command.CommandText;
                    return result;
                }
                try { await Release.Task.WaitAsync(cancellationToken); cancellationToken.ThrowIfCancellationRequested(); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            if (command.CommandText.Contains("INSERT INTO") || command.CommandText.Contains("UPDATE \"") || command.CommandText.Contains("DELETE FROM")) Writes.Add(command.CommandText);
            return result;
        }
    }
}
