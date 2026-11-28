using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services.TripMetadataReceipts;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Models;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class TripMetadataConditionalPostgresTests
{
    /// <summary>A reviewed Web move and coordinate edit retain their historical parent, vectors and route effects after later owner changes.</summary>
    [PostgresFact]
    public async Task StructureReceipt_LostMoveReplyRecoversHistoricalParentAndRoutes()
    {
        var graph = await RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext();
        var trip = await db.Trips.AsNoTracking().SingleAsync(row => row.Id == graph.TripId);
        var place = await db.Places.AsNoTracking().SingleAsync(row => row.Id == graph.PlaceIds[0]);
        var destination = graph.DestinationId ?? throw new InvalidOperationException("Move fixture requires a destination.");
        var siblings = await db.Places.AsNoTracking().Where(row => row.RegionId == place.RegionId || row.RegionId == destination).ToArrayAsync();
        var originals = siblings.GroupBy(row => row.RegionId).ToDictionary(group => group.Key.ToString("D"),
            group => group.Select(row => new { row.Id, row.DisplayOrder }).ToArray());
        var orders = new Dictionary<string, Guid[]>
        {
            [place.RegionId.ToString("D")] = siblings.Where(row => row.RegionId == place.RegionId && row.Id != place.Id).Select(row => row.Id).ToArray(),
            [destination.ToString("D")] = siblings.Where(row => row.RegionId == destination).Select(row => row.Id).Append(place.Id).ToArray()
        };
        var expected = new { place = PlaceStructuralRequest.Original(place), placesByRegion = originals };
        var changes = new { regionId = destination, location = new { latitude = 4d, longitude = 4d } };
        var storedManualDuration = await db.Segments.AsNoTracking().Where(row => graph.SurvivorIds.Contains(row.Id)
            && row.EstimatedDurationSource == EstimatedDurationSource.Manual).Select(row => row.EstimatedDuration).SingleAsync();
        var drop = true;
        await using var app = await StartAsync(db, middleware: host => host.Use(async (context, next) =>
        {
            if (!drop || context.Request.Method != "PUT") { await next(); return; }
            drop = false;
            var transport = context.Response.Body;
            await using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            try { await next(); }
            finally { context.Response.Body = transport; }
            throw new IOException("Controlled lost reply after typed move commit.");
        }));
        using var web = await WebAsync(app, trip.UserId);
        var path = $"/api/trips/{trip.Id}/editor/places/{place.Id}";
        using var preview = await web.PostAsJsonAsync(path + "/prepare", new { expected, changes, placeIdsByRegion = orders }, JsonSerializerOptions.Web);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var approval = await preview.Content.ReadFromJsonAsync<JsonElement>();
        var body = JsonSerializer.Serialize(new { receiptContract = TripMetadataIdentity.StructureVersion, expected, changes,
            placeIdsByRegion = orders, conditionToken = approval.GetProperty("conditionToken").GetString(), reviewed = true }, JsonSerializerOptions.Web);
        var route = path + "/receipt";
        var identity = new TripMetadataIdentity(TripMetadataIdentity.StructureVersion, trip.UserId, "web", "place-save",
            trip.Id, "", Guid.Empty, default) { Target = new("place", place.Id) };
        var permit = await TripMetadataReceiptHttp.IssueTypedAsync(web, identity, route, body);

        using var lost = await TripMetadataReceiptHttp.SendAsync(web, HttpMethod.Put, route, permit, body);
        Assert.Equal(HttpStatusCode.InternalServerError, lost.StatusCode);
        var saved = await db.Places.AsNoTracking().SingleAsync(row => row.Id == place.Id);
        Assert.Equal(destination, saved.RegionId);
        await db.Places.Where(row => row.Id == place.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.RegionId, place.RegionId));
        using var recovery = await LookupStructureAsync(web, route, permit);
        var evidence = await TripMetadataReceiptHttp.EvidenceAsync(recovery, permit, "applied");
        var result = evidence.GetProperty("result");
        Assert.Equal(place.RegionId, result.GetProperty("fromRegionId").GetGuid());
        Assert.Equal(destination, result.GetProperty("regionId").GetGuid());
        Assert.Equal(2, result.GetProperty("placeOrders").GetArrayLength());
        Assert.Equal(TripStructureDigests.Place(saved, "location"), result.GetProperty("fieldDigests").GetProperty("location").GetString());
        var savedSegments = result.GetProperty("segmentEffects").GetProperty("updatedSegments").EnumerateArray().ToArray();
        var manual = Assert.Single(savedSegments, row => row.GetProperty("estimatedDurationSource").GetString() == "Manual");
        Assert.Equal(storedManualDuration?.Ticks.ToString(CultureInfo.InvariantCulture), manual.GetProperty("estimatedDurationTicks").GetString());
        Assert.True(TripMetadataIdentity.IsDigest(manual.GetProperty("route").GetProperty("effectiveDigest").GetString()));
        var vectors = result.GetProperty("placeOrders").EnumerateArray().ToArray();
        foreach (var vector in vectors)
        {
            var regionId = vector.GetProperty("regionId").GetGuid().ToString("D");
            Assert.Equal(orders[regionId], vector.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetGuid()).ToArray());
        }

        using var ownerReply = await web.GetAsync($"/api/trips/{trip.Id}/editor");
        Assert.Equal(HttpStatusCode.OK, ownerReply.StatusCode);
        var owner = await ownerReply.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(place.RegionId, owner.GetProperty("placesById").GetProperty(place.Id.ToString("D")).GetProperty("regionId").GetGuid());
        using var replay = await TripMetadataReceiptHttp.SendAsync(web, HttpMethod.Put, route, permit, body);
        Assert.Equal(evidence.GetRawText(), (await TripMetadataReceiptHttp.EvidenceAsync(replay, permit, "applied")).GetRawText());
        Assert.Equal(place.RegionId, await db.Places.Where(row => row.Id == place.Id).Select(row => row.RegionId).SingleAsync());
    }
}
