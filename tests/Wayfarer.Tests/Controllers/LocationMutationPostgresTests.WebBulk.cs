using System.Net;
using AngleSharp.Html.Parser;
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
        var fault = new BeforeCommit();
        await WithHostAsync(credential, async client =>
        {
            await UseCookieAsync(client, owner);
            var observed = await ProtectedLocationRequest.DetailAsync(client, id);
            Assert.Equal("original", observed.GetProperty("record").GetProperty("notesHtml").GetString());
            using (var page = await client.GetAsync($"/User/Location/Edit/{id}"))
            {
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                Assert.True(page.Headers.CacheControl?.NoStore);
                var html = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync());
                var embedded = JsonSerializer.Deserialize<JsonElement>(html.QuerySelector("#locationForm")!.GetAttribute("data-location-detail")!);
                Assert.Equal(observed.GetProperty("record").GetProperty("eventUtc").GetString(), embedded.GetProperty("locationMutation").GetProperty("baseValues").GetProperty("eventUtc").GetProperty("value").GetString());
                Assert.EndsWith(".000000", html.QuerySelector("#LocalTimestamp")!.GetAttribute("value"));
            }
            var edit = await ProtectedLocationRequest.CreateAsync(client, id, new { notes = new { op = "set", html = "N1" },
                position = new { op = "set", latitude = 40.7, longitude = -74.0 }, manualAddress = new { op = "set", fields = new { place = "  My place  ", country = (string?)null } } }, "web-edit", observed);
            var result = await AssertStateAsync(await edit.SendAsync(client), 200, "applied");
            Assert.Equal("manual", result.GetProperty("addressOutcome").GetString());
            Assert.Equal("unavailable", result.GetProperty("providerOutcome").GetString());
            using var bulk = await client.PostAsync("/User/Location/BulkEditNotes", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["OperationScope"] = "bulk", ["Append"] = "true", ["Notes"] = " suffix", ["Place"] = "My place" }));
            Assert.True(bulk.StatusCode == HttpStatusCode.OK, (await bulk.Content.ReadAsStringAsync()).Split("HEADERS")[0]);
            await using var db = fixture.CreateContext(); var row = await db.Locations.SingleAsync(row => row.Id == id);
            Assert.Equal("N1 suffix", row.Notes); Assert.Equal("My place", row.Place); Assert.Null(row.Country);
            Assert.Contains("N1 SUFFIX", row.HistorySearchText);
            // Bulk has no single-operation receipt even at cardinality one.
            Assert.Single(await db.LocationMutationOperations.Where(row => row.UserId == owner).ToListAsync());
            var second = new Wayfarer.Models.Location { UserId = owner, TimeZoneId = "UTC", Coordinates = new(0, 0) { SRID = 4326 },
                Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow, Place = "My place", Notes = "other Notes" };
            db.Add(second); await db.SaveChangesAsync();
            var beforeRollback = await db.Locations.AsNoTracking().Where(item => item.UserId == owner).OrderBy(item => item.Id)
                .Select(item => new { item.Notes, item.HistorySearchText }).ToArrayAsync();
            fault.Armed = true;
            using (var failedBulk = await client.PostAsync("/User/Location/BulkEditNotes", new FormUrlEncodedContent(new Dictionary<string, string>
                { ["OperationScope"] = "bulk", ["Append"] = "true", ["Notes"] = " must roll back", ["Place"] = "My place" })))
                Assert.True((int)failedBulk.StatusCode >= 500);
            var afterRollback = await db.Locations.AsNoTracking().Where(item => item.UserId == owner).OrderBy(item => item.Id)
                .Select(item => new { item.Notes, item.HistorySearchText }).ToArrayAsync();
            Assert.Equal(JsonSerializer.Serialize(beforeRollback), JsonSerializer.Serialize(afterRollback));
            using var old = await client.PostAsync("/User/Location/Edit", new FormUrlEncodedContent(new Dictionary<string, string>
                { ["Id"] = id.ToString(), ["Notes"] = "unsafe", ["Latitude"] = "1", ["Longitude"] = "1" }));
            Assert.Equal(428, (int)old.StatusCode);
            using var bare = await client.PostAsJsonAsync("/api/location/bulk-delete", new { locationIds = new[] { id } });
            Assert.Equal(428, (int)bare.StatusCode);
        }, fault);
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

    /// <summary>Pre-action refusals use problem media types without receipts, effects or cookie-to-bearer fallback.</summary>
    [PostgresTheory]
    [InlineData("antiforgery", 403, "antiforgeryInvalid")]
    [InlineData("missing", 401, "mutationAuthenticationRequired")]
    [InlineData("inactiveCookie", 401, "mutationAuthenticationRequired")]
    [InlineData("missingRecovery", 401, "mutationAuthenticationRequired")]
    public async Task ProtectedPreActionErrorsUseProblemMediaType(string boundary, int status, string code)
    {
        var (owner, credential, id) = await SeedAsync();
        var (_, foreignCredential, _) = await SeedAsync();
        await WithHostAsync(credential, async client =>
        {
            var command = await ProtectedLocationRequest.CreateAsync(client, id, new { notes = new { op = "clear" } });
            if (boundary is "antiforgery" or "inactiveCookie")
            {
                await UseCookieAsync(client, owner);
                client.DefaultRequestHeaders.Remove("RequestVerificationToken");
                if (boundary == "inactiveCookie")
                {
                    await using var db = fixture.CreateContext();
                    await db.Users.Where(user => user.Id == owner).ExecuteUpdateAsync(set => set.SetProperty(user => user.IsActive, false));
                    client.DefaultRequestHeaders.Authorization = new("Bearer", foreignCredential);
                }
            }
            if (boundary.StartsWith("missing")) client.DefaultRequestHeaders.Authorization = null;
            using var response = boundary.EndsWith("Recovery") ? await command.RecoverAsync(client) : await command.SendAsync(client);
            Assert.Equal(status, (int)response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            Assert.Equal(code, body.GetProperty("code").GetString());
            Assert.False(body.TryGetProperty("state", out _));
            Assert.False(body.TryGetProperty("actionId", out _));
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            await using var verify = fixture.CreateContext();
            Assert.False(await verify.LocationMutationOperations.AnyAsync(row => row.UserId == owner));
            Assert.Equal("original", (await verify.Locations.SingleAsync(row => row.Id == id)).Notes);
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
