using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class SegmentConditionalPostgresTests
{
    /// <summary>Malformed, ambiguous, absent, Unchanged and out-of-footprint requests fail before any write.</summary>
    [PostgresTheory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"expected\":{},\"changes\":{\"notes\":\"Mine\"}}")]
    [InlineData("{\"expected\":{\"notes\":\"original notes\"},\"changes\":{}}")]
    [InlineData("{\"expected\":{\"notes\":\"original notes\"},\"changes\":{\"notes\":null}}")]
    [InlineData("{\"expected\":{\"notes\":5},\"changes\":{\"notes\":\"Mine\"}}")]
    [InlineData("{\"expected\":{\"notes\":\"original notes\"},\"changes\":{\"notes\":false}}")]
    [InlineData("{\"expected\":{\"Notes\":null},\"changes\":{\"notes\":\"Mine\"}}")]
    [InlineData("{\"expected\":{\"notes\":null,\"notes\":\"original notes\"},\"changes\":{\"notes\":\"Mine\"}}")]
    [InlineData("{\"expected\":{\"notes\":\"original notes\"},\"changes\":{\"notes\":\"Mine\",\"notes\":\"Other\"}}")]
    [InlineData("{\"expected\":{},\"expected\":{\"notes\":\"original notes\"},\"changes\":{\"notes\":\"Mine\"}}")]
    [InlineData("{\"expected\":{\"notes\":\"original notes\"},\"changes\":{\"notes\":\"Mine\",\"route\":null}}")]
    [InlineData("{\"expected\":{\"notes\":\"original notes\"},\"changes\":{\"notes\":\"Mine\"},\"mode\":\"walk\"}")]
    [InlineData("{\"expected\":{\"notes\":\"original notes\",\"displayOrder\":1},\"changes\":{\"notes\":\"Mine\"}}")]
    [InlineData("{\"expected\":{\"notes\":\"original notes\"},\"changes\":{\"estimatedDurationMinutes\":90}}")]
    public async Task StrictRequest_ZeroWrites(string json)
    {
        var seed = await SeedAsync();
        var before = await SnapshotAsync(seed);
        var probe = new SegmentProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, probe);
        using var bearer = Bearer(app, seed.UserId);
        using var response = await bearer.PatchAsync(PatchUrl(seed, false), new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(seed));
        Assert.Empty(probe.Updates);
        Assert.Empty(_warmup.Invocations);
    }

    /// <summary>Null and empty remain distinct originals; explicit Clear and sanitized Set publish exact nullable evidence and fresh opaque tokens.</summary>
    [PostgresTheory]
    [InlineData(null, "", true)]
    [InlineData("", "", false)]
    [InlineData("old<script>unsafe()</script>", "<p>New</p><script>unsafe()</script>", true)]
    public async Task CanonicalContentAndPhysicalFootprint(string? original, string desired, bool applied)
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        await db.Segments.Where(s => s.Id == seed.SegmentId).ExecuteUpdateAsync(s => s.SetProperty(s => s.Notes, original));
        var future = DateTime.UtcNow.Date.AddDays(1);
        await db.Trips.Where(t => t.Id == seed.TripId).ExecuteUpdateAsync(s => s.SetProperty(t => t.Name, "Independent Trip name")
            .SetProperty(t => t.Notes, "Root notes").SetProperty(t => t.IsPublic, true).SetProperty(t => t.ShareProgressEnabled, true)
            .SetProperty(t => t.CoverImageUrl, "https://example.test/cover.jpg").SetProperty(t => t.CenterLat, 42).SetProperty(t => t.CenterLon, 23)
            .SetProperty(t => t.Zoom, 9).SetProperty(t => t.UpdatedAt, future));
        var probe = new SegmentProbe();
        await using var app = await StartAsync(db, probe);
        var published = await PublishedAsync(app, seed);
        Assert.Equal(RichNotes.Normalize(original), published.OriginalValues!.NotesHtml);
        var before = JsonNode.Parse(await SnapshotAsync(seed))!;
        using var web = await WebAsync(app, seed.UserId);
        using var response = await PatchAsync(web, seed, true, published.OriginalValues.NotesHtml, desired);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var saved = json.GetProperty("data");
        Assert.Equal(RichNotes.Normalize(desired), saved.GetProperty("notesHtml").GetString());
        Assert.Equal(RichNotes.Normalize(desired), saved.GetProperty("originalValues").GetProperty("notesHtml").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("affected").GetProperty("segmentOrder").ValueKind);
        var after = JsonNode.Parse(await SnapshotAsync(seed))!;
        Assert.Equal(applied, before["version"]!.ToString() != after["version"]!.ToString());
        var tokens = app.Services.GetRequiredService<SegmentAggregateTokenService>();
        Assert.True(tokens.TryRead(published.AggregateConcurrencyToken, seed.UserId, seed.TripId, seed.SegmentId!.Value, out var originalVersion));
        Assert.True(tokens.TryRead(saved.GetProperty("aggregateConcurrencyToken").GetString(), seed.UserId, seed.TripId, seed.SegmentId!.Value, out var savedVersion));
        Assert.Equal(applied, originalVersion != savedVersion);
        Assert.Equal(after["version"]!.ToString(), savedVersion.ToString());
        before["notes"] = after["notes"]?.DeepClone();
        before["version"] = after["version"]!.DeepClone();
        Assert.True(JsonNode.DeepEquals(before, after), "All other Segment scalars/provenance, waypoints/xmin and Trip metadata/future timestamp must be identical.");
        Assert.Equal(applied ? 2 : 0, probe.Updates.Count);
        Assert.Empty(_warmup.Invocations);
        foreach (var sql in probe.Updates) output.WriteLine(sql);
    }

    /// <summary>Originals are compared exactly to published canonical Notes; malicious images never become hidden field writes.</summary>
    [PostgresTheory]
    [InlineData("original notes<script>same canonical text</script>", "Mine", 409)]
    [InlineData("original notes", "<img src='data:image/png;base64,AAAA'>", 400)]
    public async Task InvalidContentOrInventedBase_ZeroWrites(string original, string desired, int status)
    {
        var seed = await SeedAsync();
        var before = await SnapshotAsync(seed);
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var bearer = Bearer(app, seed.UserId);
        using var response = await PatchAsync(bearer, seed, false, original, desired);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(seed));
    }

    /// <summary>Bearer and cookie/antiforgery boundaries stay distinct; missing/foreign/scoped identities remain concealed.</summary>
    [PostgresFact]
    public async Task AuthAndScope_NoCrossCredentialFallback()
    {
        var seed = await SeedAsync();
        var foreign = await SeedAsync();
        var before = await SnapshotAsync(seed);
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var cookie = await WebAsync(app, seed.UserId);
        using var bearer = Bearer(app, seed.UserId);
        using var cookieToBearer = await PatchAsync(cookie, seed, false, "original notes", "Mine");
        Assert.Equal(HttpStatusCode.Unauthorized, cookieToBearer.StatusCode);
        using var bearerToCookie = await PatchAsync(bearer, seed, true, "original notes", "Mine");
        Assert.NotEqual(HttpStatusCode.OK, bearerToCookie.StatusCode);
        using var noAnti = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(noAnti, app.Services, seed.UserId);
        using var rejected = await PatchAsync(noAnti, seed, true, "original notes", "Mine");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var wrong = await PatchAsync(bearer, foreign, false, "original notes", "Mine");
        using var missing = await PatchAsync(bearer, seed with { SegmentId = Guid.NewGuid() }, false, "original notes", "Mine");
        using var scope = await PatchAsync(cookie, seed with { TripId = foreign.TripId }, true, "original notes", "Mine");
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await missing.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, scope.StatusCode);
        Assert.Equal(before, await SnapshotAsync(seed));
    }

    /// <summary>Intervening Notes invalidate old complete route/mixed edits; deliberate adoption restores the one-request workflow.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AggregateToken_RejectsStaleRouteOrMixedDraft(bool mixed)
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        var probe = new SegmentProbe();
        await using var app = await StartAsync(db, probe);
        using var web = await WebAsync(app, seed.UserId);
        using var bearer = Bearer(app, seed.UserId);
        var old = await PublishedAsync(app, seed);
        using var notes = await PatchAsync(bearer, seed, false, old.NotesHtml, "Mobile first");
        Assert.Equal(HttpStatusCode.OK, notes.StatusCode);
        var before = await SnapshotAsync(seed);
        using var stale = await web.PutAsJsonAsync(WebUrl(seed), FullRequest(old, mixed ? "Stale mixed" : null, 123));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("segment-aggregate-stale", await stale.Content.ReadAsStringAsync());
        Assert.Equal(before, await SnapshotAsync(seed));
        Assert.Equal(2, probe.Updates.Count);
        var reviewed = await PublishedAsync(app, seed);
        using var retry = await web.PutAsJsonAsync(WebUrl(seed), FullRequest(reviewed, mixed ? "Reviewed mixed" : null, 123));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        await using var verify = fixture.CreateContext();
        var stored = await ReadAsync(verify, seed);
        Assert.Equal(mixed ? "Reviewed mixed" : "Mobile first", stored.Notes);
        Assert.Equal(123, stored.EstimatedDuration!.Value.TotalMinutes);
        using var mobileAfter = await PatchAsync(bearer, seed, false, mixed ? "Mobile first" : reviewed.NotesHtml, "Mobile after route");
        Assert.Equal(mixed ? HttpStatusCode.Conflict : HttpStatusCode.OK, mobileAfter.StatusCode);
        output.WriteLine($"Complete Web mixed={mixed}: stale token rejected with zero effects; deliberate refreshed retry retained single aggregate commit.");
    }
    /// <summary>Builds the strict C4 dialect without the fields an older server requires.</summary>
    private static JsonObject RouteRequest(Wayfarer.Models.Dtos.Editor.EditorSegmentDto dto, string? notes = null, double? duration = null)
    {
        var request = JsonSerializer.SerializeToNode(FullRequest(dto, duration: duration), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        request.Remove("notesHtml");
        request.Remove("aggregateConcurrencyToken");
        request["contract"] = "segment-route-notes-1";
        request["originalRouteToken"] = dto.OriginalRouteToken;
        if (notes != null)
        {
            request["expected"] = new JsonObject { ["notesHtml"] = dto.OriginalValues!.NotesHtml };
            request["changes"] = new JsonObject { ["notesHtml"] = notes };
        }
        return request;
    }

}
