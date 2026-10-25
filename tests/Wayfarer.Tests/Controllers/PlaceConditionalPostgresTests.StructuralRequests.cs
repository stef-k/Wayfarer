using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Models;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class PlaceConditionalPostgresTests
{
    /// <summary>A prepared combined token cannot be retained while silently replacing a scalar original after a concurrent edit.</summary>
    [PostgresFact]
    public async Task CombinedConditions_CannotExchangeOriginalBase()
    {
        var place = await SeedAsync();
        await using var db = fixture.CreateContext(); await using var app = await StartAsync(db);
        using var web = await WebAsync(app, place.UserId);
        var request = CoordinateRequest(place, true, 15, 23);
        request["expected"] = new { regionId = place.RegionId, location = Coordinate(place), name = place.Name };
        request["changes"] = new { name = "My name", location = new { latitude = 15, longitude = 23 } };
        await PrepareStructureAsync(web, place, true, "coordinates", request);
        await db.Places.Where(p => p.Id == place.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "Intervening name"));
        request["expected"] = new { regionId = place.RegionId, location = Coordinate(place), name = "Intervening name" };
        using var response = await web.PutAsJsonAsync(StructurePath(place, true, "coordinates"), request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var verify = fixture.CreateContext(); var stored = await verify.Places.SingleAsync(p => p.Id == place.Id);
        Assert.Equal(("Intervening name", place.Location!.AsText()), (stored.Name, stored.Location!.AsText()));
    }

    /// <summary>Malformed complete vectors and stale same-ID content/order reject atomically through both transports.</summary>
    [PostgresTheory]
    [InlineData(false, "duplicate")]
    [InlineData(true, "foreign")]
    [InlineData(false, "omitted")]
    [InlineData(true, "content")]
    [InlineData(false, "order")]
    public async Task Order_InvalidOrStaleVectorHasNoPartialEffect(bool web, string mutation)
    {
        var graph = await RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext();
        await AddCredentialAsync(db, graph.UserId);
        await using var app = await StartAsync(db);
        using var client = web ? await WebAsync(app, graph.UserId) : Bearer(app, graph.UserId);
        var places = await db.Places.AsNoTracking().Where(p => p.RegionId == graph.SourceId).OrderBy(p => p.Id).ToArrayAsync();
        var path = OrderPath(graph.TripId, graph.SourceId, web);
        Dictionary<string, object?> request = new() { ["expected"] = new { places = places.Select(Material).ToArray() }, ["placeIds"] = places.Select(p => p.Id).ToArray() };
        using var prepare = await client.PostAsJsonAsync(path + "/prepare", request);
        Assert.Equal(HttpStatusCode.OK, prepare.StatusCode);
        request["conditionToken"] = (await prepare.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conditionToken").GetString(); request["reviewed"] = true;
        if (mutation == "duplicate") request["placeIds"] = new[] { places[0].Id, places[0].Id };
        if (mutation == "foreign") request["placeIds"] = new[] { places[0].Id, graph.OutsidePlaceId };
        if (mutation == "omitted") request["placeIds"] = new[] { places[0].Id };
        if (mutation == "content") await db.Places.Where(p => p.Id == places[0].Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Notes, "Changed content"));
        if (mutation == "order") await db.Places.Where(p => p.Id == places[0].Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.DisplayOrder, 88));
        var current = await db.Places.AsNoTracking().Where(p => p.RegionId == graph.SourceId).OrderBy(p => p.Id).Select(p => new { p.Id, p.DisplayOrder, p.Notes }).ToArrayAsync();
        using var result = await client.PutAsJsonAsync(path, request);
        Assert.Equal(mutation is "content" or "order" ? HttpStatusCode.Conflict : HttpStatusCode.BadRequest, result.StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.Equal(JsonSerializer.Serialize(current), JsonSerializer.Serialize(await verify.Places.Where(p => p.RegionId == graph.SourceId).OrderBy(p => p.Id).Select(p => new { p.Id, p.DisplayOrder, p.Notes }).ToArrayAsync()));
    }

    /// <summary>Missing review, a tampered condition and changed desired intent cannot use a prepared original base.</summary>
    [PostgresTheory]
    [InlineData("unreviewed")]
    [InlineData("tampered")]
    [InlineData("intent")]
    public async Task Coordinates_ConditionsArePurposeAndIntentBound(string mutation)
    {
        var place = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var client = Bearer(app, place.UserId);
        var request = CoordinateRequest(place, false, 15, 23);
        await PrepareStructureAsync(client, place, false, "coordinates", request);
        if (mutation == "unreviewed") request["reviewed"] = false;
        if (mutation == "tampered") request["conditionToken"] = "tampered";
        if (mutation == "intent") request["location"] = new { latitude = 16, longitude = 24 };
        using var response = await client.PostAsJsonAsync(StructurePath(place, false, "coordinates"), request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.Equal(place.Location!.AsText(), (await verify.Places.FindAsync(place.Id))!.Location!.AsText());
    }

    /// <summary>New coordinate contract rejects malformed WGS84 pairs and cannot grant bearer Address authoring.</summary>
    [PostgresTheory]
    [InlineData("{\"latitude\":91,\"longitude\":23}")]
    [InlineData("{\"latitude\":15}")]
    [InlineData("null")]
    [InlineData("{\"latitude\":15,\"longitude\":23,\"address\":\"New\"}")]
    public async Task Coordinates_RejectInvalidPair(string pair)
    {
        var place = await SeedAsync();
        await using var db = fixture.CreateContext(); await using var app = await StartAsync(db);
        using var client = Bearer(app, place.UserId);
        var request = CoordinateRequest(place, false, 15, 23); request["location"] = JsonSerializer.Deserialize<JsonElement>(pair);
        using var response = await client.PostAsJsonAsync(StructurePath(place, false, "coordinates") + "/prepare", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>The route retains current bearer authority and the cookie/User/antiforgery boundary for prepare and apply.</summary>
    [PostgresFact]
    public async Task StructuralRoutes_RetainAuthoritySeparation()
    {
        var place = await SeedAsync();
        await using var db = fixture.CreateContext(); await using var app = await StartAsync(db);
        using var cookie = await WebAsync(app, place.UserId);
        using var bearer = Bearer(app, place.UserId);
        using var cookieOnlyBearer = await cookie.PostAsJsonAsync(StructurePath(place, false, "coordinates") + "/prepare", CoordinateRequest(place, false, 15, 23));
        Assert.Equal(HttpStatusCode.Unauthorized, cookieOnlyBearer.StatusCode);
        cookie.DefaultRequestHeaders.Remove("RequestVerificationToken");
        using var missingAnti = await cookie.PostAsJsonAsync(StructurePath(place, true, "coordinates") + "/prepare", CoordinateRequest(place, true, 15, 23));
        Assert.Equal(HttpStatusCode.BadRequest, missingAnti.StatusCode);
        var request = CoordinateRequest(place, false, 15, 23); await PrepareStructureAsync(bearer, place, false, "coordinates", request);
        await db.ApiTokens.Where(t => t.UserId == place.UserId).ExecuteDeleteAsync();
        using var revoked = await bearer.PostAsJsonAsync(StructurePath(place, false, "coordinates"), request);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }
}
