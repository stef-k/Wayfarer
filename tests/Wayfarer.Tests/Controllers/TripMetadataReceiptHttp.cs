using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Wayfarer.Models;
using Wayfarer.Services.TripMetadataReceipts;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Creates the published frozen requests and extracts the routed evidence grammar.</summary>
internal static class TripMetadataReceiptHttp
{
    internal const string Contract = "trip-metadata-receipt-1";

    /// <summary>Locates the existing adapter writer and its additive operation routes.</summary>
    internal static string Route(Trip trip, bool web = false) =>
        $"/api/trips/{trip.Id}/" + (web ? "editor/metadata/conditional" : "conditional");

    /// <summary>Serializes the frozen marked body once.</summary>
    internal static string Body(object expected, object changes) =>
        JsonSerializer.Serialize(new { receiptContract = Contract, expected, changes });

    /// <summary>Obtains a permit from the actual server, using the authenticated subject and exact bytes.</summary>
    internal static Task<HttpResponseMessage> IssueAsync(HttpClient client, Trip trip, string body, bool web = false) =>
        client.PostAsJsonAsync(Route(trip, web) + "/operations", new
        {
            contract = Contract,
            requestDigest = TripMetadataIdentity.Digest(trip.UserId, web ? "web" : "bearer", trip.Id, Encoding.UTF8.GetBytes(body))
        });

