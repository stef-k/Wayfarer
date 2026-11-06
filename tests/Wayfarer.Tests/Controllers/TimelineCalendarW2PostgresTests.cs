using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
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

    /// <summary>Known unmapped records page by exact identity; month presence replaces selected dates and preserves uncertainty.</summary>
    [PostgresFact]
    public async Task RecordsAndAvailabilityUsePrivateFilteredScope()
    {
        var owner = await fixture.CreateUserAsync();
        var first = Row(owner.Id, "2026-10-25T10:00Z"); first.Notes = "literal_% blue";
        var unmapped = Row(owner.Id, "2026-10-25T11:00Z"); unmapped.Notes = first.Notes; unmapped.Coordinates = NetTopologySuite.Geometries.Point.Empty;
        var outside = Row(owner.Id, "2026-10-26T10:00Z"); outside.Notes = first.Notes;
        await SeedAsync(first, unmapped, outside); await ReadyTextAsync();
        await using var app = await HostAsync(); using var client = Client(app, owner.Id); await PostTokenAsync(client);
        var values = new Dictionary<string, string>(W2Day) { ["q"] = "literal_%", ["pageSize"] = "1" };
        var page = await PostAsync(client, "records", values);
        Assert.Equal(unmapped.Id, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("locationId").GetInt32());
        Assert.Equal("live-request", page.GetProperty("consistency").GetString());
        values["continuation"] = page.GetProperty("nextContinuation").GetString()!;
        page = await PostAsync(client, "records", values);
        Assert.Equal(first.Id, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("locationId").GetInt32());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextContinuation").ValueKind);
        values["q"] = "other";
        await PostAsync(client, "records", values, 400, "continuationInvalid");
        var month = new Dictionary<string, string>(W2Day); month.Remove("dateType"); month.Remove("day"); month["q"] = "literal_%";
        var marks = await PostAsync(client, "availability", month);
        Assert.Equal(new[] { "2026-10-25", "2026-10-26" }, marks.GetProperty("presentDates").EnumerateArray().Select(x => x.GetString()));
        Assert.False(marks.GetProperty("hasUnresolvedCandidates").GetBoolean());
        month["timeBasis"] = "capture";
        marks = await PostAsync(client, "availability", month);
        Assert.True(marks.GetProperty("hasUnresolvedCandidates").GetBoolean());
        var clock = Stopwatch.StartNew();
        await PostAsync(client, "analysis", new Dictionary<string, string>(W2Day) { ["q"] = "literal_%" });
        output.WriteLine($"Filtered routed analysis: {clock.Elapsed.TotalMilliseconds:F2} ms; three owner source rows, independent snapshots.");
        using var noToken = Client(app, owner.Id);
        await PostAsync(noToken, "records", W2Day, 400);
        await PostAsync(client, "period?q=private", W2Day, 400, "parametersInvalid");
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
