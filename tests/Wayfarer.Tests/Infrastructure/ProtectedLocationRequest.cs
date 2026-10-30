using System.Text;
using System.Text.Json;
using Wayfarer.Services.LocationMutations;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>Builds protected requests from actual owner detail; transport still uses production MVC and PostgreSQL.</summary>
internal sealed record ProtectedLocationRequest(int Id, Guid BaseId, Guid ActionId, string Handle, byte[] Bytes)
{
    internal string Route => $"/api/location/{Id}/mutations/{BaseId:D}/{ActionId:D}";

    /// <summary>Retained detail can intentionally supply stale edit-start originals.</summary>
    internal static async Task<ProtectedLocationRequest> CreateAsync(HttpClient client, int id, object? changes,
        string adapter = "owner", JsonElement? detail = null)
    {
        detail ??= await DetailAsync(client, id);
        var evidence = detail.Value.GetProperty("locationMutation");
        var originals = evidence.GetProperty("baseValues");
        var selected = JsonSerializer.SerializeToElement(changes);
        var groups = new HashSet<string> { "uuid" };
        if (changes == null) groups.Add("content");
        else foreach (var field in selected.EnumerateObject())
        {
            if (field.Name is "position" or "manualAddress") groups.UnionWith(["position", "address"]);
            else groups.Add(field.Name switch { "notes" => "notesHtml", "activity" => "activityTypeId", _ => field.Name });
        }
        var body = new Dictionary<string, object?> { ["contract"] = "location-mutation-1", ["normalization"] = "location-values-1",
            ["adapter"] = adapter, ["baseHandle"] = evidence.GetProperty("baseHandle").GetString(), ["kind"] = changes == null ? "delete" : "update",
            ["expected"] = groups.ToDictionary(group => group, group => originals.GetProperty(group == "content" ? "deleteContent" : group)) };
        if (changes != null) body["changes"] = selected;
        return new(id, evidence.GetProperty("baseId").GetGuid(), Guid.NewGuid(), evidence.GetProperty("baseHandle").GetString()!, JsonSerializer.SerializeToUtf8Bytes(body));
    }

    /// <summary>Retrieves actual authorized detail, failing loudly on an infrastructure/authority error.</summary>
    internal static async Task<JsonElement> DetailAsync(HttpClient client, int id)
    {
        using var response = await client.GetAsync($"/api/location/history/{id}?viewerZone=UTC");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Detail {(int)response.StatusCode}: {Encoding.UTF8.GetString(bytes)}");
        return JsonSerializer.Deserialize<JsonElement>(bytes);
    }

    /// <summary>Reuses exactly the bytes and identity, even after a simulated total response loss.</summary>
    internal Task<HttpResponseMessage> SendAsync(HttpClient client) => client.PostAsync(Route, new ByteArrayContent(Bytes)
        { Headers = { ContentType = new("application/json") } });

    /// <summary>Read-only recovery carries original authority, including beyond execution expiry.</summary>
    internal Task<HttpResponseMessage> RecoverAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Route);
        request.Headers.Add("X-Location-Base", Handle);
        return client.SendAsync(request);
    }

    /// <summary>The first recovered server fingerprint must match this independently computed pre-send value.</summary>
    internal string Fingerprint(string owner, string adapter = "owner") =>
        LocationMutationCommand.Fingerprint(owner, Id, BaseId, ActionId, adapter, Bytes);
}
