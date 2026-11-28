using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services.TripMetadataReceipts;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class TripMetadataConditionalPostgresTests
{
    /// <summary>Recover committed Region fields after losing the response and removing the live target.</summary>
    [PostgresFact]
    public async Task StructureReceipt_RecoversAndReplaysAfterRegionRemoval()
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        var region = await SeedStructureRegionAsync(db, trip);
        var dropReply = true;
        await using var app = await StartAsync(db, middleware: app => app.Use(async (context, next) =>
        {
            if (!dropReply || context.Request.Method != "PATCH") { await next(); return; }
            dropReply = false;
            var transport = context.Response.Body;
            await using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            try { await next(); }
            finally { context.Response.Body = transport; }
            throw new IOException("Controlled lost response after typed receipt commit");
        }));
        using var web = await WebAsync(app, trip.UserId);
        var route = $"/api/trips/{trip.Id}/editor/regions/{region.Id}/conditional/receipt";
        var body = StructureBody(region.Name);
        var permit = await IssueStructureAsync(web, trip, region, route, body);
        using var lost = await TripMetadataReceiptHttp.SendAsync(web, HttpMethod.Patch, route, permit, body);
        Assert.Equal(HttpStatusCode.InternalServerError, lost.StatusCode);
        Assert.Equal("Saved Region", await db.Regions.Where(row => row.Id == region.Id).Select(row => row.Name).SingleAsync());
        await db.Regions.Where(row => row.Id == region.Id).ExecuteDeleteAsync();

        using var lookup = await LookupStructureAsync(web, route, permit);
        var recovered = await TripMetadataReceiptHttp.EvidenceAsync(lookup, permit, "applied");
        var result = recovered.GetProperty("result");
        Assert.Equal(region.Id, result.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("regionId").ValueKind);
        Assert.True(TripMetadataIdentity.IsDigest(result.GetProperty("fieldDigests").GetProperty("name").GetString()));

        using var replay = await TripMetadataReceiptHttp.SendAsync(web, HttpMethod.Patch, route, permit, body);
        Assert.Equal(recovered.GetRawText(), (await TripMetadataReceiptHttp.EvidenceAsync(replay, permit, "applied")).GetRawText());
        using var changed = await TripMetadataReceiptHttp.SendAsync(web, HttpMethod.Patch, route, permit, body + " ");
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.False((await changed.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("state", out _));
    }

    /// <summary>Rotation after authentication and before the apply lock gives no correlated evidence or effects.</summary>
    [PostgresFact]
    public async Task StructureReceipt_LateCookieRotationRefusesApply()
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        var region = await SeedStructureRegionAsync(db, trip);
        var gate = new ReviewAccountGate();
        await using var app = await StartAsync(db, [gate]);
        using var web = await WebAsync(app, trip.UserId);
        var route = $"/api/trips/{trip.Id}/editor/regions/{region.Id}/conditional/receipt";
        var body = StructureBody(region.Name);
        var permit = await IssueStructureAsync(web, trip, region, route, body);

        gate.Armed = true;
        var pending = TripMetadataReceiptHttp.SendAsync(web, HttpMethod.Patch, route, permit, body);
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(8));
            var stamp = Guid.NewGuid().ToString("N");
            await db.Users.Where(row => row.Id == trip.UserId).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.SecurityStamp, stamp));
        }
        finally { gate.Resume.TrySetResult(); }

        using var refusal = await pending;
        Assert.Equal(HttpStatusCode.Unauthorized, refusal.StatusCode);
        var error = await refusal.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mutation-authority-unavailable", error.GetProperty("code").GetString());
        Assert.False(error.TryGetProperty("operationId", out _));
        Assert.Equal(region.Name, await db.Regions.Where(row => row.Id == region.Id).Select(row => row.Name).SingleAsync());
        using var current = await WebAsync(app, trip.UserId);
        using var lookup = await LookupStructureAsync(current, route, permit);
        await TripMetadataReceiptHttp.EvidenceAsync(lookup, permit, "unobserved");
    }

    /// <summary>Creates a normal Region under the fixture's disposable owned Trip.</summary>
    private static async Task<Region> SeedStructureRegionAsync(ApplicationDbContext db, Trip trip)
    {
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, UserId = trip.UserId, Name = "Original Region" };
        db.Regions.Add(region);
        await db.SaveChangesAsync();
        return region;
    }

    /// <summary>Freezes the submitted bytes with the typed contract marker.</summary>
    private static string StructureBody(string name) => JsonSerializer.Serialize(new
    {
        receiptContract = TripMetadataIdentity.StructureVersion, expected = new { name }, changes = new { name = "Saved Region" }
    });

    /// <summary>Issues a real typed permit for the exact Region route and submitted bytes.</summary>
    private static async Task<JsonElement> IssueStructureAsync(HttpClient client, Trip trip, Region region, string route, string body)
    {
        var identity = new TripMetadataIdentity(TripMetadataIdentity.StructureVersion, trip.UserId, "web", "region-fields",
            trip.Id, "", Guid.Empty, default) { Target = new("region", region.Id) };
        var permit = await IssueTypedAsync(client, identity, route, body);
        Assert.Equal(region.Id, permit.GetProperty("target").GetProperty("id").GetGuid());
        return permit;
    }

    /// <summary>Issues a permit on the exact route with the frozen eight-frame digest.</summary>
    private static async Task<JsonElement> IssueTypedAsync(HttpClient client, TripMetadataIdentity identity, string route, string body)
    {
        using var response = await client.PostAsJsonAsync(route + "/operations", new
        {
            contract = identity.Contract, requestDigest = identity.StructureDigest(Encoding.UTF8.GetBytes(body))
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Requests historical evidence without sending the submitted body.</summary>
    private static Task<HttpResponseMessage> LookupStructureAsync(HttpClient client, string route, JsonElement permit) =>
        TripMetadataReceiptHttp.SendAsync(client, HttpMethod.Get,
            route + "/operations/" + permit.GetProperty("operationId").GetString(), permit, null);
}
