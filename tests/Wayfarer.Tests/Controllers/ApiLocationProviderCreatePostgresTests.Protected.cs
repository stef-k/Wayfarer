using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Protected publication races reuse the actual admitted-provider host and controlled HTTP transport.</summary>
public sealed partial class ApiLocationProviderCreatePostgresTests
{
    /// <summary>A13: delayed original-create output cannot undo Set then value-equal Clear, even without the Clear reply.</summary>
    [PostgresFact(Timeout = 30000)]
    public async Task ProtectedManualSetClearClosesDelayedCreatePublication()
    {
        var protection = new EphemeralDataProtectionProvider();
        var owner = await SeedAsync("geoapify", protection);
        var key = Guid.NewGuid();
        using var handler = new ControlledProvider();
        await WithHostAsync(protection, handler, [], async client =>
        {
            handler.BeforeResponse = async _ =>
            {
                int id;
                await using (var db = fixture.CreateContext()) id = await db.Locations.Where(row => row.UserId == owner.UserId).Select(row => row.Id).SingleAsync();
                client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Token);
                var pending = await ProtectedLocationRequest.DetailAsync(client, id);
                Assert.Equal("pending", pending.GetProperty("locationMutation").GetProperty("originalCreateAddressPublication").GetString());
                var notes = await ProtectedLocationRequest.CreateAsync(client, id, new { notes = new { op = "set", html = "independent" } });
                await AppliedAsync(await notes.SendAsync(client));
                await WebCookieAsync(client, owner.UserId);
                var set = await ProtectedLocationRequest.CreateAsync(client, id, new { manualAddress = new { op = "set", fields = new { place = "Manual place" } } }, "web-edit");
                await AppliedAsync(await set.SendAsync(client));
                var clear = await ProtectedLocationRequest.CreateAsync(client, id, new { manualAddress = new { op = "set", fields = new { place = (string?)null } } }, "web-edit");
                // Discard the response after the real effect/receipt commit, then inspect from a new request/context.
                using (var lost = await clear.SendAsync(client)) Assert.Equal(HttpStatusCode.OK, lost.StatusCode);
                var fresh = await ProtectedLocationRequest.DetailAsync(client, id);
                Assert.Equal("closed", fresh.GetProperty("locationMutation").GetProperty("originalCreateAddressPublication").GetString());
                Assert.All(fresh.GetProperty("locationMutation").GetProperty("baseValues").GetProperty("address").GetProperty("value").EnumerateObject(), field => Assert.Equal(JsonValueKind.Null, field.Value.ValueKind));
                await AppliedAsync(await clear.RecoverAsync(client));
                Assert.Equal(1, handler.Calls); Assert.Equal(1, await AdmissionCountAsync(owner.UserId, "geoapify"));
            };
            using var original = Request("check-in", owner.Token, key);
            using var response = await client.SendAsync(original);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await using var verify = fixture.CreateContext();
            var row = await verify.Locations.SingleAsync(row => row.UserId == owner.UserId);
            Assert.Null(row.Place); Assert.Null(row.FullAddress); Assert.Null(row.OriginalCreateAddressToken);
            Assert.Equal("independent", row.Notes);
            Assert.Equal(1, handler.Calls); Assert.Equal(1, await AdmissionCountAsync(owner.UserId, "geoapify"));
        });
    }

    /// <summary>A3: final merge preserves independent Notes/time and rejects an overlapping address change without refunding contact.</summary>
    [PostgresTheory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectedCoordinatePublicationUsesFinalSelectedOriginals(bool overlap)
    {
        var protection = new EphemeralDataProtectionProvider();
        var owner = await SeedAsync("geoapify", protection);
        var id = await SeedLocationAsync(owner.UserId, Guid.NewGuid());
        using var handler = new ControlledProvider();
        await WithHostAsync(protection, handler, [], async client =>
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Token);
            var move = await ProtectedLocationRequest.CreateAsync(client, id, new { position = new { op = "set", latitude = 11.0, longitude = 21.0 } });
            handler.BeforeResponse = async _ =>
            {
                // A duplicate can see Pending but cannot contact or acquire the executor nonce.
                using (var duplicate = await move.SendAsync(client)) Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
                if (overlap)
                {
                    await WebCookieAsync(client, owner.UserId);
                    var manual = await ProtectedLocationRequest.CreateAsync(client, id, new { manualAddress = new { op = "set", fields = new { country = "New manual country" } } }, "web-edit");
                    await AppliedAsync(await manual.SendAsync(client));
                }
                else
                {
                    var independent = await ProtectedLocationRequest.CreateAsync(client, id, new { notes = new { op = "set", html = "new Notes" }, eventUtc = new { op = "set", value = "2026-10-29T08:00:00.123456Z" } });
                    await AppliedAsync(await independent.SendAsync(client));
                }
            };
            using var completed = await move.SendAsync(client);
            Assert.Equal(overlap ? HttpStatusCode.Conflict : HttpStatusCode.OK, completed.StatusCode);
            var result = JsonSerializer.Deserialize<JsonElement>(await completed.Content.ReadAsStringAsync());
            Assert.Equal(overlap ? "locationBaseStale" : "succeeded", result.GetProperty(overlap ? "code" : "providerOutcome").GetString());
            await using var verify = fixture.CreateContext();
            var row = await verify.Locations.SingleAsync(row => row.Id == id);
            Assert.Equal(overlap ? 10 : 11, row.Coordinates.Y);
            if (overlap) Assert.Equal("New manual country", row.Country);
            else
            {
                Assert.Equal("new Notes", row.Notes); Assert.Equal("Controlled address", row.FullAddress);
                Assert.Equal(1234560, row.LocalTimestamp.Ticks % TimeSpan.TicksPerSecond);
                Assert.Equal(8, row.LocalTimestamp.Hour);
            }
            await move.SendAsync(client); Assert.Equal(1, handler.Calls);
            Assert.Equal(1, await AdmissionCountAsync(owner.UserId, "geoapify"));
        });
    }

    /// <summary>A handled optional failure still completes valid movement once, clears obsolete data and retains consumed accounting.</summary>
    [PostgresFact(Timeout = 30000)]
    public async Task ProtectedCoordinateProviderFailureCommitsFallbackOnce()
    {
        var protection = new EphemeralDataProtectionProvider();
        var owner = await SeedAsync("geoapify", protection);
        var id = await SeedLocationAsync(owner.UserId, null);
        await using (var db = fixture.CreateContext())
            await db.Locations.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Country, "obsolete"));
        using var handler = new ControlledProvider { BeforeResponse = _ => throw new HttpRequestException("Controlled optional outage") };
        await WithHostAsync(protection, handler, [], async client =>
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Token);
            var request = await ProtectedLocationRequest.CreateAsync(client, id, new { position = new { op = "set", latitude = 11.0, longitude = 21.0 } });
            var receipt = await AppliedAsync(await request.SendAsync(client)); Assert.Equal("unavailable", receipt.GetProperty("providerOutcome").GetString());
            await AppliedAsync(await request.SendAsync(client));
            await using var verify = fixture.CreateContext(); var row = await verify.Locations.SingleAsync(row => row.Id == id);
            Assert.Equal(11, row.Coordinates.Y); Assert.Null(row.Country); Assert.Null(row.IdempotencyKey);
            Assert.Equal(1, handler.Calls); Assert.Equal(1, await AdmissionCountAsync(owner.UserId, "geoapify"));
        });
    }

    /// <summary>Late first executor output cannot publish after its fixed deadline, and consumed contact remains consumed.</summary>
    [PostgresFact(Timeout = 30000)]
    public async Task ProtectedLateExecutorCannotPublishAfterRecoveryDeadline()
    {
        var protection = new EphemeralDataProtectionProvider();
        var owner = await SeedAsync("geoapify", protection);
        var id = await SeedLocationAsync(owner.UserId, Guid.NewGuid());
        var clock = new ControlledDatabaseClock();
        using var handler = new ControlledProvider { BeforeResponse = _ => { clock.Now = clock.Now.AddSeconds(121); return Task.CompletedTask; } };
        await WithHostAsync(protection, handler, [clock], async client =>
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Token);
            var request = await ProtectedLocationRequest.CreateAsync(client, id, new { position = new { op = "set", latitude = 11.0, longitude = 21.0 }, notes = new { op = "set", html = "must not publish" } });
            using var response = await request.SendAsync(client); Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var terminal = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            Assert.Equal("operationExecutionExpired", terminal.GetProperty("code").GetString()); Assert.False(terminal.GetProperty("futureEffects").GetBoolean());
            using var recovered = await request.RecoverAsync(client);
            Assert.Equal(terminal.GetRawText(), JsonSerializer.Deserialize<JsonElement>(await recovered.Content.ReadAsStringAsync()).GetRawText());
            await using var verify = fixture.CreateContext(); var row = await verify.Locations.SingleAsync(row => row.Id == id);
            Assert.Equal(10, row.Coordinates.Y); Assert.Null(row.FullAddress); Assert.NotEqual("must not publish", row.Notes);
            Assert.Equal(1, handler.Calls); Assert.Equal(1, await AdmissionCountAsync(owner.UserId, "geoapify"));
        });
    }

    /// <summary>Revoked provider output is optional; a valid coordinate effect still commits with a cleared tuple.</summary>
    [PostgresTheory(Timeout = 30000)]
    [InlineData("geoapify")]
    [InlineData("mapbox")]
    public async Task ProtectedRevokedProviderOutputFallsBackWithoutRefund(string provider)
    {
        var protection = new EphemeralDataProtectionProvider();
        var owner = await SeedAsync(provider, protection);
        var id = await SeedLocationAsync(owner.UserId, Guid.NewGuid());
        using var handler = new ControlledProvider { BeforeResponse = async _ =>
        {
            await using var edit = fixture.CreateContext();
            var selection = await edit.PersonalLocationProviderSelections.SingleAsync(row => row.UserId == owner.UserId);
            selection.Select(Wayfarer.Models.LocationProviders.PersonalProviderCapability.Geocoding, null);
            await edit.SaveChangesAsync();
        } };
        await WithHostAsync(protection, handler, [], async client =>
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Token);
            var request = await ProtectedLocationRequest.CreateAsync(client, id, new { position = new { op = "set", latitude = 11.0, longitude = 21.0 } });
            var terminal = await AppliedAsync(await request.SendAsync(client)); Assert.Equal("unavailable", terminal.GetProperty("providerOutcome").GetString());
            await using var verify = fixture.CreateContext(); var row = await verify.Locations.SingleAsync(row => row.Id == id);
            Assert.Equal(11, row.Coordinates.Y); Assert.Null(row.FullAddress);
            Assert.Equal(1, handler.Calls); Assert.Equal(1, await AdmissionCountAsync(owner.UserId, provider));
        });
    }

    /// <summary>Authentic cookie/antiforgery, including cookie priority over an already supplied bearer.</summary>
    private async Task WebCookieAsync(HttpClient client, string owner)
    {
        var cookie = OwnerCookie(owner);
        client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Add("Cookie", cookie);
        using var tokens = await client.GetAsync("/test-antiforgery"); tokens.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", cookie + "; " + tokens.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(".AspNetCore.Antiforgery.")).Split(';')[0]);
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", await tokens.Content.ReadAsStringAsync());
    }

    /// <summary>Production receipts, rather than ordinary success booleans, prove each action.</summary>
    private static async Task<JsonElement> AppliedAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
            var body = JsonSerializer.Deserialize<JsonElement>(text); Assert.Equal("applied", body.GetProperty("state").GetString());
            return body;
        }
    }
}
