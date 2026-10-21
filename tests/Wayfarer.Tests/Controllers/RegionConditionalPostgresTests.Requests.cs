using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class RegionConditionalPostgresTests
{
    /// <summary>Malformed and nonselected fields cannot become partial writes on either authenticated route.</summary>
    [PostgresTheory]
    [InlineData(false, "{}")]
    [InlineData(false, "{\"expected\":{},\"changes\":{\"name\":\"New\"}}")]
    [InlineData(false, "{\"expected\":{},\"changes\":{\"notes\":null}}")]
    [InlineData(false, "{\"expected\":{},\"changes\":{\"displayOrder\":2}}")]
    [InlineData(false, "{\"expected\":{},\"changes\":{\"coverImage\":null}}")]
    [InlineData(false, "{\"expected\":{\"name\":\"Original Region\"},\"changes\":{\"name\":\"one\",\"name\":\"two\"}}")]
    [InlineData(false, "{\"expected\":{\"name\":\"Original Region\",\"name\":\"x\"},\"changes\":{\"name\":\"one\"}}")]
    [InlineData(true, "{\"expected\":{},\"changes\":{\"Name\":\"New\"}}")]
    [InlineData(true, "{\"expected\":{\"notesHtml\":null},\"changes\":{\"notesHtml\":\"<img src='data:image/png;base64,a'>\"}}")]
    [InlineData(true, "{\"expected\":{\"center\":null},\"changes\":{\"center\":{\"latitude\":1}}}")]
    [InlineData(true, "{\"expected\":{\"center\":null},\"changes\":{\"center\":{\"latitude\":91,\"longitude\":0}}}")]
    [InlineData(true, "{\"expected\":{\"coverImage\":null},\"changes\":{\"coverImage\":{\"rawUrl\":\"https://example.test/a\",\"rawUrl\":null}}}")]
    [InlineData(true, "{\"expected\":{\"coverImage\":null},\"changes\":{\"coverImage\":{\"rawUrl\":\"file:///a\"}}}")]
    [InlineData(true, "{\"expected\":{},\"changes\":{},\"changes\":{}}")]
    public async Task StrictRequest_RejectsWithoutDomainOrExternalEffects(bool webRoute, string json)
    {
        var region = await SeedAsync();
        var probe = new RegionProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var client = webRoute ? await WebAsync(app, region.UserId) : Bearer(app, region.UserId);
        using var response = await client.PatchAsync(webRoute ? $"/api/trips/{region.TripId}/editor/regions/{region.Id}/conditional"
            : $"/api/trips/{region.TripId}/conditional/regions/{region.Id}", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("mutation-request-invalid", await response.Content.ReadAsStringAsync());
        Assert.Empty(probe.Updates);
        Assert.Empty(_warmup.Invocations);
    }

    /// <summary>Reserved identity remains Notes-only on bearer and wholly noneditable on the current Web route.</summary>
    [PostgresFact]
    public async Task ReservedRegion_OnlyBearerNotesAreAdmitted()
    {
        var reserved = await SeedAsync(name: "Unassigned Places");
        var ordinary = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, reserved.UserId);
        using var bearer = Bearer(app, reserved.UserId);
        using var ordinaryBearer = Bearer(app, ordinary.UserId);
        using var allowed = await PatchAsync(bearer, reserved, false, new { notes = reserved.Notes }, new { notes = "Allowed" });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var deniedWeb = await PatchAsync(web, reserved, true, new { notesHtml = "Allowed" }, new { notesHtml = "No" });
        using var rename = await PatchAsync(bearer, reserved, false, new { name = reserved.Name }, new { name = "Rename" });
        using var adopt = await PatchAsync(ordinaryBearer, ordinary, false, new { name = ordinary.Name }, new { name = "  uNaSsIgNeD pLaCeS  " });
        Assert.Equal(HttpStatusCode.Forbidden, deniedWeb.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, rename.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, adopt.StatusCode);
        await using var verification = fixture.CreateContext();
        var stored = await verification.Regions.SingleAsync(r => r.Id == reserved.Id);
        Assert.Equal(("Unassigned Places", 0, "Allowed"), (stored.Name, stored.DisplayOrder, stored.Notes));
    }

    /// <summary>Cookie, role, antiforgery, incoming token and owned Trip/Region scope remain distinct boundaries.</summary>
    [PostgresFact]
    public async Task Routes_KeepExistingAuthorityAndConcealedScope()
    {
        var region = await SeedAsync();
        var other = await SeedAsync();
        var probe = new RegionProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, region.UserId);
        using var bearer = Bearer(app, region.UserId);
        using var foreignWeb = await WebAsync(app, other.UserId);
        using var foreignBearer = Bearer(app, other.UserId);
        using var noAntiforgery = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(noAntiforgery, app.Services, region.UserId);
        using var wrongRole = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(wrongRole, app.Services, region.UserId, "Manager");
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(wrongRole);
        using var cookieOnBearer = await PatchAsync(web, region, false, new { name = region.Name }, new { name = "No" });
        using var bearerOnCookie = await PatchAsync(bearer, region, true, new { name = region.Name }, new { name = "No" });
        using var missingAntiforgery = await PatchAsync(noAntiforgery, region, true, new { name = region.Name }, new { name = "No" });
        using var foreignCookie = await PatchAsync(foreignWeb, region, true, new { name = region.Name }, new { name = "No" });
        using var foreignToken = await PatchAsync(foreignBearer, region, false, new { name = region.Name }, new { name = "No" });
        Assert.Equal(HttpStatusCode.Unauthorized, cookieOnBearer.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, bearerOnCookie.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignCookie.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignToken.StatusCode);
        using var roleDenied = await PatchAsync(wrongRole, region, true, new { name = region.Name }, new { name = "No" });
        Assert.Equal(HttpStatusCode.Forbidden, roleDenied.StatusCode);
        // Correct owner but incorrect original parent cannot discover the Region via another Trip route.
        using var wrongParent = await bearer.PatchAsJsonAsync($"/api/trips/{other.TripId}/conditional/regions/{region.Id}",
            new { expected = new { name = region.Name }, changes = new { name = "No" } });
        Assert.Equal(HttpStatusCode.NotFound, wrongParent.StatusCode);
        Assert.Empty(probe.Updates);
        Assert.Empty(_warmup.Invocations);
    }
}
