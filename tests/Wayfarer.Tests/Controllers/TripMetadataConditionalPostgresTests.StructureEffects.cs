using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Planning;
using Wayfarer.Services.TripMetadataReceipts;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Models;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class TripMetadataConditionalPostgresTests
{
    /// <summary>All four approved choices capture only physically removed dependent Planning and actual visited-link effects.</summary>
    [PostgresTheory]
    [InlineData("keep", "keep")]
    [InlineData("keep", "delete")]
    [InlineData("delete", "keep")]
    [InlineData("delete", "delete")]
    public async Task StructureReceipt_RegionDeletionPreservesAllFourChoices(string places, string areas)
    {
        var graph = await RegionDeletionTestGraph.SeedAsync(fixture, destination: false);
        var plan = new PlanningAggregate(1, [new(Guid.NewGuid(), new("unspecified"))]);
        await using var db = fixture.CreateContext();
        await db.Regions.Where(row => row.Id == graph.SourceId).ExecuteUpdateAsync(set => set.SetProperty(row => row.Planning, plan));
        await db.Places.Where(row => graph.PlaceIds.Contains(row.Id)).ExecuteUpdateAsync(set => set.SetProperty(row => row.Planning, plan));
        await db.Areas.Where(row => graph.AreaIds.Contains(row.Id)).ExecuteUpdateAsync(set => set.SetProperty(row => row.Planning, plan));
        await db.Segments.Where(row => graph.EndpointIds.Concat(graph.SurvivorIds).Contains(row.Id))
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Planning, plan));

        var dropReply = places == "delete" && areas == "keep";
        if (dropReply)
        {
            var now = DateTime.UtcNow;
            db.PlaceVisitCandidates.Add(new PlaceVisitCandidate { Id = Guid.NewGuid(), UserId = graph.UserId,
                PlaceId = graph.PlaceIds[0], FirstHitUtc = now, LastHitUtc = now, ConsecutiveHits = 1 });
            await db.SaveChangesAsync();
        }
        await using var app = await StartAsync(db, middleware: host => host.Use(async (context, next) =>
        {
            if (!dropReply || !context.Request.Path.Value!.EndsWith("/deletion/receipt", StringComparison.Ordinal))
            {
                await next();
                return;
            }

            dropReply = false;
            var transport = context.Response.Body;
            await using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            try { await next(); }
            finally { context.Response.Body = transport; }
            throw new IOException("Controlled lost response after mixed Region deletion.");
        }));
        using var web = await WebAsync(app, graph.UserId);
        var baseUrl = $"/api/trips/{graph.TripId}/editor/regions/{graph.SourceId}/deletion";
        const string contract = TripMetadataIdentity.StructureVersion;
        using var previewReply = await web.PostAsJsonAsync(baseUrl + "-preview?contract=" + contract, new { places, areas });
        Assert.Equal(HttpStatusCode.OK, previewReply.StatusCode);
        var preview = await previewReply.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, preview.GetProperty("contractVersion").GetInt32());
        Assert.Equal(contract, preview.GetProperty("contract").GetString());
        Assert.Equal(places == "delete" ? 1 : 0, preview.GetProperty("consequences").GetProperty("unlinkedVisits").GetInt32());
        var body = JsonSerializer.Serialize(new { receiptContract = contract, places, areas,
            confirmationToken = preview.GetProperty("confirmation").GetProperty("token").GetString() });
        var identity = new TripMetadataIdentity(contract, graph.UserId, "web", "region-delete", graph.TripId, "", Guid.Empty, default)
            { Target = new("region", graph.SourceId) };
        var route = baseUrl + "/receipt";
        var permit = await TripMetadataReceiptHttp.IssueTypedAsync(web, identity, route, body);
        using var committed = await TripMetadataReceiptHttp.SendAsync(web, HttpMethod.Post, route, permit, body);
        var evidence = places == "delete" && areas == "keep"
            ? await RecoverMixedDeletionAsync(db, app, graph, route, permit, committed)
            : await TripMetadataReceiptHttp.EvidenceAsync(committed, permit, "applied");
        var result = evidence.GetProperty("result");
        Assert.Equal(TripStructureDigests.Planning(plan), result.GetProperty("planningDigest").GetString());
        Assert.Equal(places == "keep" ? graph.PlaceIds.Length : 0, result.GetProperty("movedPlaces").GetArrayLength());
        Assert.Equal(areas == "keep" ? graph.AreaIds.Length : 0, result.GetProperty("movedAreas").GetArrayLength());
        Assert.Equal(places == "delete" ? 1 : 0, result.GetProperty("visitEffects").GetProperty("unlinkedEvents").GetInt32());
        var expected = (places == "delete" ? graph.PlaceIds.Select(id => ((string?)"place", id)).Concat(graph.EndpointIds.Select(id => ((string?)"segment", id))) : [])
            .Concat(areas == "delete" ? graph.AreaIds.Select(id => ((string?)"area", id)) : [])
            .OrderBy(row => row.Item1, StringComparer.Ordinal).ThenBy(row => row.id).ToArray();
        var removed = result.GetProperty("deletedDependentPlanning").EnumerateArray().ToArray();
        Assert.Equal(expected, removed.Select(row => (row.GetProperty("type").GetString(), row.GetProperty("id").GetGuid())).ToArray());
        Assert.All(removed, row => Assert.Equal(TripStructureDigests.Planning(plan), row.GetProperty("planningDigest").GetString()));
        Assert.All(await db.Segments.AsNoTracking().Where(row => graph.SurvivorIds.Contains(row.Id)).ToArrayAsync(),
            row => Assert.True(PlanningCodec.Equal(plan, row.Planning)));
        Assert.False(await db.Regions.AsNoTracking().AnyAsync(row => row.Id == graph.SourceId));
        if (places == "delete" && areas == "keep")
        {
            var destination = result.GetProperty("destinationRegionId").GetGuid();
            Assert.Equal("create", result.GetProperty("destinationMode").GetString());
            Assert.Equal(graph.AreaIds.Length, await db.Areas.CountAsync(row => graph.AreaIds.Contains(row.Id) && row.RegionId == destination));
            Assert.Equal(graph.PlaceIds.Order(), result.GetProperty("deletedPlaceIds").EnumerateArray().Select(row => row.GetGuid()));
            Assert.Equal(graph.EndpointIds.Order(), result.GetProperty("segmentEffects").GetProperty("deletedSegmentIds")
                .EnumerateArray().Select(row => row.GetGuid()));
            Assert.Equal(1, result.GetProperty("visitEffects").GetProperty("deletedCandidates").GetInt32());
            Assert.False(await db.PlaceVisitCandidates.AnyAsync(row => graph.PlaceIds.Contains(row.PlaceId)));
            var visit = await db.PlaceVisitEvents.AsNoTracking().SingleAsync(row => row.TripIdSnapshot == graph.TripId);
            Assert.Null(visit.PlaceId);
            Assert.Equal("Delete source", visit.RegionNameSnapshot);
            Assert.Equal("First", visit.PlaceNameSnapshot);
        }

        using var current = await WebAsync(app, graph.UserId);
        using var replay = await TripMetadataReceiptHttp.SendAsync(current, HttpMethod.Post, route, permit, body);
        Assert.Equal(evidence.GetRawText(), (await TripMetadataReceiptHttp.EvidenceAsync(replay, permit, "applied")).GetRawText());
    }

    /// <summary>Recovers the historical outcome with a replacement credential after the source was removed.</summary>
    private static async Task<JsonElement> RecoverMixedDeletionAsync(ApplicationDbContext db, WebApplication app,
        RegionDeletionTestGraph graph, string route, JsonElement permit, HttpResponseMessage lost)
    {
        Assert.Equal(HttpStatusCode.InternalServerError, lost.StatusCode);
        await db.Users.Where(row => row.Id == graph.UserId).ExecuteUpdateAsync(set => set.SetProperty(row => row.SecurityStamp, Guid.NewGuid().ToString("N")));
        using var replacement = await WebAsync(app, graph.UserId);
        using var recovery = await LookupStructureAsync(replacement, route, permit);
        return await TripMetadataReceiptHttp.EvidenceAsync(recovery, permit, "applied");
    }

    /// <summary>Old pages cannot dispatch any of the six dependent mutations with a missing or repeated protocol header.</summary>
    [PostgresFact]
    public async Task StructureReceipt_StaleDependentWebRoutesRefuseBeforeMutation()
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, trip.UserId);
        var segment = Guid.NewGuid();
        var routes = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Patch, $"planning/trip/{trip.Id}"), (HttpMethod.Post, "segments"), (HttpMethod.Put, $"segments/{segment}"),
            (HttpMethod.Patch, $"segments/{segment}/conditional"), (HttpMethod.Delete, $"segments/{segment}"), (HttpMethod.Put, "segments/order")
        };
        foreach (var (method, suffix) in routes)
        {
            using var request = new HttpRequestMessage(method, $"/api/trips/{trip.Id}/editor/{suffix}") { Content = JsonContent.Create(new { }) };
            using var response = await web.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("trip-write-upgrade-required", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        using var duplicate = new HttpRequestMessage(HttpMethod.Patch, $"/api/trips/{trip.Id}/editor/planning/trip/{trip.Id}")
            { Content = JsonContent.Create(new { }) };
        duplicate.Headers.TryAddWithoutValidation("X-Wayfarer-Trip-Dispatch", new[] { "3", "3" });
        using var refusal = await web.SendAsync(duplicate);
        Assert.Equal(HttpStatusCode.Conflict, refusal.StatusCode);
        Assert.Empty(await db.Segments.AsNoTracking().Where(row => row.TripId == trip.Id).ToArrayAsync());
        var saved = await db.Trips.AsNoTracking().SingleAsync(row => row.Id == trip.Id);
        Assert.Null(saved.Planning);
        Assert.Equal(trip.UpdatedAt, saved.UpdatedAt);
    }
}
