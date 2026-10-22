using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class PlaceConditionalPostgresTests
{
    /// <summary>Strict wire intent rejects malformed, duplicate, unknown, nested and forbidden fields without effects.</summary>
    [PostgresTheory]
    [InlineData(false, """{}""")]
    [InlineData(false, """{"expected":{},"changes":{"name":"New"}}""")]
    [InlineData(false, """{"expected":{"regionId":"PARENT"},"changes":{}}""")]
    [InlineData(false, """{"expected":{"regionId":"PARENT"},"changes":{"notes":null}}""")]
    [InlineData(true, """{"expected":{"regionId":"PARENT","name":"Original Place"},"changes":{"name":" "}}""")]
    [InlineData(false, """{"expected":{"regionId":"PARENT","iconName":null},"changes":{"iconName":null}}""")]
    [InlineData(true, """{"expected":{"regionId":"PARENT","markerColor":null},"changes":{"markerColor":"invalid"}}""")]
    [InlineData(false, """{"expected":{"regionId":"PARENT","iconName":null},"changes":{"iconName":"invalid"}}""")]
    [InlineData(false, """{"expected":{"regionId":"PARENT","notes":null},"changes":{"notes":{"html":"No"}}}""")]
    [InlineData(true, """{"expected":{"regionId":"PARENT","notesHtml":null},"changes":{"notesHtml":"<img src=\"data:image/png;base64,a\">"}}""")]
    [InlineData(true, """{"expected":{"regionId":"PARENT","Name":"Original Place"},"changes":{"name":"New"}}""")]
    [InlineData(false, """{"expected":{"regionId":"PARENT","name":"Original Place"},"changes":{"name":"New","name":"Other"}}""")]
    [InlineData(true, """{"expected":{"regionId":"PARENT","regionId":"PARENT"},"changes":{"name":"New"}}""")]
    [InlineData(false, """{"expected":{"regionId":"PARENT","name":null},"changes":{"name":"New"}}""")]
    [InlineData(true, """{"expected":{"regionId":"PARENT","notesHtml":null},"changes":{"notes":"New"}}""")]
    [InlineData(false, """{"expected":{"regionId":"PARENT","name":"Original Place"},"changes":{"name":"New"},"extra":true}""")]
    [InlineData(false, """{"expected":{"regionId":"PARENT","name":"Original Place"},"changes":{"name":"New"},"changes":{}}""")]
    [InlineData(false, """{"expected":{"regionId":"PARENT"},"changes":{"regionId":"PARENT"}}""")]
    [InlineData(false, """{"expected":{"regionId":"PARENT"},"changes":{"address":"No"}}""")]
    [InlineData(false, """{"expected":{"regionId":"PARENT"},"changes":{"location":{}}}""")]
    [InlineData(false, """{"expected":{"regionId":"PARENT"},"changes":{"displayOrder":1}}""")]
    [InlineData(false, """{"expected":{"regionId":"PARENT"},"changes":{"reverseGeocode":true}}""")]
    [InlineData(false, """{"expected":{"regionId":"PARENT"},"changes":{"latitude":10}}""")]
    [InlineData(false, """{"expected":{"regionId":"PARENT"},"changes":{"route":{}}}""")]
    [InlineData(false, """{"expected":{"regionId":"PARENT"},"changes":{"resolvedFeatureName":"No"}}""")]
    [InlineData(true, """{"expected":{"regionId":"PARENT","name":"Original Place"},"changes":{"name":"New\u0000name"}}""")]
    [InlineData(false, """{"expected":{"regionId":"PARENT","name":"Original Place"},"changes":{"name":"New\uD800name"}}""")]
    public async Task InvalidRequests_WriteNothing(bool web, string json)
    {
        var place = await SeedAsync();
        var probe = new PlaceProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var client = web ? await WebAsync(app, place.UserId) : Bearer(app, place.UserId);
        using var response = await client.PatchAsync(web ? $"/api/trips/{place.Region.TripId}/editor/places/{place.Id}/conditional"
            : $"/api/trips/{place.Region.TripId}/conditional/places/{place.Id}",
            new StringContent(json.Replace("PARENT", place.RegionId.ToString()), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("mutation-request-invalid", await response.Content.ReadAsStringAsync());
        Assert.Empty(probe.Updates);
        Assert.Empty(_warmup.Invocations);
        await using var verify = fixture.CreateContext();
        var stored = await verify.Places.FindAsync(place.Id);
        Assert.Equal((place.Name, place.Notes, place.IconName, place.MarkerColor), (stored!.Name, stored.Notes, stored.IconName, stored.MarkerColor));
        Assert.Equal(place.Region.Trip.UpdatedAt, (await verify.Trips.FindAsync(place.Region.TripId))!.UpdatedAt);
    }

    /// <summary>#848 keep disposition invalidates the old parent but retained Places in Unassigned remain editable.</summary>
    [PostgresFact]
    public async Task KeptPlace_RejectsOldParent_AndAllowsExplicitlyRefreshedUnassignedBase()
    {
        var place = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, place.Region.UserId);
        using var bearer = Bearer(app, place.Region.UserId);
        var route = $"/api/trips/{place.Region.TripId}/editor/regions/{place.RegionId}";
        using var previewResponse = await web.PostAsJsonAsync(route + "/deletion-preview", new { places = "keep", areas = "keep" });
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<RegionDeletionPreviewDto>())!;
        using var deletion = await web.PostAsJsonAsync(route + "/deletion", new { places = "keep", areas = "keep", confirmationToken = preview.Confirmation.Token });
        Assert.Equal(HttpStatusCode.OK, deletion.StatusCode);
        using var staleWeb = await PatchAsync(web, place, true, new { notesHtml = place.Notes }, new { notesHtml = "Old scope" });
        using var staleBearer = await PatchAsync(bearer, place, false, new { notes = place.Notes }, new { notes = "Old scope" });
        Assert.Equal(HttpStatusCode.Conflict, staleWeb.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, staleBearer.StatusCode);
        await using var verify = fixture.CreateContext();
        var current = await verify.Places.Include(a => a.Region).SingleAsync(a => a.Id == place.Id);
        Assert.NotEqual(place.RegionId, current.RegionId);
        Assert.Equal("Unassigned Places", current.Region.Name);
        Assert.Equal(place.Location!.AsText(), current.Location!.AsText());
        using var refreshedWeb = await PatchAsync(web, current, true, new { notesHtml = place.Notes }, new { notesHtml = "Web kept notes" });
        Assert.Equal(HttpStatusCode.OK, refreshedWeb.StatusCode);
        using var refreshedBearer = await PatchAsync(bearer, current, false, new { notes = "Web kept notes" }, new { notes = "Mobile kept notes" });
        Assert.Equal(HttpStatusCode.OK, refreshedBearer.StatusCode);
        Assert.Equal("Mobile kept notes", await verify.Places.AsNoTracking().Where(a => a.Id == place.Id).Select(a => a.Notes).SingleAsync());
    }

    /// <summary>Preserves actual incoming bearer, cookie role, antiforgery, and concealed Place/parent authorization.</summary>
    [PostgresFact]
    public async Task Routes_EnforceDistinctAuthorityAndConcealedScope()
    {
        var place = await SeedAsync();
        var other = await SeedAsync();
        var probe = new PlaceProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, place.Region.UserId);
        using var bearer = Bearer(app, place.Region.UserId);
        using var foreign = await WebAsync(app, other.Region.UserId);
        using var foreignBearer = Bearer(app, other.Region.UserId);
        using var noCsrf = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(noCsrf, app.Services, place.Region.UserId);
        using var wrongRole = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(wrongRole, app.Services, place.Region.UserId, "Manager");
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(wrongRole);
        using var cookieOnBearer = await PatchAsync(web, place, false, new { notes = place.Notes }, new { notes = "No" });
        using var bearerOnCookie = await PatchAsync(bearer, place, true, new { notesHtml = place.Notes }, new { notesHtml = "No" });
        using var missingCsrf = await PatchAsync(noCsrf, place, true, new { notesHtml = place.Notes }, new { notesHtml = "No" });
        using var role = await PatchAsync(wrongRole, place, true, new { notesHtml = place.Notes }, new { notesHtml = "No" });
        using var otherWeb = await PatchAsync(foreign, place, true, new { notesHtml = place.Notes }, new { notesHtml = "No" });
        using var otherToken = await PatchAsync(foreignBearer, place, false, new { notes = place.Notes }, new { notes = "No" });
        Assert.Equal(HttpStatusCode.Unauthorized, cookieOnBearer.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, bearerOnCookie.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, role.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherWeb.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherToken.StatusCode);
        using var wrongTrip = await bearer.PatchAsJsonAsync($"/api/trips/{other.Region.TripId}/conditional/places/{place.Id}", new
        { expected = new { regionId = place.RegionId, notes = place.Notes }, changes = new { notes = "No" } });
        Assert.Equal(HttpStatusCode.NotFound, wrongTrip.StatusCode);
        place.Id = Guid.NewGuid();
        using var missing = await PatchAsync(bearer, place, false, new { notes = place.Notes }, new { notes = "No" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Empty(probe.Updates);
        Assert.Empty(_warmup.Invocations);
    }
}
