using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Models;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class PlaceConditionalPostgresTests
{
    /// <summary>BD2-08: changed/unchanged coordinates preserve Address and atomically clear/retain the complete D3 tuple.</summary>
    [PostgresTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Coordinates_D3AndAddress(bool web, bool changed)
    {
        var place = await SeedAsync();
        await using var db = fixture.CreateContext();
        var enriched = DateTimeOffset.UtcNow;
        await db.Places.Where(p => p.Id == place.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.ResolvedFeatureName, "Feature")
            .SetProperty(p => p.ResolvedFeatureType, "landmark").SetProperty(p => p.AddressEnrichmentProvider, "geoapify")
            .SetProperty(p => p.AddressEnrichmentStorageMode, "persistent").SetProperty(p => p.AddressEnrichedAt, enriched));
        var future = DateTime.UtcNow.Date.AddDays(1);
        await db.Trips.Where(t => t.Id == place.Region.TripId).ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, future));
        await using var app = await StartAsync(db);
        using var client = web ? await WebAsync(app, place.UserId) : Bearer(app, place.UserId);
        var request = CoordinateRequest(place, web, changed ? 15 : 14, 22);
        using var response = await ApplyStructureAsync(client, place, web, "coordinates", request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        await using var verify = fixture.CreateContext();
        var current = await verify.Places.SingleAsync(p => p.Id == place.Id);
        Assert.Equal(place.Address, current.Address);
        Assert.Equal(changed ? 15 : 14, current.Location!.Y);
        if (changed) Assert.All(new object?[] { current.ResolvedFeatureName, current.ResolvedFeatureType,
            current.AddressEnrichmentProvider, current.AddressEnrichmentStorageMode, current.AddressEnrichedAt }, Assert.Null);
        else Assert.Equal(("Feature", "landmark", "geoapify", "persistent"), (current.ResolvedFeatureName,
            current.ResolvedFeatureType, current.AddressEnrichmentProvider, current.AddressEnrichmentStorageMode));
        Assert.Equal(future, (await verify.Trips.FindAsync(place.Region.TripId))!.UpdatedAt);
        Assert.Empty(_warmup.Invocations);
    }

    /// <summary>BD2-09: both transports transfer once, preserve full content/visits/routes and normalize only the two scopes.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Move_PreservesPopulatedGraph(bool web)
    {
        var graph = await RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext();
        var place = await db.Places.Include(p => p.Region).SingleAsync(p => p.Id == graph.PlaceIds[0]);
        await AddCredentialAsync(db, place.UserId);
        var before = await GraphStateAsync(graph.TripId);
        var original = JsonSerializer.Serialize(Material(place));
        await using var app = await StartAsync(db);
        using var client = web ? await WebAsync(app, place.UserId) : Bearer(app, place.UserId);
        var request = await MoveRequestAsync(place, graph.DestinationId!.Value, web);
        using var response = await ApplyStructureAsync(client, place, web, "move", request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        await using var verify = fixture.CreateContext();
        var current = await verify.Places.SingleAsync(p => p.Id == place.Id);
        Assert.Equal(graph.DestinationId, current.RegionId);
        current.RegionId = place.RegionId; current.DisplayOrder = place.DisplayOrder;
        Assert.Equal(original, JsonSerializer.Serialize(Material(current)));
        Assert.Equal(before, await GraphStateAsync(graph.TripId));
        Assert.Single(await verify.PlaceVisitEvents.Where(v => v.PlaceId == place.Id).ToArrayAsync());
        Assert.Equal(17, (await verify.Regions.FindAsync(graph.DestinationId))!.DisplayOrder);
        Assert.Equal(1, (await verify.Places.SingleAsync(p => p.Id == graph.PlaceIds[1])).DisplayOrder);
    }

    /// <summary>BD2-10: complete vectors retain null/sparse originals, content and the separate Unassigned capability.</summary>
    [PostgresTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Order_CompleteVectorAndCanonicalRules(bool web, bool canonical)
    {
        var graph = await RegionDeletionTestGraph.SeedAsync(fixture);
        var regionId = canonical ? graph.DestinationId!.Value : graph.SourceId;
        await using var db = fixture.CreateContext();
        await AddCredentialAsync(db, graph.UserId);
        var places = await db.Places.AsNoTracking().Where(p => p.RegionId == regionId).OrderByDescending(p => p.Id).ToArrayAsync();
        var before = await GraphStateAsync(graph.TripId);
        await using var app = await StartAsync(db);
        using var client = web ? await WebAsync(app, graph.UserId) : Bearer(app, graph.UserId);
        var path = OrderPath(graph.TripId, regionId, web);
        var request = new Dictionary<string, object?> { ["expected"] = new { places = places.OrderBy(p => p.Id).Select(Material) }, ["placeIds"] = places.Select(p => p.Id).ToArray() };
        using var prepare = await client.PostAsJsonAsync(path + "/prepare", request);
        if (canonical && web) { Assert.Equal(HttpStatusCode.Forbidden, prepare.StatusCode); return; }
        Assert.True(prepare.StatusCode == HttpStatusCode.OK, await prepare.Content.ReadAsStringAsync());
        request["conditionToken"] = (await prepare.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conditionToken").GetString();
        request["reviewed"] = true;
        using var response = await client.PutAsJsonAsync(path, request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        await using var verify = fixture.CreateContext();
        for (var index = 0; index < places.Length; index++)
        {
            var stored = await verify.Places.AsNoTracking().SingleAsync(p => p.Id == places[index].Id);
            Assert.Equal(index + 1, stored.DisplayOrder);
            stored.DisplayOrder = places[index].DisplayOrder;
            Assert.Equal(JsonSerializer.Serialize(Material(places[index])), JsonSerializer.Serialize(Material(stored)));
        }
        Assert.Equal(before, await GraphStateAsync(graph.TripId));
        if (canonical) Assert.Equal(17, (await verify.Regions.FindAsync(regionId))!.DisplayOrder);
    }

    /// <summary>Paired disjoint scalar/coordinate saves survive either save order; prepare never copies a newer scalar into changes.</summary>
    [PostgresTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task CoordinateAndScalar_BothSaveOrders(bool structuralFirst, bool webStructural)
    {
        var place = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, place.UserId);
        using var bearer = Bearer(app, place.UserId);
        var request = CoordinateRequest(place, webStructural, 15, 23);
        await PrepareStructureAsync(webStructural ? web : bearer, place, webStructural, "coordinates", request);
        async Task Scalar()
        {
            using var response = webStructural
                ? await PatchAsync(bearer, place, false, new { notes = place.Notes }, new { notes = "Mobile notes" })
                : await PatchAsync(web, place, true, new { notesHtml = place.Notes }, new { notesHtml = "Mobile notes" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        if (!structuralFirst) await Scalar();
        using var coordinates = webStructural ? await web.PutAsJsonAsync(StructurePath(place, true, "coordinates"), request)
            : await bearer.PostAsJsonAsync(StructurePath(place, false, "coordinates"), request);
        Assert.True(coordinates.StatusCode == HttpStatusCode.OK, await coordinates.Content.ReadAsStringAsync());
        if (structuralFirst) await Scalar();
        await using var verify = fixture.CreateContext();
        var current = await verify.Places.SingleAsync(p => p.Id == place.Id);
        Assert.Equal(("Mobile notes", 15d, 23d, place.Address), (current.Notes, current.Location!.Y, current.Location.X, current.Address));
    }

    private static object Material(Place place) => new { id = place.Id, regionId = place.RegionId, name = place.Name,
        notes = RichNotes.Normalize(place.Notes), address = place.Address, location = Coordinate(place), iconName = place.IconName,
        markerColor = place.MarkerColor, displayOrder = place.DisplayOrder, resolvedFeatureName = place.ResolvedFeatureName, resolvedFeatureType = place.ResolvedFeatureType };
    private static object? Coordinate(Place place) => place.Location == null ? null : new { latitude = place.Location.Y, longitude = place.Location.X };
    private static Dictionary<string, object?> CoordinateRequest(Place place, bool web, double latitude, double longitude) => web
        ? new() { ["expected"] = new { regionId = place.RegionId, location = Coordinate(place) }, ["changes"] = new { location = new { latitude, longitude } } }
        : new() { ["expected"] = new { regionId = place.RegionId, location = Coordinate(place) }, ["location"] = new { latitude, longitude } };
    private async Task<Dictionary<string, object?>> MoveRequestAsync(Place place, Guid destinationId, bool web)
    {
        await using var db = fixture.CreateContext();
        var places = await db.Places.AsNoTracking().Where(p => p.RegionId == place.RegionId || p.RegionId == destinationId).ToArrayAsync();
        var original = new[] { place.RegionId, destinationId }.ToDictionary(id => id,
            id => places.Where(p => p.RegionId == id).OrderBy(p => p.Id).Select(p => new { id = p.Id, displayOrder = p.DisplayOrder }).ToArray());
        var orders = new[] { place.RegionId, destinationId }.ToDictionary(id => id, id => places.Where(p => p.RegionId == id && p.Id != place.Id)
            .OrderBy(p => p.DisplayOrder == null).ThenBy(p => p.DisplayOrder).ThenBy(p => p.Id).Select(p => p.Id)
            .Concat(id == destinationId ? new[] { place.Id } : []).ToArray());
        Dictionary<string, object?> request = new() { ["expected"] = new { place = Material(place), placesByRegion = original }, ["placeIdsByRegion"] = orders };
        if (web) request["changes"] = new { regionId = destinationId };
        else request["destinationRegionId"] = destinationId;
        return request;
    }
    private static string StructurePath(Place place, bool web, string operation) => web
        ? $"/api/trips/{place.Region.TripId}/editor/places/{place.Id}"
        : $"/api/trips/{place.Region.TripId}/conditional/places/{place.Id}/{operation}";
    private static string OrderPath(Guid tripId, Guid regionId, bool web) =>
        $"/api/trips/{tripId}/{(web ? "editor" : "conditional")}/regions/{regionId}/places/order";
    private static async Task PrepareStructureAsync(HttpClient client, Place place, bool web, string operation, Dictionary<string, object?> request)
    {
        using var prepare = await client.PostAsJsonAsync(StructurePath(place, web, operation) + "/prepare", request);
        Assert.True(prepare.StatusCode == HttpStatusCode.OK, await prepare.Content.ReadAsStringAsync());
        var preview = await prepare.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(preview.GetProperty("requiresReview").GetBoolean());
        request["conditionToken"] = preview.GetProperty("conditionToken").GetString(); request["reviewed"] = true;
    }
    private static async Task<HttpResponseMessage> ApplyStructureAsync(HttpClient client, Place place, bool web, string operation, Dictionary<string, object?> request)
    {
        await PrepareStructureAsync(client, place, web, operation, request);
        return web ? await client.PutAsJsonAsync(StructurePath(place, web, operation), request) : await client.PostAsJsonAsync(StructurePath(place, web, operation), request);
    }
    private static async Task AddCredentialAsync(ApplicationDbContext db, string userId)
    {
        db.ApiTokens.Add(new ApiToken { UserId = userId, User = null!, Name = "Place structural qualification", TokenHash = ApiTokenService.HashToken(userId) });
        await db.SaveChangesAsync();
    }
    /// <summary>Exact route/waypoint/visit and unrelated Area/Region state, deliberately excluding intended Place order/membership.</summary>
    private async Task<string> GraphStateAsync(Guid tripId)
    {
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT string_agg(row, ',' ORDER BY row) FROM (
              SELECT 's:' || to_jsonb(s)::text AS row FROM "Segments" s WHERE s."TripId" = $1
              UNION ALL SELECT 'w:' || to_jsonb(w)::text FROM "SegmentWaypoints" w JOIN "Segments" s ON s."Id" = w."SegmentId" WHERE s."TripId" = $1
              UNION ALL SELECT 'v:' || to_jsonb(v)::text FROM "PlaceVisitEvents" v WHERE v."TripIdSnapshot" = $1
              UNION ALL SELECT 'a:' || to_jsonb(a)::text FROM "Areas" a JOIN "Regions" r ON r."Id" = a."RegionId" WHERE r."TripId" = $1
              UNION ALL SELECT 'r:' || to_jsonb(r)::text FROM "Regions" r WHERE r."TripId" = $1
            ) preserved
            """, connection);
        command.Parameters.AddWithValue(tripId);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
