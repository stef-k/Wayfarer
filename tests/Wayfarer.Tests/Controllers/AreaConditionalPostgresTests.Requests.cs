using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class AreaConditionalPostgresTests
{
    /// <summary>#848 keep disposition invalidates the old parent but retained Areas in Unassigned remain editable.</summary>
    [PostgresFact]
    public async Task KeptArea_RejectsOldParent_AndAllowsExplicitlyRefreshedUnassignedBase()
    {
        var area = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, area.Region.UserId);
        using var bearer = Bearer(app, area.Region.UserId);
        var route = $"/api/trips/{area.Region.TripId}/editor/regions/{area.RegionId}";
        using var previewResponse = await web.PostAsJsonAsync(route + "/deletion-preview", new { places = "keep", areas = "keep" });
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<RegionDeletionPreviewDto>())!;
        using var deletion = await web.PostAsJsonAsync(route + "/deletion", new { places = "keep", areas = "keep", confirmationToken = preview.Confirmation.Token });
        Assert.Equal(HttpStatusCode.OK, deletion.StatusCode);
        using var staleWeb = await PatchAsync(web, area, true, new { notesHtml = area.Notes }, new { notesHtml = "Old scope" });
        using var staleBearer = await PatchAsync(bearer, area, false, new { notes = area.Notes }, new { notes = "Old scope" });
        Assert.Equal(HttpStatusCode.Conflict, staleWeb.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, staleBearer.StatusCode);
        await using var verify = fixture.CreateContext();
        var current = await verify.Areas.Include(a => a.Region).SingleAsync(a => a.Id == area.Id);
        Assert.NotEqual(area.RegionId, current.RegionId);
        Assert.Equal("Unassigned Places", current.Region.Name);
        AssertPolygon(area.Geometry, current.Geometry);
        using var refreshedWeb = await PatchAsync(web, current, true, new { notesHtml = area.Notes }, new { notesHtml = "Web kept notes" });
        Assert.Equal(HttpStatusCode.OK, refreshedWeb.StatusCode);
        using var refreshedBearer = await PatchAsync(bearer, current, false, new { notes = "Web kept notes" }, new { notes = "Mobile kept notes" });
        Assert.Equal(HttpStatusCode.OK, refreshedBearer.StatusCode);
        Assert.Equal("Mobile kept notes", await verify.Areas.AsNoTracking().Where(a => a.Id == area.Id).Select(a => a.Notes).SingleAsync());
    }

    /// <summary>Rejects malformed, duplicate and unselected intent before any Area/Trip/cache effects.</summary>
    [PostgresTheory]
    [InlineData(false, "{}")]
    [InlineData(false, "{\"expected\":{},\"changes\":{\"notes\":\"New\"}}")]
    [InlineData(false, "{\"expected\":{\"regionId\":\"PARENT\"},\"changes\":{\"notes\":\"New\"}}")]
    [InlineData(false, "{\"expected\":{\"regionId\":\"PARENT\"},\"changes\":{\"notes\":null}}")]
    [InlineData(false, "{\"expected\":{\"regionId\":\"PARENT\",\"notes\":false},\"changes\":{\"notes\":\"New\"}}")]
    [InlineData(false, "{\"expected\":{\"regionId\":\"PARENT\"},\"changes\":{\"name\":\"No\"}}")]
    [InlineData(false, "{\"expected\":{\"regionId\":\"PARENT\"},\"changes\":{\"fillHex\":\"#112233\"}}")]
    [InlineData(false, "{\"expected\":{\"regionId\":\"PARENT\"},\"changes\":{\"geometry\":null}}")]
    [InlineData(false, "{\"expected\":{\"regionId\":\"PARENT\"},\"changes\":{\"displayOrder\":2}}")]
    [InlineData(false, "{\"expected\":{\"regionId\":\"PARENT\"},\"changes\":{\"regionId\":\"PARENT\"}}")]
    [InlineData(false, "{\"expected\":{\"regionId\":\"PARENT\",\"notes\":null},\"changes\":{\"notes\":\"one\",\"notes\":\"two\"}}")]
    [InlineData(true, "{\"expected\":{\"regionId\":\"PARENT\",\"regionId\":\"PARENT\"},\"changes\":{\"name\":\"New\"}}")]
    [InlineData(true, "{\"expected\":{\"regionId\":\"PARENT\",\"notesHtml\":null},\"changes\":{\"notesHtml\":\"<img src='data:image/png;base64,a'>\"}}")]
    [InlineData(true, "{\"expected\":{\"regionId\":\"PARENT\",\"name\":null},\"changes\":{\"name\":\"New\"}}")]
    [InlineData(true, "{\"expected\":{\"regionId\":\"PARENT\",\"fillHex\":null},\"changes\":{\"fillHex\":\"red\"}}")]
    [InlineData(true, "{\"expected\":{\"regionId\":\"PARENT\",\"geometry\":{}},\"changes\":{\"geometry\":{}}}")]
    [InlineData(true, "{\"expected\":{\"regionId\":\"PARENT\"},\"changes\":{},\"changes\":{}}")]
    public async Task InvalidRequests_WriteNothing(bool web, string json)
    {
        var area = await SeedAsync();
        var probe = new AreaProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var client = web ? await WebAsync(app, area.Region.UserId) : Bearer(app, area.Region.UserId);
        using var response = await client.PatchAsync(web ? $"/api/trips/{area.Region.TripId}/editor/areas/{area.Id}/conditional"
            : $"/api/trips/{area.Region.TripId}/conditional/areas/{area.Id}",
            new StringContent(json.Replace("PARENT", area.RegionId.ToString()), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("mutation-request-invalid", await response.Content.ReadAsStringAsync());
        Assert.Empty(probe.Updates);
        Assert.Empty(_warmup.Invocations);
    }

    /// <summary>Desired geometry uses the existing WGS84 pairs/closed-ring validation plus strict object boundaries.</summary>
    [PostgresTheory]
    [InlineData("{\"type\":\"Polygon\",\"coordinates\":[]}")]
    [InlineData("{\"type\":\"Polygon\",\"type\":\"Polygon\",\"coordinates\":[]}")]
    [InlineData("{\"type\":\"Polygon\",\"srid\":4326,\"coordinates\":[]}")]
    [InlineData("{\"type\":\"Polygon\",\"coordinates\":[[[181,1],[2,1],[2,2],[181,1]]]}")]
    [InlineData("{\"type\":\"Polygon\",\"coordinates\":[[[1,91],[2,1],[2,2],[1,91]]]}")]
    [InlineData("{\"type\":\"Polygon\",\"coordinates\":[[[1,1,0],[2,1],[2,2],[1,1,0]]]}")]
    [InlineData("{\"type\":\"Polygon\",\"coordinates\":[[[1,1],[2,1],[2,2],[1,2]]]}")]
    public async Task InvalidPolygonReplacement_IsUnapplied(string json)
    {
        var area = await SeedAsync();
        var probe = new AreaProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, area.Region.UserId);
        using var geometry = System.Text.Json.JsonDocument.Parse(json);
        using var response = await PatchAsync(web, area, true, new { geometry = Fingerprint(area) }, new { geometry = geometry.RootElement });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(probe.Updates);
        Assert.Empty(_warmup.Invocations);
    }

    /// <summary>Legacy geometry-only PUT writes no Notes; its conflicting Geometry replacements remain unconditional.</summary>
    [PostgresFact]
    public async Task LegacyGeometryOnly_PreservesNotesAndScope_ButCanOverwriteGeometry()
    {
        var area = await SeedAsync();
        var probe = new AreaProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, area.Region.UserId);
        using var bearer = Bearer(app, area.Region.UserId);
        using var notes = await PatchAsync(bearer, area, false, new { notes = area.Notes }, new { notes = "New Notes" });
        Assert.Equal(HttpStatusCode.OK, notes.StatusCode);
        var writes = probe.Updates.Count;
        using var geometry = await web.PutAsJsonAsync($"/api/trips/{area.Region.TripId}/editor/areas/{area.Id}/geometry", new { geometry = Geometry(Polygon(20)) });
        Assert.Equal(HttpStatusCode.OK, geometry.StatusCode);
        var sql = probe.Updates.Skip(writes).Single(s => s.Contains("UPDATE \"Areas\""));
        Assert.Contains("\"Geometry\" =", sql);
        Assert.DoesNotContain("\"Notes\" =", sql);
        using var wrongTrip = await web.PutAsJsonAsync($"/api/trips/{Guid.NewGuid()}/editor/areas/{area.Id}/geometry", new { geometry = Geometry(area) });
        Assert.Equal(HttpStatusCode.NotFound, wrongTrip.StatusCode);
        using var staleProtected = await PatchAsync(web, area, true, new { geometry = Fingerprint(area) }, new { geometry = Geometry(Polygon(30)) });
        Assert.Equal(HttpStatusCode.Conflict, staleProtected.StatusCode);
        using var legacyOverwrite = await web.PutAsJsonAsync($"/api/trips/{area.Region.TripId}/editor/areas/{area.Id}/geometry", new { geometry = Geometry(area) });
        Assert.Equal(HttpStatusCode.OK, legacyOverwrite.StatusCode);
        await using var verify = fixture.CreateContext();
        var stored = await verify.Areas.SingleAsync(a => a.Id == area.Id);
        Assert.Equal("New Notes", stored.Notes);
        AssertPolygon(area.Geometry, stored.Geometry);
        output.WriteLine("Legacy geometry-only PUT: Geometry + Trip UpdatedAt only; preserved Notes, still unconditional for Geometry conflicts.");
        output.WriteLine(sql);
    }

    /// <summary>Preserves actual incoming bearer, cookie role, antiforgery, and concealed Area/parent authorization.</summary>
    [PostgresFact]
    public async Task Routes_EnforceDistinctAuthorityAndConcealedScope()
    {
        var area = await SeedAsync();
        var other = await SeedAsync();
        var probe = new AreaProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, area.Region.UserId);
        using var bearer = Bearer(app, area.Region.UserId);
        using var foreign = await WebAsync(app, other.Region.UserId);
        using var foreignBearer = Bearer(app, other.Region.UserId);
        using var noCsrf = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(noCsrf, app.Services, area.Region.UserId);
        using var wrongRole = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(wrongRole, app.Services, area.Region.UserId, "Manager");
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(wrongRole);
        using var cookieOnBearer = await PatchAsync(web, area, false, new { notes = area.Notes }, new { notes = "No" });
        using var bearerOnCookie = await PatchAsync(bearer, area, true, new { notesHtml = area.Notes }, new { notesHtml = "No" });
        using var missingCsrf = await PatchAsync(noCsrf, area, true, new { notesHtml = area.Notes }, new { notesHtml = "No" });
        using var role = await PatchAsync(wrongRole, area, true, new { notesHtml = area.Notes }, new { notesHtml = "No" });
        using var otherWeb = await PatchAsync(foreign, area, true, new { notesHtml = area.Notes }, new { notesHtml = "No" });
        using var otherToken = await PatchAsync(foreignBearer, area, false, new { notes = area.Notes }, new { notes = "No" });
        Assert.Equal(HttpStatusCode.Unauthorized, cookieOnBearer.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, bearerOnCookie.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, role.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherWeb.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherToken.StatusCode);
        using var wrongTrip = await bearer.PatchAsJsonAsync($"/api/trips/{other.Region.TripId}/conditional/areas/{area.Id}", new
        { expected = new { regionId = area.RegionId, notes = area.Notes }, changes = new { notes = "No" } });
        Assert.Equal(HttpStatusCode.NotFound, wrongTrip.StatusCode);
        area.Id = Guid.NewGuid();
        using var missing = await PatchAsync(bearer, area, false, new { notes = area.Notes }, new { notes = "No" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Empty(probe.Updates);
        Assert.Empty(_warmup.Invocations);
    }
}