    /// <summary>Obtains a permit and verifies the transport response.</summary>
    internal static async Task<JsonElement> PermitAsync(HttpClient client, Trip trip, string body, bool web = false)
    {
        using var reply = await IssueAsync(client, trip, body, web);
        Assert.True(reply.IsSuccessStatusCode, $"Issuance failed: {(int)reply.StatusCode} {await reply.Content.ReadAsStringAsync()}");
        Assert.True(reply.Headers.CacheControl is { Private: true, NoStore: true });
        return await reply.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Sends the exact frozen bytes and opaque permit.</summary>
    internal static Task<HttpResponseMessage> SendAsync(HttpClient client, Trip trip, JsonElement permit, string body, bool web = false) =>
        SendAsync(client, HttpMethod.Patch, Route(trip, web), permit, body);

    /// <summary>Looks up one correlated operation without transmitting changes.</summary>
    internal static Task<HttpResponseMessage> LookupAsync(HttpClient client, Trip trip, JsonElement permit, bool web = false) =>
        SendAsync(client, HttpMethod.Get, Route(trip, web) + "/operations/" + permit.GetProperty("operationId").GetString(), permit, null);

    /// <summary>Uses the exact proof without putting it in the URL or response logs.</summary>
    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, JsonElement permit, string? body)
    {
        using var request = new HttpRequestMessage(method, route);
        request.Headers.Add("X-Wayfarer-Trip-Attempt", permit.GetProperty("proof").GetString());
        if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    /// <summary>Issues a permit on the exact route with the frozen eight-frame digest.</summary>
    internal static async Task<JsonElement> IssueTypedAsync(HttpClient client, TripMetadataIdentity identity, string route, string body)
    {
        using var response = await IssueTypedResponseAsync(client, identity, route, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Routes a Web regression request through the typed contract, retaining issuance refusals for assertions.</summary>
    internal static async Task<HttpResponseMessage> DispatchTypedAsync(HttpClient client, TripMetadataIdentity identity,
        HttpMethod method, string route, string payload)
    {
        var body = MarkTypedBody(identity.Contract, payload);
        var issuance = await IssueTypedResponseAsync(client, identity, route, body);
        if (!issuance.IsSuccessStatusCode) return issuance;
        using (issuance)
            return await SendAsync(client, method, route, await issuance.Content.ReadFromJsonAsync<JsonElement>(), body);
    }

    /// <summary>Confirms a protected Web Region deletion using only the reviewed choices and token.</summary>
    internal static Task<HttpResponseMessage> DispatchRegionDeletionAsync(HttpClient client, string userId, Guid tripId, Guid regionId, object choices)
    {
        var identity = new TripMetadataIdentity(TripMetadataIdentity.StructureVersion, userId, "web", "region-delete",
            tripId, "", Guid.Empty, default) { Target = new("region", regionId) };
        return DispatchTypedAsync(client, identity, HttpMethod.Post,
            $"/api/trips/{tripId}/editor/regions/{regionId}/deletion/receipt", JsonSerializer.Serialize(choices));
    }

    /// <summary>Captures issuance before a race test installs its separate domain-row blocker.</summary>
    internal static async Task<Func<Task<HttpResponseMessage>>> PrepareTypedAsync(HttpClient client, TripMetadataIdentity identity,
        HttpMethod method, string route, string payload)
    {
        var body = MarkTypedBody(identity.Contract, payload);
        var permit = await IssueTypedAsync(client, identity, route, body);
        return () => SendAsync(client, method, route, permit, body);
    }

    /// <summary>Prepends the contract marker while preserving all fixture bytes, including duplicated names.</summary>
    private static string MarkTypedBody(string contract, string payload) =>
        "{\"receiptContract\":\"" + contract + "\"" + (payload == "{}" ? "}" : "," + payload[1..]);

    /// <summary>Hashes validated or deliberately malformed fixture bytes exactly as sent.</summary>
    private static Task<HttpResponseMessage> IssueTypedResponseAsync(HttpClient client, TripMetadataIdentity identity, string route, string body) =>
        client.PostAsJsonAsync(route + "/operations", new
        {
            contract = identity.Contract, requestDigest = identity.StructureDigest(Encoding.UTF8.GetBytes(body))
        });

    /// <summary>Checks complete correlation independently of the current Trip.</summary>
    internal static async Task<JsonElement> EvidenceAsync(HttpResponseMessage reply, JsonElement permit, string state, int status = 200)
    {
        Assert.Equal(status, (int)reply.StatusCode);
        var value = await reply.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(state, value.GetProperty("state").GetString());
        foreach (var field in permit.EnumerateObject())
            if (field.Name != "proof") Assert.Equal(field.Value.GetRawText(), value.GetProperty(field.Name).GetRawText());
        Assert.False(value.TryGetProperty("proof", out _));
        Assert.False(value.TryGetProperty("trip", out _));
        Assert.True(reply.Headers.CacheControl is { Private: true, NoStore: true });
        return value;
    }
}

/// <summary>A frozen v2 routed operation used for both original dispatch and exact replay.</summary>
internal sealed record TripControlWrite(string Route, HttpMethod Method, string Kind, string Adapter, string Body)
{
    internal const string Contract = "trip-metadata-receipt-2";

    /// <summary>Serializes one frozen payload using the exact route kind.</summary>
    internal static TripControlWrite Create(Guid trip, string suffix, HttpMethod method, string kind, bool web, object payload)
    {
        var fields = JsonSerializer.SerializeToElement(payload).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        fields.Add("receiptContract", Contract);
        var route = suffix.StartsWith("/User/", StringComparison.Ordinal) ? suffix : $"/api/trips/{trip}/{suffix}";
        return new(route, method, kind, web ? "web" : "bearer", JsonSerializer.Serialize(fields));
    }

    /// <summary>Requests a real permit without recording credentials in evidence.</summary>
    internal Task<HttpResponseMessage> IssueAsync(HttpClient client, string subject, Guid trip) =>
        client.PostAsJsonAsync(Route + "/operations", new
        {
            contract = Contract,
            requestDigest = TripMetadataIdentity.Digest(Contract, Kind, subject, Adapter, trip, Encoding.UTF8.GetBytes(Body))
        });

    /// <summary>Reads a successful authenticated issuance.</summary>
    internal async Task<JsonElement> PermitAsync(HttpClient client, Trip trip)
    {
        using var response = await IssueAsync(client, trip.UserId, trip.Id);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Sends the original bytes and proof to the exact write route.</summary>
    internal Task<HttpResponseMessage> DispatchAsync(HttpClient client, JsonElement permit) =>
        TripMetadataReceiptHttp.SendAsync(client, Method, Route, permit, Body);

    /// <summary>Reads one operation without replaying it.</summary>
    internal Task<HttpResponseMessage> LookupAsync(HttpClient client, JsonElement permit) =>
        TripMetadataReceiptHttp.SendAsync(client, HttpMethod.Get, Route + "/operations/" + permit.GetProperty("operationId").GetString(), permit, null);
}
