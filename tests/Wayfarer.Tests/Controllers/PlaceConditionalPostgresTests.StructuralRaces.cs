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
    /// <summary>Move binds full target content/parent; order binds target content while a later disjoint Notes patch remains valid.</summary>
    [PostgresTheory]
    [InlineData("move", false, false)]
    [InlineData("move", false, true)]
    [InlineData("move", true, false)]
    [InlineData("move", true, true)]
    [InlineData("order", false, false)]
    [InlineData("order", false, true)]
    [InlineData("order", true, false)]
    [InlineData("order", true, true)]
    public async Task MoveAndOrder_ReciprocalNotesSaveOrders(string operation, bool webStructural, bool structuralFirst)
    {
        var graph = await RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext();
        var place = await db.Places.Include(p => p.Region).SingleAsync(p => p.Id == graph.PlaceIds[0]);
        await AddCredentialAsync(db, graph.UserId);
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, graph.UserId); using var bearer = Bearer(app, graph.UserId);
        var client = webStructural ? web : bearer;
        var path = operation == "move" ? StructurePath(place, webStructural, operation) : OrderPath(graph.TripId, graph.SourceId, webStructural);
        var places = await db.Places.AsNoTracking().Where(p => p.RegionId == graph.SourceId).OrderBy(p => p.Id).ToArrayAsync();
        var request = operation == "move" ? await MoveRequestAsync(place, graph.DestinationId!.Value, webStructural)
            : new Dictionary<string, object?> { ["expected"] = new { places = places.Select(Material).ToArray() }, ["placeIds"] = places.Select(p => p.Id).Reverse().ToArray() };
        using var prepare = await client.PostAsJsonAsync(path + "/prepare", request);
        Assert.True(prepare.IsSuccessStatusCode, await prepare.Content.ReadAsStringAsync());
        request["conditionToken"] = (await prepare.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conditionToken").GetString(); request["reviewed"] = true;
        async Task<HttpResponseMessage> Structure() => operation == "order" || webStructural
            ? await client.PutAsJsonAsync(path, request) : await client.PostAsJsonAsync(path, request);
        Task<HttpResponseMessage> Notes() => webStructural
            ? PatchAsync(bearer, place, false, new { notes = place.Notes }, new { notes = "Opposite Notes" })
            : PatchAsync(web, place, true, new { notesHtml = place.Notes }, new { notesHtml = "Opposite Notes" });
        using var first = structuralFirst ? await Structure() : await Notes(); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var second = structuralFirst ? await Notes() : await Structure();
        Assert.Equal(operation == "order" && structuralFirst ? HttpStatusCode.OK : HttpStatusCode.Conflict, second.StatusCode);
        await using var verify = fixture.CreateContext(); var current = await verify.Places.SingleAsync(p => p.Id == place.Id);
        Assert.Equal(structuralFirst && operation == "move" ? place.Notes : "Opposite Notes", current.Notes);
        Assert.Equal(structuralFirst && operation == "move" ? graph.DestinationId!.Value : graph.SourceId, current.RegionId);
    }

    /// <summary>Profile dependency changes committed during a real Segment wait cannot be used without their earlier-class lock.</summary>
    [PostgresFact]
    public async Task Coordinates_ProfileDriftDuringSegmentWaitRejects()
    {
        var graph = await RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext();
        var place = await db.Places.Include(p => p.Region).SingleAsync(p => p.Id == graph.PlaceIds[0]);
        await AddCredentialAsync(db, graph.UserId);
        var profile = new TransportProfile { Id = Guid.NewGuid(), Key = "bd2-" + Guid.NewGuid().ToString("N"), Label = "Drift profile", Category = "ground", PlanningSpeedKmh = 5 };
        fixture.RegisterTransportProfile(profile.Id); db.Add(profile); await db.SaveChangesAsync();
        var segmentId = await db.Segments.Where(s => s.FromPlaceId == place.Id || s.ToPlaceId == place.Id || s.Waypoints.Any(w => w.PlaceId == place.Id))
            .OrderBy(s => s.Id).Select(s => s.Id).FirstAsync();
        var probe = new PlaceProbe("Segments"); await using var app = await StartAsync(db, [probe]); using var bearer = Bearer(app, graph.UserId);
        var request = CoordinateRequest(place, false, 2.25, 2.25); await PrepareStructureAsync(bearer, place, false, "coordinates", request);
        await using var blocker = fixture.CreateContext(); await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Segments\" WHERE \"Id\" = {segmentId} FOR UPDATE");
        var pending = bearer.PostAsJsonAsync(StructurePath(place, false, "coordinates"), request);
        await WaitForLockAsync(probe, 0);
        await blocker.Segments.Where(s => s.Id == segmentId).ExecuteUpdateAsync(s => s.SetProperty(s => s.TransportProfileId, profile.Id).SetProperty(s => s.Mode, profile.Key));
        await transaction.CommitAsync(); using var result = await pending; Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        await using var verify = fixture.CreateContext(); Assert.Equal(place.Location!.AsText(), (await verify.Places.FindAsync(place.Id))!.Location!.AsText());
        Assert.Empty(probe.Updates);
    }

    /// <summary>Material route/provenance and enrichment drift after prepare rejects without location or derived partial effects.</summary>
    [PostgresTheory]
    [InlineData(false, "route")]
    [InlineData(true, "enrichment")]
    [InlineData(false, "anchor")]
    [InlineData(true, "parent")]
    public async Task Coordinates_RejectMaterialDrift(bool web, string drift)
    {
        var graph = await RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext();
        var place = await db.Places.Include(p => p.Region).SingleAsync(p => p.Id == graph.PlaceIds[0]);
        await AddCredentialAsync(db, graph.UserId);
        await using var app = await StartAsync(db);
        using var client = web ? await WebAsync(app, graph.UserId) : Bearer(app, graph.UserId);
        var request = CoordinateRequest(place, web, 2.25, 2.25);
        await PrepareStructureAsync(client, place, web, "coordinates", request);
        await using (var other = fixture.CreateContext())
        {
            if (drift == "route") await other.Segments.Where(s => s.Id == graph.SurvivorIds[0]).ExecuteUpdateAsync(s => s.SetProperty(s => s.RouteAttribution, "New provenance"));
            if (drift == "enrichment") await other.Places.Where(p => p.Id == place.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.ResolvedFeatureName, "New enrichment"));
            if (drift == "anchor") await other.Places.Where(p => p.Id == graph.OutsidePlaceId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Location, new Point(1.1, 1.1) { SRID = 4326 }));
            if (drift == "parent") await other.Places.Where(p => p.Id == place.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.RegionId, graph.DestinationId!.Value));
        }
        var preserved = await GraphStateAsync(graph.TripId);
        using var result = web ? await client.PutAsJsonAsync(StructurePath(place, web, "coordinates"), request)
            : await client.PostAsJsonAsync(StructurePath(place, web, "coordinates"), request);
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        Assert.DoesNotContain("New provenance", await result.Content.ReadAsStringAsync());
        Assert.Equal(preserved, await GraphStateAsync(graph.TripId));
        await using var verify = fixture.CreateContext();
        Assert.Equal(place.Location!.AsText(), (await verify.Places.FindAsync(place.Id))!.Location!.AsText());
    }

    /// <summary>Target/sibling order drift prevents both move and ordering, including same-ID authored content changes.</summary>
    [PostgresTheory]
    [InlineData(false, "content")]
    [InlineData(true, "order")]
    [InlineData(false, "membership")]
    public async Task Move_RejectsStaleTargetAndMembership(bool web, string drift)
    {
        var graph = await RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext();
        var place = await db.Places.Include(p => p.Region).SingleAsync(p => p.Id == graph.PlaceIds[0]);
        await AddCredentialAsync(db, graph.UserId);
        await using var app = await StartAsync(db);
        using var client = web ? await WebAsync(app, graph.UserId) : Bearer(app, graph.UserId);
        var request = await MoveRequestAsync(place, graph.DestinationId!.Value, web);
        await PrepareStructureAsync(client, place, web, "move", request);
        if (drift == "content") await db.Places.Where(p => p.Id == place.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Notes, "New material Notes"));
        if (drift == "order") await db.Places.Where(p => p.Id == graph.PlaceIds[1]).ExecuteUpdateAsync(s => s.SetProperty(p => p.DisplayOrder, 99));
        if (drift == "membership") await db.Places.Where(p => p.Id == graph.PlaceIds[1]).ExecuteUpdateAsync(s => s.SetProperty(p => p.RegionId, graph.DestinationId!.Value));
        using var response = web ? await client.PutAsJsonAsync(StructurePath(place, web, "move"), request)
            : await client.PostAsJsonAsync(StructurePath(place, web, "move"), request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.Equal(place.RegionId, (await verify.Places.FindAsync(place.Id))!.RegionId);
    }

    /// <summary>Changes committed while a real row lock is awaited are discovered again, including newly affected Segments.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Coordinates_RediscoverDuringPlaceWait(bool addDependency)
    {
        var place = await SeedAsync();
        var probe = new PlaceProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var bearer = Bearer(app, place.UserId);
        var request = CoordinateRequest(place, false, 15, 23);
        await PrepareStructureAsync(bearer, place, false, "coordinates", request);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Places\" WHERE \"Id\" = {place.Id} FOR UPDATE");
        var pending = bearer.PostAsJsonAsync(StructurePath(place, false, "coordinates"), request);
        await WaitForLockAsync(probe, 0);
        if (addDependency)
        {
            blocker.Segments.Add(new Segment { Id = Guid.NewGuid(), TripId = place.Region.TripId, UserId = place.UserId,
                FromPlaceId = place.Id, ToPlaceId = place.Id, DisplayOrder = 1, Notes = "New dependency" });
            await blocker.SaveChangesAsync();
        }
        else await blocker.Places.Where(p => p.Id == place.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.ResolvedFeatureName, "During wait"));
        await transaction.CommitAsync();
        using var response = await pending;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.Equal(place.Location!.AsText(), (await verify.Places.FindAsync(place.Id))!.Location!.AsText());
        Assert.Empty(probe.Updates);
    }

    /// <summary>Order evidence is rediscovered under the move locks; membership committed during a Place wait cannot be overwritten.</summary>
    [PostgresFact]
    public async Task Move_RevalidatesOrderScopeAfterPlaceWait()
    {
        var graph = await RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext();
        var place = await db.Places.Include(p => p.Region).SingleAsync(p => p.Id == graph.PlaceIds[0]);
        var outsideRegion = await db.Places.Where(p => p.Id == graph.OutsidePlaceId).Select(p => p.RegionId).SingleAsync();
        var probe = new PlaceProbe();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, place.UserId);
        var request = await MoveRequestAsync(place, graph.DestinationId!.Value, true);
        await PrepareStructureAsync(web, place, true, "move", request);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        // Hold the row this writer changes; holding the target can deadlock against an earlier sibling lock.
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Places\" WHERE \"Id\" = {graph.PlaceIds[1]} FOR UPDATE");
        var pending = web.PutAsJsonAsync(StructurePath(place, true, "move"), request);
        await WaitForLockAsync(probe, 0);
        Assert.False(pending.IsCompleted);
        output.WriteLine("Routed move reached a physical PostgreSQL Place wait before the sibling membership change.");
        await blocker.Places.Where(p => p.Id == graph.PlaceIds[1]).ExecuteUpdateAsync(s => s.SetProperty(p => p.RegionId, outsideRegion));
        await transaction.CommitAsync();
        var before = await StoredStructureRowsAsync(graph.TripId);
        using var response = await pending;
        var body = await response.Content.ReadAsStringAsync();
        output.WriteLine("Complete conflict response: " + body);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("mutation-dependency-stale", JsonSerializer.Deserialize<JsonElement>(body).GetProperty("code").GetString());
        Assert.DoesNotContain(graph.PlaceIds[1].ToString(), body);
        Assert.DoesNotContain(outsideRegion.ToString(), body);
        Assert.Equal(before, await StoredStructureRowsAsync(graph.TripId));
        Assert.Empty(probe.Updates);
        Assert.Empty(_warmup.Invocations);
    }

    /// <summary>A single Web save combines coordinate, membership, Address and selected scalars; SQL failure rolls every effect back.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CombinedSave_AtomicGraphAndRollback(bool failCommit)
    {
        var graph = await RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext();
        var place = await db.Places.Include(p => p.Region).SingleAsync(p => p.Id == graph.PlaceIds[0]);
        var before = await GraphStateAsync(graph.TripId);
        var originalOrders = await db.Places.AsNoTracking().Where(p => p.Region.TripId == graph.TripId).OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.RegionId, p.DisplayOrder }).ToArrayAsync();
        await using var app = await StartAsync(db, failCommit ? [new FailPlaceCommit(false)] : []);
        using var web = await WebAsync(app, graph.UserId);
        var request = await MoveRequestAsync(place, graph.DestinationId!.Value, true);
        request["changes"] = new { regionId = graph.DestinationId, location = new { latitude = 2.25, longitude = 2.25 },
            name = "Combined name", notesHtml = "Combined Notes", address = "Authored address" };
        using var response = await ApplyStructureAsync(web, place, true, "move", request);
        Assert.True(response.StatusCode == (failCommit ? HttpStatusCode.InternalServerError : HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        await using var verify = fixture.CreateContext();
        var current = await verify.Places.SingleAsync(p => p.Id == place.Id);
        if (failCommit)
        {
            Assert.Equal(before, await GraphStateAsync(graph.TripId));
            Assert.Equal(JsonSerializer.Serialize(originalOrders), JsonSerializer.Serialize(await verify.Places.AsNoTracking()
                .Where(p => p.Region.TripId == graph.TripId).OrderBy(p => p.Id).Select(p => new { p.Id, p.RegionId, p.DisplayOrder }).ToArrayAsync()));
            Assert.Equal(JsonSerializer.Serialize(Material(place)), JsonSerializer.Serialize(Material(current)));
            return;
        }
        Assert.Equal((graph.DestinationId!.Value, "Combined name", "Combined Notes", "Authored address", "POINT (2.25 2.25)"),
            (current.RegionId, current.Name, current.Notes, current.Address, current.Location!.AsText()));
        var segments = await verify.Segments.Include(s => s.Waypoints).Where(s => s.TripId == graph.TripId).ToArrayAsync();
        var custom = segments.Single(s => s.Id == graph.SurvivorIds[0]);
        Assert.Null(custom.RouteProvider); Assert.Null(custom.RouteAttribution); Assert.Null(custom.RouteInstructionsJson);
        Assert.Equal(EstimatedDurationSource.Manual, custom.EstimatedDurationSource);
        Assert.Equal(TimeSpan.FromTicks(123456780), custom.EstimatedDuration);
        Assert.Equal(new Coordinate(2.25, 2.25), custom.RouteGeometry!.GetCoordinateN(custom.Waypoints.Single(w => w.PlaceId == place.Id).RouteVertexIndex!.Value));
        Assert.NotEqual(123.25, custom.EstimatedDistanceKm);
        Assert.Single(await verify.PlaceVisitEvents.Where(v => v.PlaceId == place.Id).ToArrayAsync());
    }

    /// <summary>Unknown commit acknowledgement remains 500 after a real commit, with no server retry.</summary>
    [PostgresFact]
    public async Task Coordinate_LostCommitAcknowledgementIsUnknown()
    {
        var place = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [new FailPlaceCommit(true)]);
        using var bearer = Bearer(app, place.UserId);
        using var result = await ApplyStructureAsync(bearer, place, false, "coordinates", CoordinateRequest(place, false, 15, 23));
        Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.Equal(15, (await verify.Places.FindAsync(place.Id))!.Location!.Y);
    }
}
