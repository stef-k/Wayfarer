using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class PlaceConditionalPostgresTests
{
    /// <summary>Coordinates preserve scalar authority, and later stale combined requests are rejected.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PopulatedGraph_ScalarFootprintAndCombinedWriterProtection(bool combinedWitness)
    {
        var graph = await Wayfarer.Tests.Models.RegionDeletionTestGraph.SeedAsync(fixture);
        await using var db = fixture.CreateContext();
        var place = await db.Places.Include(p => p.Region).SingleAsync(p => p.Id == graph.PlaceIds[0]);
        var staleNotes = place.Notes;
        await db.Places.Where(p => p.Id == place.Id).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.ResolvedFeatureName, "Feature").SetProperty(p => p.ResolvedFeatureType, "landmark")
            .SetProperty(p => p.AddressEnrichmentProvider, "nominatim").SetProperty(p => p.AddressEnrichmentStorageMode, "persistent")
            .SetProperty(p => p.AddressEnrichedAt, DateTimeOffset.UtcNow));
        var future = DateTime.UtcNow.Date.AddDays(1);
        var probe = new PlaceProbe();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, graph.UserId);
        // An actual complete Web coordinate/address edit exercises route, waypoint and enrichment lifecycle behavior.
        var coordinateRequest = CoordinateRequest(place, true, 2.25, 2.25);
        coordinateRequest["expected"] = new { regionId = place.RegionId, location = Coordinate(place), address = place.Address };
        coordinateRequest["changes"] = new { location = new { latitude = 2.25, longitude = 2.25 }, address = "Updated address" };
        using var structural = await ApplyStructureAsync(web, place, true, "coordinates", coordinateRequest);
        Assert.True(structural.StatusCode == HttpStatusCode.OK, await structural.Content.ReadAsStringAsync());
        await db.Trips.Where(t => t.Id == graph.TripId).ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, future));
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        // Exact row versions prove all sibling/visit/route/waypoint/measurement/provenance rows stayed untouched by PATCH.
        await using var snapshot = new NpgsqlCommand("""
            SELECT string_agg(row, ',' ORDER BY row) FROM (
              SELECT 'region:' || r."Id"::text || ':' || r.xmin::text AS row FROM "Regions" r WHERE r."UserId" = $1
              UNION ALL SELECT 'area:' || a."Id"::text || ':' || a.xmin::text FROM "Areas" a
                JOIN "Regions" r ON r."Id" = a."RegionId" WHERE r."UserId" = $1
              UNION ALL SELECT 'place:' || p."Id"::text || ':' || p.xmin::text FROM "Places" p WHERE p."UserId" = $1 AND p."Id" <> $2
              UNION ALL SELECT 'segment:' || s."Id"::text || ':' || s.xmin::text FROM "Segments" s WHERE s."UserId" = $1
              UNION ALL SELECT 'waypoint:' || w."SegmentId"::text || ':' || w."Position"::text || ':' || w.xmin::text
                FROM "SegmentWaypoints" w JOIN "Segments" s ON s."Id" = w."SegmentId" WHERE s."UserId" = $1
              UNION ALL SELECT 'visit:' || v."Id"::text || ':' || v.xmin::text FROM "PlaceVisitEvents" v WHERE v."UserId" = $1
              UNION ALL SELECT 'target:' || (to_jsonb(p) - ARRAY['Name','Notes','IconName','MarkerColor'])::text FROM "Places" p WHERE p."Id" = $2
              UNION ALL SELECT 'trip:' || (to_jsonb(t) - 'UpdatedAt')::text FROM "Trips" t WHERE t."Id" = $3
            ) dependents
            """, connection);
        snapshot.Parameters.AddWithValue(graph.UserId);
        snapshot.Parameters.AddWithValue(place.Id);
        snapshot.Parameters.AddWithValue(graph.TripId);
        var before = await snapshot.ExecuteScalarAsync();
        probe.Updates.Clear();
        using var response = await PatchAsync(web, place, true, new { notesHtml = staleNotes, iconName = "marker" },
            new { notesHtml = "Protected notes", iconName = "star" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, await snapshot.ExecuteScalarAsync());
        Assert.Equal(2, probe.Updates.Count);
        var sql = probe.Updates.First();
        Assert.Contains("\"Notes\" =", sql);
        Assert.Contains("\"IconName\" =", sql);
        Assert.DoesNotContain("\"Location\" =", sql);
        Assert.DoesNotContain("\"Address\" =", sql);
        Assert.DoesNotContain("\"RegionId\" =", sql.Split("WHERE")[0]);
        await using var verify = fixture.CreateContext();
        var stored = await verify.Places.SingleAsync(p => p.Id == place.Id);
        Assert.Equal(("Protected notes", "star", "Updated address", "POINT (2.25 2.25)"),
            (stored.Notes, stored.IconName, stored.Address, stored.Location!.AsText()));
        Assert.Equal(future, (await verify.Trips.FindAsync(graph.TripId))!.UpdatedAt);
        if (combinedWitness)
        {
            using var combined = await web.PostAsJsonAsync($"/api/trips/{graph.TripId}/editor/places/{place.Id}/prepare", new
            { expected = new { regionId = place.RegionId, notesHtml = staleNotes, name = place.Name, address = "Updated address",
                    location = new { latitude = 2.25, longitude = 2.25 } },
                changes = new { name = "Combined name", notesHtml = staleNotes, address = "Combined address", location = new { latitude = 2.5, longitude = 2.5 } } });
            Assert.Equal(HttpStatusCode.Conflict, combined.StatusCode);
            var preserved = await verify.Places.AsNoTracking().SingleAsync(p => p.Id == place.Id);
            Assert.Equal((place.Name, "Protected notes", "Updated address", "POINT (2.25 2.25)"),
                (preserved.Name, preserved.Notes, preserved.Address, preserved.Location!.AsText()));
            output.WriteLine("Stale combined Web intent was rejected before any partial write.");
        }
        Assert.Empty(_warmup.Invocations);
        output.WriteLine("SQL/fresh-context proof: scalar PATCH preserved complete geometry, enrichment, membership/order, route, measurement, provider and visit footprint.");
        foreach (var statement in probe.Updates) output.WriteLine(statement);
    }
}
