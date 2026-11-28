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
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, JsonElement permit, string? body)
    {
        using var request = new HttpRequestMessage(method, route);
        request.Headers.Add("X-Wayfarer-Trip-Attempt", permit.GetProperty("proof").GetString());
        if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

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
