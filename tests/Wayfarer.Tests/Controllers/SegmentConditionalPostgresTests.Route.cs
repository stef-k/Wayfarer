using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class SegmentConditionalPostgresTests
{
    /// <summary>Independently protected original route and Notes intent survive either sequential save order.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndependentRouteAndNotes_PreserveBoth(bool notesFirst)
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, seed.UserId);
        using var bearer = Bearer(app, seed.UserId);
        var original = await PublishedAsync(app, seed);
        async Task SaveNotes()
        {
            using var response = await PatchAsync(bearer, seed, false, original.OriginalValues!.NotesHtml, "Independent Notes");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        if (notesFirst) await SaveNotes();
        using var route = await web.PutAsJsonAsync(WebUrl(seed), RouteRequest(original, duration: 123));
        Assert.True(route.StatusCode == HttpStatusCode.OK, await route.Content.ReadAsStringAsync());
        if (!notesFirst) await SaveNotes();
        await using var verify = fixture.CreateContext();
        var stored = await ReadAsync(verify, seed);
        Assert.Equal("Independent Notes", stored.Notes);
        Assert.Equal(123, stored.EstimatedDuration!.Value.TotalMinutes);
        Assert.Equal(original.WaypointPlaceIds, stored.Waypoints.Select(w => w.PlaceId));
    }

    /// <summary>Unchanged Notes never assigns or normalizes the stored nullable value during a route save.</summary>
    [PostgresTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("original<script>historical</script>")]
    public async Task RouteOnly_PreservesExactUntouchedNotes(string? notes)
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        await db.Segments.Where(s => s.Id == seed.SegmentId).ExecuteUpdateAsync(s => s.SetProperty(s => s.Notes, notes));
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, seed.UserId);
        var request = RouteRequest(await PublishedAsync(app, seed), duration: 123);
        request["changes"] = new JsonObject { ["notesHtml"] = null };
        using var response = await web.PutAsJsonAsync(WebUrl(seed), request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        await using var verify = fixture.CreateContext();
        Assert.Equal(notes, (await ReadAsync(verify, seed)).Notes);
    }

    /// <summary>Nullable original Notes with Set or Clear commits together with route measurements in one request.</summary>
    [PostgresTheory]
    [InlineData(null, "")]
    [InlineData(null, "<p>New Notes</p>")]
    [InlineData("original notes", "")]
    public async Task CombinedSetOrClear_CommitsBoth(string? original, string desired)
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        await db.Segments.Where(s => s.Id == seed.SegmentId).ExecuteUpdateAsync(s => s.SetProperty(s => s.Notes, original));
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, seed.UserId);
        var request = RouteRequest(await PublishedAsync(app, seed), desired, 123);
        using var response = await web.PutAsJsonAsync(WebUrl(seed), request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        await using var verify = fixture.CreateContext();
        var stored = await ReadAsync(verify, seed);
        Assert.Equal(desired, stored.Notes);
        Assert.Equal(123, stored.EstimatedDuration!.Value.TotalMinutes);
    }

    /// <summary>Each original route dimension and independent Notes veto a combined stale save before any effects.</summary>
    [PostgresTheory]
    [InlineData("notes")]
    [InlineData("route")]
    [InlineData("anchor")]
    [InlineData("scope")]
    [InlineData("waypoint")]
    [InlineData("profile")]
    [InlineData("measurement")]
    [InlineData("provenance")]
    [InlineData("order")]
    public async Task StaleOriginal_RejectsCombinedWithoutPartialWrites(string drift)
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, seed.UserId);
        var original = await PublishedAsync(app, seed);
        await using (var other = fixture.CreateContext())
        {
            var segment = await other.Segments.FindAsync(seed.SegmentId);
            if (drift == "notes") segment!.Notes = "Other Notes";
            if (drift == "route") segment!.RouteGeometry = null;
            if (drift == "profile") { segment!.Mode = seed.SecondProfileKey; segment.TransportProfileId = seed.SecondProfileId; }
            if (drift == "measurement") segment!.EstimatedDistanceKm = 42;
            if (drift == "provenance") segment!.RouteAttribution = "New attribution";
            if (drift == "order") segment!.DisplayOrder++;
            if (drift == "scope")
            {
                var region = new Region { Id = Guid.NewGuid(), TripId = seed.TripId, UserId = seed.UserId, Name = "Moved anchor scope" };
                other.Regions.Add(region);
                (await other.Places.FindAsync(seed.FromId))!.RegionId = region.Id;
            }
            if (drift == "anchor") (await other.Places.FindAsync(seed.FromId))!.Location = new(10, 10) { SRID = 4326 };
            if (drift == "waypoint") (await other.Set<SegmentWaypoint>().FirstAsync(w => w.SegmentId == seed.SegmentId)).RouteVertexIndex = null;
            await other.SaveChangesAsync();
        }
        var before = await SnapshotAsync(seed);
        using var rejected = await web.PutAsJsonAsync(WebUrl(seed), RouteRequest(original, "My Notes", 123));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains(drift == "notes" ? "segment-notes-stale" : "segment-route-stale", await rejected.Content.ReadAsStringAsync());
        Assert.Equal(before, await SnapshotAsync(seed));
    }

    /// <summary>Validation and an application failure both preserve route, Notes, children and Trip freshness atomically.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CombinedFailure_LeavesBothUnchanged(bool applicationFailure)
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = applicationFailure ? await StartAsync(db, new FailCommit(false)) : await StartAsync(db);
        using var web = await WebAsync(app, seed.UserId);
        var request = RouteRequest(await PublishedAsync(app, seed), "My Notes", applicationFailure ? 123 : double.MaxValue);
        var before = await SnapshotAsync(seed);
        using var rejected = await web.PutAsJsonAsync(WebUrl(seed), request);
        Assert.Equal(applicationFailure ? HttpStatusCode.InternalServerError : HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(before, await SnapshotAsync(seed));
    }

    /// <summary>Notes alone preserves explicit route-clear consent; authored stale Notes still rejects the combined attempt.</summary>
    [PostgresFact]
    public async Task NotesChange_PreservesRouteClearAuthority()
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, seed.UserId);
        var original = await PublishedAsync(app, seed);
        var request = RouteRequest(original, duration: 123);
        request["waypointPlaceIds"]![0] = seed.AlternateId.ToString();
        using var challenge = await web.PutAsJsonAsync(WebUrl(seed), request);
        Assert.Equal(HttpStatusCode.Conflict, challenge.StatusCode);
        var consent = challenge.Headers.GetValues("X-Wayfarer-Clear-Route-Confirmation").Single();
        using var notes = await PatchAsync(web, seed, true, original.OriginalValues!.NotesHtml, "Later Notes");
        Assert.Equal(HttpStatusCode.OK, notes.StatusCode);
        var before = await SnapshotAsync(seed);
        web.DefaultRequestHeaders.Add("X-Wayfarer-Clear-Route-Confirmation", consent);
        var altered = request.DeepClone().AsObject();
        altered["estimatedDurationMinutes"] = 124;
        using var staleConsent = await web.PutAsJsonAsync(WebUrl(seed), altered);
        Assert.Equal(HttpStatusCode.Conflict, staleConsent.StatusCode);
        Assert.Contains("segment-route-clear-confirmation-stale", await staleConsent.Content.ReadAsStringAsync());
        Assert.False(staleConsent.Headers.Contains("X-Wayfarer-Clear-Route-Confirmation"));
        Assert.Equal(before, await SnapshotAsync(seed));
        var combined = request.DeepClone().AsObject();
        combined["expected"] = new JsonObject { ["notesHtml"] = original.OriginalValues.NotesHtml };
        combined["changes"] = new JsonObject { ["notesHtml"] = "My stale Notes" };
        using var rejected = await web.PutAsJsonAsync(WebUrl(seed), combined);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains("segment-notes-stale", await rejected.Content.ReadAsStringAsync());
        Assert.Equal(before, await SnapshotAsync(seed));
        using var saved = await web.PutAsJsonAsync(WebUrl(seed), request);
        Assert.True(saved.StatusCode == HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        await using var verify = fixture.CreateContext();
        var stored = await ReadAsync(verify, seed);
        Assert.Null(stored.RouteGeometry);
        Assert.Equal("Later Notes", stored.Notes);
        Assert.Equal(seed.AlternateId, stored.Waypoints.First().PlaceId);
    }

    /// <summary>Malformed originals, unsupported dialects and ambiguous new envelopes never fall back to complete saves.</summary>
    [PostgresTheory]
    [InlineData("contract")]
    [InlineData("token")]
    [InlineData("original")]
    [InlineData("unchanged-original")]
    [InlineData("legacy")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    public async Task StrictRouteDialect_RejectsWithoutWrites(string invalid)
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, seed.UserId);
        var original = await PublishedAsync(app, seed);
        var request = RouteRequest(original, "My Notes", 123);
        if (invalid == "contract") request["contract"] = "future-unsupported";
        if (invalid == "token") request["originalRouteToken"] = original.AggregateConcurrencyToken;
        if (invalid == "original") request.Remove("expected");
        if (invalid == "unchanged-original")
        {
            request["expected"]!["notesHtml"] = 5;
            request["changes"]!["notesHtml"] = null;
        }
        if (invalid == "legacy") request["notesHtml"] = "Unconditional";
        if (invalid == "unknown") request["changes"]!["route"] = null;
        var json = request.ToJsonString();
        if (invalid == "duplicate") json = json.Insert(1, "\"contract\":\"segment-route-notes-1\",");
        var before = await SnapshotAsync(seed);
        using var rejected = await web.PutAsync(WebUrl(seed), new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(before, await SnapshotAsync(seed));
    }
}
