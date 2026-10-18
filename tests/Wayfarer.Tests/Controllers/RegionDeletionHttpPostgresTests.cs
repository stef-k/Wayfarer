using Microsoft.AspNetCore.TestHost;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Models;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Routes real cookie/antiforgery and bearer requests through production MVC against PostgreSQL.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class RegionDeletionHttpPostgresTests(PostgresImportTestFixture fixture) : TestBase
{
    /// <summary>Each adapter returns the same authoritative lifecycle envelope; preview makes no writes.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothAdapters_NontrivialConfirmedOperation(bool cookie)
    {
        var g = await RegionDeletionTestGraph.SeedAsync(fixture, false);
        await using var db = fixture.CreateContext();
        db.ApiTokens.Add(new ApiToken { User = await db.Users.SingleAsync(u => u.Id == g.UserId), UserId = g.UserId, Token = "wf_848_" + g.UserId, Name = "848 fixture" });
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        if (cookie) { UserConnectionTokenHttpTests.Cookie(client, app.Services, g.UserId); await UserConnectionTokenHttpTests.AddAntiforgeryAsync(client); }
        else client.DefaultRequestHeaders.Authorization = new("Bearer", "wf_848_" + g.UserId);
        var route = $"/api/trips/{g.TripId}/{(cookie ? "editor/" : "")}regions/{g.SourceId}";
        using var response = await client.PostAsJsonAsync(route + "/deletion-preview", new { places = "keep", areas = "keep" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var preview = (await response.Content.ReadFromJsonAsync<RegionDeletionPreviewDto>())!;
        Assert.Equal("create", preview.Destination.Mode);
        Assert.False(await db.Regions.AnyAsync(r => r.Id == preview.Destination.RegionId));
        using var mutation = await client.PostAsJsonAsync(route + "/deletion", new { places = "keep", areas = "keep", confirmationToken = preview.Confirmation.Token });
        Assert.Equal(HttpStatusCode.OK, mutation.StatusCode);
        var result = (await mutation.Content.ReadFromJsonAsync<EditorMutationResult<RegionDeletionResultDto>>())!;
        Assert.True(result.Success); Assert.Equal(g.PlaceIds.Order(), result.Data.MovedPlaceIds); Assert.Equal(g.AreaIds.Order(), result.Data.MovedAreaIds);
        Assert.NotEmpty(result.Affected.Places); Assert.NotEmpty(result.Affected.Areas); Assert.Empty(result.DeletedIds.Segments);
    }

    /// <summary>Cookie-only bearer requests, missing antiforgery, wrong owner and role never acquire authority.</summary>
    [PostgresFact]
    public async Task AuthorityAndStrictRequestBoundaries()
    {
        var g = await RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(client, app.Services, g.UserId);
        var route = $"/api/trips/{g.TripId}/editor/regions/{g.SourceId}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/api/trips/{g.TripId}/regions/{g.SourceId}/deletion-preview", new { places = "keep", areas = "keep" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route + "/deletion-preview", new { places = "keep", areas = "keep" })).StatusCode);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(client);
        foreach (var body in new[] { "{}", "{\"places\":\"keep\"}", "{\"places\":0,\"areas\":\"keep\"}", "{\"places\":\"keep\",\"areas\":\"keep\",\"extra\":1}" })
        {
            using var invalid = await client.PostAsync(route + "/deletion-preview", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("region-deletion-request-invalid", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        using var missing = await client.PostAsJsonAsync(route + "/deletion", new { places = "delete", areas = "delete" });
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Equal("region-deletion-confirmation-required", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        using var tampered = await client.PostAsJsonAsync(route + "/deletion", new { places = "keep", areas = "keep", confirmationToken = "tampered" });
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
        var other = await fixture.CreateUserAsync();
        client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        UserConnectionTokenHttpTests.Cookie(client, app.Services, other.Id); await UserConnectionTokenHttpTests.AddAntiforgeryAsync(client);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(route + "/deletion-preview", new { places = "keep", areas = "keep" })).StatusCode);
        client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        UserConnectionTokenHttpTests.Cookie(client, app.Services, g.UserId, "Admin"); await UserConnectionTokenHttpTests.AddAntiforgeryAsync(client);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(route + "/deletion-preview", new { places = "keep", areas = "keep" })).StatusCode);
        Assert.True(await db.Regions.AnyAsync(r => r.Id == g.SourceId));
    }
}
