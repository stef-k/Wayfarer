using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>A2: unavailable raw capture originals remain distinct from null, validation failure and stale known values.</summary>
public sealed partial class LocationMutationPostgresTests
{
    /// <summary>Published sanitization and an unrelated protected altitude save never rewrite historical Notes.</summary>
    [PostgresFact]
    public async Task RetiredAttributes_ReadAndUnrelatedProtectedSavePreserveNotes()
    {
        var (_, credential, id) = await SeedAsync();
        const string stored = "<p class=\"ql-align-right\">Historical</p>";
        await using (var db = fixture.CreateContext())
            await db.Locations.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Notes, stored));
        await WithHostAsync(credential, async client =>
        {
            var detail = await ProtectedLocationRequest.DetailAsync(client, id);
            Assert.Equal("<p>Historical</p>", detail.GetProperty("record").GetProperty("notesHtml").GetString());
            var request = await ProtectedLocationRequest.CreateAsync(client, id,
                new { altitude = new { op = "set", value = 17 } }, "web-edit", detail);
            var originalBytes = request.Bytes.ToArray();
            await AssertStateAsync(await request.SendAsync(client), 200, "applied");
            Assert.Equal(originalBytes, request.Bytes);
            await using var verification = fixture.CreateContext();
            var row = await verification.Locations.AsNoTracking().SingleAsync(row => row.Id == id);
            Assert.Equal(stored, row.Notes);
            Assert.Equal(17, row.Altitude);
        });
    }

    /// <summary>Infinity-conversion original endpoints produce durable correlated rejection without applying valid desired time.</summary>
    [PostgresTheory]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("9999-12-31T23:59:59.999999999Z")]
    public async Task EndpointOriginalsRejectAsCorrelatedBaseUnavailable(string endpoint)
    {
        var (owner, credential, id) = await SeedAsync();
        await WithHostAsync(credential, async client =>
        {
            var request = await ProtectedLocationRequest.CreateAsync(client, id, new { eventUtc = new { op = "set", value = "2026-10-29T09:00:00Z" } });
            var body = JsonNode.Parse(request.Bytes)!;
            body["expected"]!["eventUtc"]!["value"] = endpoint;
            request = request with { Bytes = JsonSerializer.SerializeToUtf8Bytes(body) };
            var receipt = await AssertStateAsync(await request.SendAsync(client), 422, "rejected", "baseUnavailable");
            Assert.Equal(request.Fingerprint(owner), receipt.GetProperty("fingerprint").GetString());
            await AssertStateAsync(await request.RecoverAsync(client), 422, "rejected", "baseUnavailable");
            await using var verify = fixture.CreateContext();
            Assert.Equal(8, (await verify.Locations.SingleAsync(row => row.Id == id)).LocalTimestamp.Hour);
        });
    }
}
