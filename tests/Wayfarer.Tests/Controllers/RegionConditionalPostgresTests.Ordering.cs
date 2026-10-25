using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Models;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class RegionConditionalPostgresTests
{
    /// <summary>Both routed dialects preserve the populated graph, historical canonical order and current root metadata/freshness.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RegionOrder_PreservesPopulatedGraphAndCanonicalStoredOrder(bool web)
    {
        var graph = await RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext();
        db.ApiTokens.Add(new ApiToken { UserId = graph.UserId, User = null!, Name = "Region order", TokenHash = ApiTokenService.HashToken(graph.UserId) });
        await db.SaveChangesAsync();
        var future = DateTime.UtcNow.Date.AddDays(1);
        await db.Trips.Where(t => t.Id == graph.TripId).ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, future));
        var request = await RegionOrderRequestAsync(graph.TripId);
        var before = await RegionOrderStoredAsync(graph.TripId, true);
        var probe = new RegionProbe();
        await using var app = await StartAsync(db, [probe]);
        using var client = web ? await WebAsync(app, graph.UserId) : Bearer(app, graph.UserId);
        using (var editorClient = await WebAsync(app, graph.UserId))
        using (var state = await editorClient.GetAsync($"/api/trips/{graph.TripId}/editor"))
        {
            var canonical = (await state.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("regionsById").GetProperty(graph.DestinationId!.Value.ToString());
            Assert.True(canonical.GetProperty("isShadow").GetBoolean());
            Assert.False(canonical.GetProperty("capabilities").GetProperty("canReorder").GetBoolean());
            Assert.Equal(17, canonical.GetProperty("displayOrder").GetInt32());
        }
        await PrepareRegionOrderAsync(client, graph.TripId, web, request);
        Assert.Equal(before, await RegionOrderStoredAsync(graph.TripId, true));
        using var response = await client.PutAsJsonAsync(RegionOrderPath(graph.TripId, web), request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var reply = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("bd2-1", reply.GetProperty("contract").GetString());
        Assert.Equal(graph.TripId, reply.GetProperty("data").GetProperty("tripId").GetGuid());
        if (!web) Assert.True(reply.GetProperty("applied").GetBoolean());
        await using var verify = fixture.CreateContext();
        var rows = await verify.Regions.Where(r => r.TripId == graph.TripId).ToArrayAsync();
        var ids = (Guid[])request["regionIds"]!;
        Assert.All(rows.Where(r => r.Name != "Unassigned Places"), r => Assert.Equal(Array.IndexOf(ids, r.Id) + 1, r.DisplayOrder));
        Assert.Equal(17, rows.Single(r => r.Name == "Unassigned Places").DisplayOrder);
        Assert.Equal(before, await RegionOrderStoredAsync(graph.TripId, true));
        Assert.Equal(future, (await verify.Trips.FindAsync(graph.TripId))!.UpdatedAt);
        Assert.All(probe.Updates.Where(sql => sql.Contains("UPDATE \"Regions\"")), sql =>
        {
            Assert.Contains("\"DisplayOrder\" =", sql);
            Assert.DoesNotContain("\"Notes\" =", sql);
            Assert.DoesNotContain("\"Name\" =", sql);
        });
    }

    /// <summary>Reordering followed by independent Notes succeeds; material Notes first invalidates the retained order in either direction.</summary>
    [PostgresTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task RegionOrder_ReciprocalMaterialSaveOrders(bool orderWeb, bool orderFirst)
    {
        var region = await SeedAsync();
        var request = await RegionOrderRequestAsync(region.TripId);
        await using var db = fixture.CreateContext(); await using var app = await StartAsync(db);
        using var web = await WebAsync(app, region.UserId); using var bearer = Bearer(app, region.UserId);
        var orderClient = orderWeb ? web : bearer;
        await PrepareRegionOrderAsync(orderClient, region.TripId, orderWeb, request);
        async Task Notes()
        {
            var key = orderWeb ? "notes" : "notesHtml";
            using var result = await PatchAsync(orderWeb ? bearer : web, region, !orderWeb,
                new Dictionary<string, object?> { [key] = region.Notes }, new Dictionary<string, object?> { [key] = "Private newer notes" });
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        }
        if (!orderFirst) await Notes();
        var before = await RegionOrderStoredAsync(region.TripId);
        using var order = await orderClient.PutAsJsonAsync(RegionOrderPath(region.TripId, orderWeb), request);
        Assert.Equal(orderFirst ? HttpStatusCode.OK : HttpStatusCode.Conflict, order.StatusCode);
        if (orderFirst) await Notes();
        else
        {
            Assert.DoesNotContain("Private newer notes", await order.Content.ReadAsStringAsync());
            Assert.Equal(before, await RegionOrderStoredAsync(region.TripId));
            using var stalePrepare = await orderClient.PostAsJsonAsync(RegionOrderPath(region.TripId, orderWeb) + "/prepare", request);
            Assert.Equal(HttpStatusCode.Conflict, stalePrepare.StatusCode);
        }
        await using var verify = fixture.CreateContext();
        Assert.Equal("Private newer notes", (await verify.Regions.FindAsync(region.Id))!.Notes);
    }

    /// <summary>Invalid complete vectors, duplicate originals and cross-scope IDs cannot prepare, disclose or partially write.</summary>
    [PostgresTheory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("canonical")]
    [InlineData("duplicate-original")]
    [InlineData("malformed")]
    public async Task RegionOrder_RejectsInvalidVectorWithoutDisclosure(string kind)
    {
        var graph = await RegionDeletionTestGraph.SeedAsync(fixture);
        var foreign = await SeedAsync();
        await using var db = fixture.CreateContext(); await using var app = await StartAsync(db);
        using var web = await WebAsync(app, graph.UserId);
        var request = await RegionOrderRequestAsync(graph.TripId);
        var ids = (Guid[])request["regionIds"]!;
        if (kind == "duplicate-original")
        {
            var expected = JsonSerializer.SerializeToElement(request["expected"], new JsonSerializerOptions(JsonSerializerDefaults.Web));
            request["expected"] = new { regions = new[] { expected.GetProperty("regions")[0], expected.GetProperty("regions")[0] }, canonical = expected.GetProperty("canonical") };
        }
        else request["regionIds"] = kind switch
        {
            "missing" => ids.Take(1).ToArray(), "duplicate" => new[] { ids[0], ids[0] },
            "foreign" => new[] { ids[0], foreign.Id }, "canonical" => new[] { ids[0], graph.DestinationId!.Value }, _ => new[] { "not-an-id" }
        };
        var before = await RegionOrderStoredAsync(graph.TripId);
        foreach (var prepare in new[] { true, false })
        {
            using var response = prepare ? await web.PostAsJsonAsync(RegionOrderPath(graph.TripId, true) + "/prepare", request)
                : await web.PutAsJsonAsync(RegionOrderPath(graph.TripId, true), request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(foreign.Id.ToString(), body);
            Assert.DoesNotContain("regionOrder", body);
        }
        Assert.Equal(before, await RegionOrderStoredAsync(graph.TripId));
    }

    /// <summary>Membership, normal/canonical orders and material drift after preparation reject with no reorder or freshness writes.</summary>
    [PostgresTheory]
    [InlineData("membership")]
    [InlineData("normal-order")]
    [InlineData("canonical-order")]
    [InlineData("canonical-identity")]
    [InlineData("cover")]
    public async Task RegionOrder_RejectsDriftWithoutRepair(string drift)
    {
        var graph = await RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext(); await using var app = await StartAsync(db);
        using var web = await WebAsync(app, graph.UserId);
        var request = await RegionOrderRequestAsync(graph.TripId);
        await PrepareRegionOrderAsync(web, graph.TripId, true, request);
        if (drift == "membership") db.Regions.Add(new Region { Id = Guid.NewGuid(), UserId = graph.UserId, TripId = graph.TripId, Name = "Added", DisplayOrder = 40 });
        if (drift == "canonical-identity")
        {
            await db.Regions.Where(r => r.Id == graph.DestinationId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Name, "Former canonical"));
            db.Regions.Add(new Region { Id = Guid.NewGuid(), UserId = graph.UserId, TripId = graph.TripId, Name = "Unassigned Places", DisplayOrder = 17 });
        }
        if (drift.EndsWith("order")) await db.Regions.Where(r => r.Id == (drift == "normal-order" ? graph.SourceId : graph.DestinationId)).ExecuteUpdateAsync(s => s.SetProperty(r => r.DisplayOrder, 77));
        if (drift == "cover") await db.Regions.Where(r => r.Id == graph.SourceId).ExecuteUpdateAsync(s => s.SetProperty(r => r.CoverImageUrl, "Private newer cover"));
        await db.SaveChangesAsync();
        var before = await RegionOrderStoredAsync(graph.TripId);
        using var response = await web.PutAsJsonAsync(RegionOrderPath(graph.TripId, true), request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("Private newer cover", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await RegionOrderStoredAsync(graph.TripId));
    }

    /// <summary>A failed commit rolls back every normal order and Trip freshness together.</summary>
    [PostgresFact]
    public async Task RegionOrder_CommitFailureRollsBack()
    {
        var region = await SeedAsync();
        await using var db = fixture.CreateContext();
        await db.Regions.Where(r => r.Id == region.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.DisplayOrder, 33));
        var request = await RegionOrderRequestAsync(region.TripId);
        var before = await RegionOrderStoredAsync(region.TripId);
        await using var app = await StartAsync(db, [new FailRegionCommit()]);
        using var web = await WebAsync(app, region.UserId);
        await PrepareRegionOrderAsync(web, region.TripId, true, request);
        using var response = await web.PutAsJsonAsync(RegionOrderPath(region.TripId, true), request);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(before, await RegionOrderStoredAsync(region.TripId));
    }

    /// <summary>A real routed creation queued behind reorder rejects its old append base without ghost Regions or replay.</summary>
    [PostgresFact]
    public async Task RegionOrder_ConcurrentCreationWaitsThenRejectsStaleAppend()
    {
        var region = await SeedAsync();
        await using var db = fixture.CreateContext();
        await db.Regions.Where(r => r.Id == region.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.DisplayOrder, 33));
        var request = await RegionOrderRequestAsync(region.TripId);
        var orderProbe = new RegionProbe(); var createProbe = new RegionProbe();
        await using var orderApp = await StartAsync(db, [orderProbe]);
        await using var createApp = await StartAsync(db, [createProbe]);
        using var web = await WebAsync(orderApp, region.UserId); using var bearer = Bearer(createApp, region.UserId);
        await PrepareRegionOrderAsync(web, region.TripId, true, request);
        await using var blocker = fixture.CreateContext(); await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Trips\" WHERE \"Id\" = {region.TripId} FOR UPDATE");
        var order = web.PutAsJsonAsync(RegionOrderPath(region.TripId, true), request);
        await orderProbe.LockStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); await WaitForBlockAsync(orderProbe.ProcessId);
        var create = bearer.PostAsJsonAsync($"/api/trips/{region.TripId}/conditional/regions", new
        {
            expected = new { regions = new[] { new { id = region.Id, displayOrder = 33, isCanonical = false } } },
            value = new { name = "Queued creation", notes = (string?)null }
        });
        await createProbe.LockStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); await WaitForBlockAsync(createProbe.ProcessId);
        await transaction.CommitAsync();
        using var ordered = await order; using var created = await create;
        Assert.Equal(HttpStatusCode.OK, ordered.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, created.StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.Equal(1, (await verify.Regions.SingleAsync(r => r.TripId == region.TripId)).DisplayOrder);
        Assert.Empty(createProbe.Updates);
    }

    /// <summary>Retains cookie/User/antiforgery versus incoming bearer, and conceals foreign Trip scope before parsing.</summary>
    [PostgresFact]
    public async Task RegionOrder_AuthorityAndBypasses()
    {
        var region = await SeedAsync(); var foreign = await SeedAsync();
        await using var db = fixture.CreateContext(); await using var app = await StartAsync(db);
        using var web = await WebAsync(app, region.UserId); using var bearer = Bearer(app, region.UserId);
        var request = await RegionOrderRequestAsync(region.TripId);
        using var cookieOnly = await web.PostAsJsonAsync(RegionOrderPath(region.TripId, false) + "/prepare", request);
        Assert.Equal(HttpStatusCode.Unauthorized, cookieOnly.StatusCode);
        web.DefaultRequestHeaders.Remove("RequestVerificationToken");
        using var noAnti = await web.PostAsJsonAsync(RegionOrderPath(region.TripId, true) + "/prepare", request);
        Assert.Equal(HttpStatusCode.BadRequest, noAnti.StatusCode);
        using var concealed = await bearer.PutAsJsonAsync(RegionOrderPath(foreign.TripId, false), new { invalid = true });
        Assert.Equal(HttpStatusCode.NotFound, concealed.StatusCode);
        using var bypass = await bearer.PutAsJsonAsync($"/api/trips/regions/{region.Id}", new { displayOrder = 99, notes = "Must not write" });
        Assert.Equal(HttpStatusCode.Conflict, bypass.StatusCode);
        Assert.Equal("mutation-originals-required", (await bypass.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        await PrepareRegionOrderAsync(bearer, region.TripId, false, request);
        await db.ApiTokens.Where(t => t.UserId == region.UserId).ExecuteDeleteAsync();
        using var revoked = await bearer.PutAsJsonAsync(RegionOrderPath(region.TripId, false), request);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.Equal(region.Notes, (await verify.Regions.FindAsync(region.Id))!.Notes);
    }

    /// <summary>Final rediscovery after a real Region lock wait rejects material, authority and deletion drift.</summary>
    [PostgresTheory]
    [InlineData("content")]
    [InlineData("token")]
    [InlineData("deleted")]
    public async Task RegionOrder_FinalLockRechecksOriginalsAndAuthority(string drift)
    {
        var region = await SeedAsync();
        var request = await RegionOrderRequestAsync(region.TripId);
        var probe = new RegionProbe();
        await using var db = fixture.CreateContext(); await using var app = await StartAsync(db, [probe]);
        using var bearer = Bearer(app, region.UserId);
        await PrepareRegionOrderAsync(bearer, region.TripId, false, request);
        await using var blocker = fixture.CreateContext(); await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Regions\" WHERE \"Id\" = {region.Id} FOR UPDATE");
        var pending = bearer.PutAsJsonAsync(RegionOrderPath(region.TripId, false), request);
        await probe.LockStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); await WaitForBlockAsync(probe.ProcessId);
        if (drift == "content") await blocker.Regions.Where(r => r.Id == region.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Notes, "Private later Notes"));
        if (drift == "token") await blocker.ApiTokens.Where(t => t.UserId == region.UserId).ExecuteDeleteAsync();
        if (drift == "deleted") await blocker.Regions.Where(r => r.Id == region.Id).ExecuteDeleteAsync();
        await transaction.CommitAsync();
        var before = await RegionOrderStoredAsync(region.TripId);
        using var response = await pending;
        Assert.Equal(drift == "token" ? HttpStatusCode.Unauthorized : HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("Private later Notes", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await RegionOrderStoredAsync(region.TripId));
        Assert.Empty(probe.Updates);
    }

    /// <summary>Prepared evidence cannot authorize altered intent, another dialect or an unauthentic token.</summary>
    [PostgresTheory]
    [InlineData("tampered")]
    [InlineData("intent")]
    [InlineData("dialect")]
    public async Task RegionOrder_ConditionsBindIntentAndDialect(string variant)
    {
        var region = await SeedAsync();
        await using var db = fixture.CreateContext();
        db.Regions.Add(new Region { Id = Guid.NewGuid(), TripId = region.TripId, UserId = region.UserId, Name = "Second", DisplayOrder = 7 });
        await db.SaveChangesAsync();
        var request = await RegionOrderRequestAsync(region.TripId);
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, region.UserId); using var bearer = Bearer(app, region.UserId);
        await PrepareRegionOrderAsync(web, region.TripId, true, request);
        if (variant == "tampered") request["conditionToken"] = "tampered";
        if (variant == "intent") request["regionIds"] = ((Guid[])request["regionIds"]!).Reverse().ToArray();
        var before = await RegionOrderStoredAsync(region.TripId);
        using var response = await (variant == "dialect" ? bearer : web).PutAsJsonAsync(RegionOrderPath(region.TripId, variant != "dialect"), request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, await RegionOrderStoredAsync(region.TripId));
    }

    /// <summary>Builds exact originals from persisted state, retaining historical orders/canonical identity.</summary>
    private async Task<Dictionary<string, object?>> RegionOrderRequestAsync(Guid tripId)
    {
        await using var db = fixture.CreateContext();
        var regions = await db.Regions.AsNoTracking().Where(r => r.TripId == tripId).OrderBy(r => r.Id).ToArrayAsync();
        return new() { ["expected"] = new { regions = regions.Where(r => !Wayfarer.Models.Dtos.RegionOrderRequest.IsCanonical(r)).Select(Wayfarer.Models.Dtos.RegionOrderRequest.Original).ToArray(),
            canonical = Wayfarer.Models.Dtos.RegionOrderRequest.CanonicalOriginal(regions) },
            ["regionIds"] = regions.Where(r => r.Name != "Unassigned Places").OrderByDescending(r => r.DisplayOrder).Select(r => r.Id).ToArray() };
    }

    /// <summary>Checks preparation through production routing before retaining the exact same client originals for apply.</summary>
    private static async Task PrepareRegionOrderAsync(HttpClient client, Guid tripId, bool web, Dictionary<string, object?> request)
    {
        using var response = await client.PostAsJsonAsync(RegionOrderPath(tripId, web) + "/prepare", request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var value = await response.Content.ReadFromJsonAsync<JsonElement>();
        request["conditionToken"] = value.GetProperty("conditionToken").GetString(); request["reviewed"] = true;
    }

    /// <summary>Keeps Web/bearer URL selection explicit at the routed test boundary.</summary>
    private static string RegionOrderPath(Guid tripId, bool web) => $"/api/trips/{tripId}/{(web ? "editor" : "conditional")}/regions/order";

    /// <summary>Fresh persisted rows and row versions prove no partial writes; preservation mode ignores only normal order/root freshness.</summary>
    private async Task<string?> RegionOrderStoredAsync(Guid tripId, bool preservation = false)
    {
        await using var connection = fixture.CreateConnection(); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT string_agg(row, ',' ORDER BY row) FROM (
              SELECT 'place:' || to_jsonb(p)::text || ':' || p.xmin::text AS row FROM "Places" p JOIN "Regions" r ON r."Id" = p."RegionId" WHERE r."TripId" = $1
              UNION ALL SELECT 'area:' || to_jsonb(a)::text || ':' || a.xmin::text FROM "Areas" a JOIN "Regions" r ON r."Id" = a."RegionId" WHERE r."TripId" = $1
              UNION ALL SELECT 'segment:' || to_jsonb(s)::text || ':' || s.xmin::text FROM "Segments" s WHERE s."TripId" = $1
              UNION ALL SELECT 'waypoint:' || to_jsonb(w)::text || ':' || w.xmin::text FROM "SegmentWaypoints" w JOIN "Segments" s ON s."Id" = w."SegmentId" WHERE s."TripId" = $1
              UNION ALL SELECT 'visit:' || to_jsonb(v)::text || ':' || v.xmin::text FROM "PlaceVisitEvents" v WHERE v."TripIdSnapshot" = $1
              UNION ALL SELECT 'region:' || CASE WHEN $2 AND r."Name" <> 'Unassigned Places' THEN (to_jsonb(r) - 'DisplayOrder')::text ELSE to_jsonb(r)::text || ':' || r.xmin::text END FROM "Regions" r WHERE r."TripId" = $1
              UNION ALL SELECT 'trip:' || CASE WHEN $2 THEN (to_jsonb(t) - 'UpdatedAt')::text ELSE to_jsonb(t)::text END FROM "Trips" t WHERE t."Id" = $1
            ) stored
            """, connection);
        command.Parameters.AddWithValue(tripId); command.Parameters.AddWithValue(preservation);
        return (string?)await command.ExecuteScalarAsync();
    }
}
