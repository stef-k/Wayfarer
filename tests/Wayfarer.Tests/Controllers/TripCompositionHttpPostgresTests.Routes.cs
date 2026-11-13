using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class TripCompositionHttpPostgresTests
{
    /// <summary>Both routed authorities reject malformed complete route facts atomically and still accept valid Edit/Clear.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RouteValidationIsStrictBeforeDiscoveryAndValidControlsStillSave(bool bearer)
    {
        var owner = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var a = new Trip { Id = Guid.NewGuid(), UserId = owner.Id, Name = "Route base" };
        var b = new Trip { Id = Guid.NewGuid(), UserId = owner.Id, Name = "Route source" };
        var region = new Region { Id = Guid.NewGuid(), TripId = a.Id, UserId = owner.Id, Name = "Route anchors" };
        var places = Enumerable.Range(0, 3).Select(i => new Place { Id = Guid.NewGuid(), RegionId = region.Id,
            UserId = owner.Id, Name = "Anchor " + i, Location = new Point(12 + i, 40 + i) { SRID = 4326 } }).ToArray();
        var segment = new Segment { Id = Guid.NewGuid(), TripId = a.Id, UserId = owner.Id, FromPlaceId = places[0].Id,
            ToPlaceId = places[2].Id, Mode = "", EstimatedDurationSource = EstimatedDurationSource.Manual,
            EstimatedDuration = TimeSpan.FromTicks(1234567), EstimatedDistanceKm = 1.23456789,
            RouteGeometry = new LineString(places.Select(p => p.Location!.Coordinate).ToArray()) { SRID = 4326 } };
        fixture.RegisterTrip(a.Id); fixture.RegisterTrip(b.Id);
        db.AddRange(a, b, region, segment); db.AddRange(places);
        db.Add(new SegmentWaypoint { SegmentId = segment.Id, PlaceId = places[1].Id, Position = 0, RouteVertexIndex = 1 });
        db.Add(new ApiToken { Name = "route-validation", Token = owner.Id, UserId = owner.Id,
            User = await db.Users.SingleAsync(u => u.Id == owner.Id) });
        await db.SaveChangesAsync();
        var created = await new TripCompositionService(db).CreateAsync(owner.Id, a.Id, b.Id, null, null);
        fixture.RegisterTrip(created.TripId);
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext());
            services.AddSingleton(Mock.Of<ICacheWarmupScheduler>());
            services.AddSingleton(Mock.Of<IIconColorProvider>());
            services.AddSingleton(Mock.Of<ITripMapThumbnailGenerator>());
        });
        using var client = app.GetTestClient();
        if (bearer) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Id);
        else
        {
            UserConnectionTokenHttpTests.Cookie(client, app.Services, owner.Id);
            await UserConnectionTokenHttpTests.AddAntiforgeryAsync(client);
        }
        var url = bearer ? $"/api/trips/compositions/{created.CompositionId}/actions"
            : $"/api/trip-compositions/{created.CompositionId}/actions";
        var target = await db.Segments.AsNoTracking().Include(s => s.Waypoints).SingleAsync(s => s.TripId == created.TripId);
        var canonical = await SegmentRouteReconciler.LoadAggregateAsync(db, target.Id) ?? throw new InvalidOperationException();
        var proof = app.Services.GetRequiredService<SegmentAggregateTokenService>().IssueRoute(owner.Id, created.TripId, canonical);
        var anchors = await db.Places.AsNoTracking().Where(p => p.Region.TripId == created.TripId).ToArrayAsync();
        db.ChangeTracker.Clear();
        var route = TripCompositionFieldValues.Element(new { target.FromPlaceId, target.ToPlaceId,
            waypoints = target.Waypoints.Select(w => new { w.PlaceId, w.Position, w.RouteVertexIndex }),
            routeJson = new NetTopologySuite.IO.GeoJsonWriter().Write(target.RouteGeometry),
            target.Mode, target.TransportProfileId, durationSource = "Manual", manualDurationMinutes = target.EstimatedDuration!.Value.TotalMinutes });
        // Select the captured A route against its initial counterpart, retaining its actual initial decision original.
        var original = TripCompositionFieldValues.Element(new TripCompositionAction("a", [new("segment", segment.Id, "reuse",
            DestinationId: target.Id, ExpectedDecision: new(target.Id, "included"), ConditionToken: proof,
            Anchors: anchors.Select(p => new TripCompositionAnchor(Guid.Empty, p.Id, p.RegionId, [p.Location!.X, p.Location.Y])).ToArray(),
            Fields: new(Route: new("edit", TripCompositionFieldValues.Element(TripCompositionFieldValues.Route(target)), route)))]));
        var before = await db.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == created.CompositionId);
        var beforeTrip = await db.Trips.AsNoTracking().SingleAsync(t => t.Id == created.TripId);
        foreach (var scenario in new[] { "profile", "anchor", "waypoint-null", "waypoint-unknown", "waypoint-missing", "waypoint-kind",
            "mode", "missing-route", "null-route", "unknown-route", "geometry", "wrong-object", "clear-value", "duplicate" })
        {
            var request = JsonNode.Parse(original.GetRawText())!;
            var item = request["items"]![0]!;
            var field = item["fields"]!["route"]!;
            var value = field["value"]!;
            switch (scenario)
            {
                case "profile": value["transportProfileId"] = 1; break;
                case "anchor": item["anchors"]![0] = null; break;
                case "waypoint-null": value["waypoints"]![0] = null; break;
                case "waypoint-unknown": value["waypoints"]![0]!["unexpected"] = "must refuse"; break;
                case "waypoint-missing": value["waypoints"]![0]!.AsObject().Remove("routeVertexIndex"); break;
                case "waypoint-kind": value["waypoints"]![0]!["position"] = "0"; break;
                case "mode": value["mode"] = 1; break;
                case "missing-route": value.AsObject().Remove("routeJson"); break;
                case "null-route": field["value"] = null; break;
                case "unknown-route": value["preservedMeasurements"] = 1; break;
                case "geometry": value["routeJson"] = "{\"type\":\"LineString\",\"coordinates\":[null,null]}"; break;
                case "wrong-object": item["kind"] = "trip"; break;
                case "clear-value": field["operation"] = "clear"; break;
            }
            var json = request.ToJsonString();
            if (scenario == "duplicate") json = json.Replace("\"position\":0", "\"position\":0,\"position\":0");
            using var response = await client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, scenario + ": " + await response.Content.ReadAsStringAsync());
            Assert.True(response.Headers.CacheControl!.NoStore);
        }
        await using var verify = fixture.CreateContext();
        var unchanged = await verify.Segments.AsNoTracking().Include(s => s.Waypoints).SingleAsync(s => s.Id == target.Id);
        Assert.True(TripCompositionFieldValues.Equal(TripCompositionFieldValues.Element(TripCompositionFieldValues.Route(target)), TripCompositionFieldValues.Route(unchanged)));
        Assert.Equal(before.Mappings, (await verify.Set<TripComposition>().AsNoTracking().SingleAsync(w => w.Id == before.Id)).Mappings);
        var unchangedTrip = await verify.Trips.AsNoTracking().SingleAsync(t => t.Id == created.TripId);
        Assert.Equal(beforeTrip.Credits, unchangedTrip.Credits); Assert.Equal(beforeTrip.UpdatedAt, unchangedTrip.UpdatedAt);
        using var edited = await client.PostAsJsonAsync(url, original);
        Assert.True(edited.StatusCode == HttpStatusCode.OK, await edited.Content.ReadAsStringAsync());
        canonical = await SegmentRouteReconciler.LoadAggregateAsync(verify, target.Id) ?? throw new InvalidOperationException();
        var clear = JsonNode.Parse(original.GetRawText())!;
        clear["items"]![0]!["expectedDecision"]!["decision"] = "reuse";
        clear["items"]![0]!["conditionToken"] = app.Services.GetRequiredService<SegmentAggregateTokenService>().IssueRoute(owner.Id, created.TripId, canonical);
        clear["items"]![0]!["fields"]!["route"]!["operation"] = "clear";
        clear["items"]![0]!["fields"]!["route"]!.AsObject().Remove("value");
        using var cleared = await client.PostAsJsonAsync(url, clear);
        Assert.True(cleared.StatusCode == HttpStatusCode.OK, await cleared.Content.ReadAsStringAsync());
        Assert.Null((await verify.Segments.AsNoTracking().SingleAsync(s => s.Id == target.Id)).RouteGeometry);
    }
}
