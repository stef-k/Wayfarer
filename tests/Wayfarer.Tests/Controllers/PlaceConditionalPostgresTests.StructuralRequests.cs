using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
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

    /// <summary>No-move Web vectors cannot authorize writes or disclose membership, including with a genuine prepared token.</summary>
    [PostgresTheory]
    [InlineData("same-owner-other-trip")]
    [InlineData("foreign-region-disclosure")]
    [InlineData("owned-region")]
    [InlineData("empty")]
    public async Task NoMove_RejectsUnprovedOrderScopes(string scope)
    {
        var own = await SeedAsync();
        var other = await SeedAsync();
        await using var db = fixture.CreateContext();
        if (scope == "same-owner-other-trip")
        {
            await db.Places.Where(p => p.Id == other.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.UserId, own.UserId));
            await db.Regions.Where(r => r.Id == other.RegionId).ExecuteUpdateAsync(s => s.SetProperty(r => r.UserId, own.UserId));
            await db.Trips.Where(t => t.Id == other.Region.TripId).ExecuteUpdateAsync(s => s.SetProperty(t => t.UserId, own.UserId));
        }
        var before = await StoredStructureRowsAsync(own.Region.TripId, other.Region.TripId);
        var probe = new PlaceProbe();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, own.UserId);
        var request = new Dictionary<string, object?>
        {
            ["expected"] = new { regionId = own.RegionId, name = own.Name },
            ["changes"] = new { name = "Own edit" }
        };
        await PrepareStructureAsync(web, own, true, "coordinates", request);
        request["placeIdsByRegion"] = scope switch
        {
            "same-owner-other-trip" => new Dictionary<Guid, Guid[]> { [own.RegionId] = [own.Id, other.Id] },
            "foreign-region-disclosure" => new Dictionary<Guid, Guid[]> { [other.RegionId] = [own.Id] },
            "owned-region" => new Dictionary<Guid, Guid[]> { [own.RegionId] = [own.Id] },
            _ => new Dictionary<Guid, Guid[]>()
        };
        var path = StructurePath(own, true, "coordinates");
        using var prepare = await web.PostAsJsonAsync(path + "/prepare", request);
        using var apply = await web.PutAsJsonAsync(path, request);
        foreach (var response in new[] { prepare, apply })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var reply = await response.Content.ReadAsStringAsync();
            Assert.Contains("mutation-request-invalid", reply);
            Assert.DoesNotContain(other.Id.ToString(), reply);
            Assert.DoesNotContain(other.RegionId.ToString(), reply);
            Assert.DoesNotContain("placeOrdersByRegionId", reply);
        }
        Assert.Equal(before, await StoredStructureRowsAsync(own.Region.TripId, other.Region.TripId));
        Assert.Empty(probe.Updates);
        Assert.Empty(_warmup.Invocations);
    }

    /// <summary>Moves prove exactly two Region scopes and every member; unrelated same-Trip and foreign vectors cannot hitchhike.</summary>
    [PostgresTheory]
    [InlineData("extra-region")]
    [InlineData("unrelated-member")]
    [InlineData("foreign-member")]
    public async Task Move_RejectsUnprovedOrderScopes(string scope)
    {
        var graph = await RegionDeletionTestGraph.SeedAsync(fixture);
        var foreign = await SeedAsync();
        await using var db = fixture.CreateContext();
        var place = await db.Places.Include(p => p.Region).SingleAsync(p => p.Id == graph.PlaceIds[0]);
        await AddCredentialAsync(db, place.UserId);
        var before = await StoredStructureRowsAsync(graph.TripId, foreign.Region.TripId);
        var dependencies = await GraphStateAsync(graph.TripId);
        var probe = new PlaceProbe();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, place.UserId);
        var request = await MoveRequestAsync(place, graph.DestinationId!.Value, true);
        await PrepareStructureAsync(web, place, true, "move", request);
        var vectors = (Dictionary<Guid, Guid[]>)request["placeIdsByRegion"]!;
        if (scope == "extra-region") vectors[foreign.RegionId] = [foreign.Id];
        else vectors[graph.DestinationId.Value] = vectors[graph.DestinationId.Value]
            .Append(scope == "foreign-member" ? foreign.Id : graph.OutsidePlaceId).ToArray();
        var path = StructurePath(place, true, "move");
        using var prepare = await web.PostAsJsonAsync(path + "/prepare", request);
        using var apply = await web.PutAsJsonAsync(path, request);
        foreach (var response in new[] { prepare, apply })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var reply = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(foreign.Id.ToString(), reply);
            Assert.DoesNotContain("placeOrdersByRegionId", reply);
        }
        Assert.Equal(before, await StoredStructureRowsAsync(graph.TripId, foreign.Region.TripId));
        Assert.Equal(dependencies, await GraphStateAsync(graph.TripId));
        Assert.Empty(probe.Updates);
        Assert.Empty(_warmup.Invocations);
    }

    /// <summary>Historical invalid geography projects safely throughout prepare/apply; only authored changes affect storage or enrichment.</summary>
    [PostgresTheory]
    [InlineData("address")]
    [InlineData("coordinates")]
    [InlineData("order")]
    public async Task HistoricalNonFinite_StructuralPrepareAndSave(string operation)
    {
        var place = await SeedAsync();
        var enrichedAt = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        await using var db = fixture.CreateContext();
        await db.Places.Where(p => p.Id == place.Id).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.Location, new Point(22, double.NaN) { SRID = 4326 })
            .SetProperty(p => p.ResolvedFeatureName, "Historical enrichment").SetProperty(p => p.ResolvedFeatureType, "landmark")
            .SetProperty(p => p.AddressEnrichmentProvider, "geoapify").SetProperty(p => p.AddressEnrichmentStorageMode, "persistent")
            .SetProperty(p => p.AddressEnrichedAt, enrichedAt));
        var before = await StoredStructureRowsAsync(place.Region.TripId);
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, place.UserId);
        using var bearer = Bearer(app, place.UserId);
        using var read = await web.GetAsync($"/api/trips/{place.Region.TripId}/editor");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var published = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("placesById").GetProperty(place.Id.ToString());
        Assert.Equal(JsonValueKind.Null, published.GetProperty("location").ValueKind);
        var material = new { id = place.Id, regionId = place.RegionId, name = place.Name, notes = place.Notes,
            address = place.Address, location = (object?)null, iconName = place.IconName, markerColor = place.MarkerColor,
            displayOrder = place.DisplayOrder, resolvedFeatureName = "Historical enrichment", resolvedFeatureType = "landmark" };
        var path = operation == "order" ? OrderPath(place.Region.TripId, place.RegionId, false)
            : StructurePath(place, operation == "address", "coordinates");
        var client = operation == "address" ? web : bearer;
        Dictionary<string, object?> request = operation switch
        {
            "address" => new() { ["expected"] = new { regionId = place.RegionId, address = place.Address }, ["changes"] = new { address = "Authored address" } },
            "coordinates" => new() { ["expected"] = new { regionId = place.RegionId, location = (object?)null }, ["location"] = new { latitude = 15, longitude = 23 } },
            _ => new() { ["expected"] = new { places = new[] { material } }, ["placeIds"] = new[] { place.Id } }
        };
        using var prepare = await client.PostAsJsonAsync(path + "/prepare", request);
        Assert.Equal(HttpStatusCode.OK, prepare.StatusCode);
        var preview = await prepare.Content.ReadFromJsonAsync<JsonElement>();
        var previewPlace = operation == "order" ? preview.GetProperty("placesByRegion").GetProperty(place.RegionId.ToString())[0] : preview.GetProperty("place");
        Assert.Equal(JsonValueKind.Null, previewPlace.GetProperty("location").ValueKind);
        Assert.Equal(before, await StoredStructureRowsAsync(place.Region.TripId));
        request["conditionToken"] = preview.GetProperty("conditionToken").GetString();
        request["reviewed"] = true;
        using var apply = operation == "coordinates" ? await client.PostAsJsonAsync(path, request) : await client.PutAsJsonAsync(path, request);
        Assert.True(apply.StatusCode == HttpStatusCode.OK, await apply.Content.ReadAsStringAsync());
        var result = await apply.Content.ReadFromJsonAsync<JsonElement>();
        var affected = Assert.Single(result.GetProperty("affected").GetProperty("places").EnumerateArray());
        Assert.Equal(operation == "coordinates" ? JsonValueKind.Object : JsonValueKind.Null, affected.GetProperty("location").ValueKind);
        if (operation != "order") Assert.True(JsonElement.DeepEquals(affected, result.GetProperty("data")));
        else Assert.Equal(place.Id, Assert.Single(result.GetProperty("affected").GetProperty("placeOrdersByRegionId").GetProperty(place.RegionId.ToString()).EnumerateArray()).GetGuid());
        await using var verify = fixture.CreateContext();
        var stored = await verify.Places.AsNoTracking().SingleAsync(p => p.Id == place.Id);
        Assert.Equal((place.Name, place.Notes, place.RegionId, place.IconName, place.MarkerColor),
            (stored.Name, stored.Notes, stored.RegionId, stored.IconName, stored.MarkerColor));
        Assert.Equal(operation == "address" ? "Authored address" : place.Address, stored.Address);
        Assert.Equal(operation == "order" ? 1 : place.DisplayOrder, stored.DisplayOrder);
        Assert.Equal(4326, stored.Location!.SRID);
        if (operation == "coordinates") Assert.Equal((23d, 15d), (stored.Location.X, stored.Location.Y));
        else { Assert.Equal(22d, stored.Location.X); Assert.True(double.IsNaN(stored.Location.Y)); }
        if (operation == "order") Assert.Equal(("Historical enrichment", "landmark", "geoapify", "persistent", (DateTimeOffset?)enrichedAt),
            (stored.ResolvedFeatureName, stored.ResolvedFeatureType, stored.AddressEnrichmentProvider, stored.AddressEnrichmentStorageMode, stored.AddressEnrichedAt));
        // Authored Address or coordinate changes intentionally clear D3; invalid historical geography alone must never clear it.
        else Assert.All(new object?[] { stored.ResolvedFeatureName, stored.ResolvedFeatureType, stored.AddressEnrichmentProvider,
            stored.AddressEnrichmentStorageMode, stored.AddressEnrichedAt }, Assert.Null);
        Assert.Empty(_warmup.Invocations);
    }

    /// <summary>Public null cannot exchange invalid raw geography for absence, even across the final Place lock wait.</summary>
    [PostgresFact]
    public async Task HistoricalNonFinite_RawGeometryDriftRejectsAfterLock()
    {
        var place = await SeedAsync();
        await using var db = fixture.CreateContext();
        await db.Places.Where(p => p.Id == place.Id).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.Location, new Point(22, double.NaN) { SRID = 4326 })
            .SetProperty(p => p.ResolvedFeatureName, "Retain enrichment"));
        var probe = new PlaceProbe("Places");
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, place.UserId);
        var request = new Dictionary<string, object?> { ["expected"] = new { regionId = place.RegionId, address = place.Address },
            ["changes"] = new { address = "Must not save" } };
        await PrepareStructureAsync(web, place, true, "coordinates", request);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Places\" WHERE \"Id\" = {place.Id} FOR UPDATE");
        var pending = web.PutAsJsonAsync(StructurePath(place, true, "coordinates"), request);
        await WaitForLockAsync(probe, 0);
        await blocker.Places.Where(p => p.Id == place.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Location, (Point?)null));
        await transaction.CommitAsync();
        var before = await StoredStructureRowsAsync(place.Region.TripId);
        using var result = await pending;
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        Assert.Contains("mutation-dependency-stale", await result.Content.ReadAsStringAsync());
        Assert.Equal(before, await StoredStructureRowsAsync(place.Region.TripId));
        Assert.Empty(probe.Updates);
        Assert.Empty(_warmup.Invocations);
    }

    /// <summary>Reads complete fresh persisted Place/Region/Trip rows, including ordering, geometry and freshness, outside request trackers.</summary>
    private async Task<string?> StoredStructureRowsAsync(params Guid[] tripIds)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT string_agg(row, ',' ORDER BY row) FROM (
              SELECT 'place:' || to_jsonb(p)::text AS row FROM "Places" p JOIN "Regions" r ON r."Id" = p."RegionId" WHERE r."TripId" = ANY($1)
              UNION ALL SELECT 'region:' || to_jsonb(r)::text FROM "Regions" r WHERE r."TripId" = ANY($1)
              UNION ALL SELECT 'trip:' || to_jsonb(t)::text FROM "Trips" t WHERE t."Id" = ANY($1)
            ) stored
            """, connection);
        command.Parameters.AddWithValue(tripIds);
        return (string?)await command.ExecuteScalarAsync();
    }

}
