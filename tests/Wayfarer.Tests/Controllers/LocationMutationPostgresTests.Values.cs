using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>A2: unavailable raw capture originals remain distinct from null, validation failure and stale known values.</summary>
public sealed partial class LocationMutationPostgresTests
{
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
