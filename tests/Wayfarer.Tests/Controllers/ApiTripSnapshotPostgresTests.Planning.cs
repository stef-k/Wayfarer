using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class ApiTripSnapshotPostgresTests
{
    /// <summary>Only a verified bearer on the planning route can confirm this specific Trip is absent.</summary>
    [PostgresFact]
    public async Task VerifiedPlanningAbsenceIsDistinctFromGenericNotFound()
    {
        var user = await fixture.CreateUserAsync();
        await TokenAsync(user.Id);
        var absent = Guid.NewGuid();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, new ReadGate(pause: false));
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", user.Id);
        using var response = await client.GetAsync($"/api/trips/{absent}/planning");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.TryGetProperty("code", out var code), body);
        Assert.Equal("trip_not_found", code.GetString());
        Assert.Equal(absent, json.RootElement.GetProperty("tripId").GetGuid());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);

        foreach (var path in new[] { $"/api/trips/{absent}", $"/api/unmatched/{absent}" })
        {
            using var generic = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, generic.StatusCode);
            using var error = JsonDocument.Parse(await generic.Content.ReadAsStringAsync());
            Assert.Equal(404, error.RootElement.GetProperty("status").GetInt32());
            Assert.False(error.RootElement.TryGetProperty("code", out _));
        }
    }

    /// <summary>Missing and invalid credentials cannot confirm absence on the planning route.</summary>
    [PostgresTheory]
    [InlineData(null)]
    [InlineData("invalid-bearer")]
    public async Task UnverifiedPlanningAbsenceIsDenied(string? bearer)
    {
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, new ReadGate(pause: false));
        using var client = app.GetTestClient();
        if (bearer != null) client.DefaultRequestHeaders.Authorization = new("Bearer", bearer);
        using var denied = await client.GetAsync($"/api/trips/{Guid.NewGuid()}/planning");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.DoesNotContain("trip_not_found", await denied.Content.ReadAsStringAsync());
    }

    /// <summary>An existing public Trip exposes its graph and planning together only to its owner.</summary>
    [PostgresFact]
    public async Task ExistingPlanningReadRequiresOwnerEvenForPublicTrip()
    {
        var trip = await SeedAsync();
        var other = await fixture.CreateUserAsync();
        await TokenAsync(other.Id);
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, new ReadGate(pause: false));
        using var client = app.GetTestClient();
        var path = $"/api/trips/{trip.Id}/planning";
        client.DefaultRequestHeaders.Authorization = new("Bearer", other.Id);
        using var denied = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.DoesNotContain("trip_not_found", await denied.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Authorization = new("Bearer", trip.UserId);
        using var owner = await client.GetAsync(path);
        using var envelope = await ReadJsonAsync(owner);
        Assert.True(owner.Headers.CacheControl?.Private);
        Assert.True(owner.Headers.CacheControl?.NoStore);
        using var detail = await client.GetAsync($"/api/trips/{trip.Id}");
        using var graph = await ReadJsonAsync(detail);
        Assert.True(JsonElement.DeepEquals(graph.RootElement, envelope.RootElement.GetProperty("trip")));
        var planning = envelope.RootElement.GetProperty("planning");
        Assert.Equal(Place(trip).Id.ToString(), Assert.Single(planning.GetProperty("places").EnumerateObject()).Name);
        Assert.Equal(trip.Segments.Single().Id.ToString(), Assert.Single(planning.GetProperty("segments").EnumerateObject()).Name);
    }
}
