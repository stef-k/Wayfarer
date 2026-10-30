using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Current Web/API authorization and deliberate bulk policy at real existing routes.</summary>
public sealed partial class LocationMutationPostgresTests
{
    /// <summary>Cookie-only manual intent wins optional fallback; bulk append reads N1 under final locks after an N0 preview.</summary>
    [PostgresFact]
    public async Task WebManualIntentAndBulkAppendUseFinalLockedContent()
    {
        var (owner, credential, id) = await SeedAsync();
        await WithHostAsync(credential, async client =>
        {
            await UseCookieAsync(client, owner);
            var observed = await ProtectedLocationRequest.DetailAsync(client, id);
            Assert.Equal("original", observed.GetProperty("record").GetProperty("notesHtml").GetString());
            var edit = await ProtectedLocationRequest.CreateAsync(client, id, new { notes = new { op = "set", html = "N1" },
                position = new { op = "set", latitude = 40.7, longitude = -74.0 }, manualAddress = new { op = "set", fields = new { place = "  My place  ", country = (string?)null } } }, "web-edit", observed);
            var result = await AssertStateAsync(await edit.SendAsync(client), 200, "applied");
            Assert.Equal("manual", result.GetProperty("addressOutcome").GetString());
            Assert.Equal("unavailable", result.GetProperty("providerOutcome").GetString());
            using var bulk = await client.PostAsync("/User/Location/BulkEditNotes", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["OperationScope"] = "bulk", ["Append"] = "true", ["Notes"] = " suffix", ["Place"] = "My place" }));
            Assert.Equal(HttpStatusCode.OK, bulk.StatusCode);
            await using var db = fixture.CreateContext(); var row = await db.Locations.SingleAsync(row => row.Id == id);
            Assert.Equal("N1 suffix", row.Notes); Assert.Equal("My place", row.Place); Assert.Null(row.Country);
            Assert.Contains("N1 suffix", row.HistorySearchText);
            // Bulk has no single-operation receipt even at cardinality one.
            Assert.Single(await db.LocationMutationOperations.Where(row => row.UserId == owner).ToListAsync());
            using var old = await client.PostAsync("/User/Location/Edit", new FormUrlEncodedContent(new Dictionary<string, string>
                { ["Id"] = id.ToString(), ["Notes"] = "unsafe", ["Latitude"] = "1", ["Longitude"] = "1" }));
            Assert.Equal(428, (int)old.StatusCode);
            using var bare = await client.PostAsJsonAsync("/api/location/bulk-delete", new { locationIds = new[] { id } });
            Assert.Equal(428, (int)bare.StatusCode);
        });
    }

    /// <summary>Protected cookie commands require antiforgery; bearer may not select Web fields or another owner's evidence.</summary>
    [PostgresFact]
    public async Task ProtectedRoutesEnforceOwnerCookieAndRetirement()
    {
        var (owner, credential, id) = await SeedAsync();
        var (_, foreignCredential, _) = await SeedAsync();
        await WithHostAsync(credential, async client =>
        {
            var command = await ProtectedLocationRequest.CreateAsync(client, id, new { notes = new { op = "clear" } });
            client.DefaultRequestHeaders.Authorization = new("Bearer", foreignCredential);
            using var foreign = await command.SendAsync(client); Assert.Equal(422, (int)foreign.StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", credential);
            await UseCookieAsync(client, owner);
            client.DefaultRequestHeaders.Remove("RequestVerificationToken");
            using var csrf = await command.SendAsync(client); Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
            client.DefaultRequestHeaders.Remove("Cookie");
            using var oldDelete = await client.DeleteAsync($"/api/location/{id}"); Assert.Equal(428, (int)oldDelete.StatusCode);
            using var oldPut = await client.PutAsJsonAsync($"/api/location/{id}", new { notes = "unsafe" }); Assert.Equal(428, (int)oldPut.StatusCode);
            await using var verify = fixture.CreateContext();
            Assert.Equal("original", (await verify.Locations.SingleAsync(row => row.Id == id)).Notes);
            Assert.False(await verify.LocationMutationOperations.AnyAsync(row => row.UserId == owner));
        });
    }

    /// <summary>Cookie and antiforgery helpers are the same production host seam used by existing routed tests.</summary>
    private async Task UseCookieAsync(HttpClient client, string owner)
    {
        var cookie = IdentityRouteHost.Cookie(hostServices, owner);
        client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Add("Cookie", cookie);
        using var response = await client.GetAsync("/test-antiforgery"); response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Add("Cookie", cookie + "; " +
            response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(".AspNetCore.Antiforgery.")).Split(';')[0]);
        client.DefaultRequestHeaders.Remove("RequestVerificationToken"); client.DefaultRequestHeaders.Add("RequestVerificationToken", await response.Content.ReadAsStringAsync());
    }
}
