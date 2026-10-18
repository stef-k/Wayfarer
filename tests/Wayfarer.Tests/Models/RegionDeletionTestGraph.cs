using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;

namespace Wayfarer.Tests.Models;

/// <summary>Representative mixed lifecycle graph shared by product-level PostgreSQL and routed HTTP assertions.</summary>
internal sealed record RegionDeletionTestGraph(string UserId, Guid TripId, Guid SourceId, Guid? DestinationId,
    Guid[] PlaceIds, Guid[] AreaIds, Guid[] EndpointIds, Guid[] SurvivorIds, Guid OutsidePlaceId)
{
    internal static LifecycleDependencyConfirmation Confirmation() => new(new EphemeralDataProtectionProvider());

    /// <summary>Creates owned rows only; the fixture records their Trip/user for exact cleanup.</summary>
    internal static async Task<RegionDeletionTestGraph> SeedAsync(PostgresImportTestFixture fixture,
        bool destination = true, bool places = true, bool areas = true)
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Four-way deletion", Notes = "Trip notes" };
        fixture.RegisterTrip(trip.Id);
        var outside = Region(trip, "Outside", 4);
        var source = Region(trip, "Delete source", 8);
        source.Notes = "<p>Region notes</p>";
        source.CoverImageUrl = "https://example.test/cover.png";
        source.Center = new Point(2, 2) { SRID = 4326 };
        var target = destination ? Region(trip, "Unassigned Places", 17) : null;
        if (target != null) { Place(target, "Existing Place", 1, 1, 9); Area(target, "Existing Area", 19); }
        var from = Place(outside, "From", 1, 1, 1);
        var to = Place(outside, "To", 5, 5, 2);
        var first = places ? Place(source, "First", 2, 2, 3) : null;
        var second = places ? Place(source, "Second", 3, 3, null) : null;
        var sourceAreas = areas ? new[] { Area(source, "First Area", 9), Area(source, "Second Area", null) } : [];
        var endpoints = new List<Guid>();
        var survivors = new List<Guid>();
        if (first != null && second != null)
        {
            var custom = Segment(trip, from, to, 8, new LineString([
                new(1, 1), new(1.5, 1.5), new(2, 2), new(2.5, 2.5), new(3, 3), new(4, 4), new(5, 5)]) { SRID = 4326 });
            Waypoint(custom, first, 0, 2); Waypoint(custom, second, 1, 4);
            custom.RouteProvider = "osrm"; custom.RouteInstructionsJson = "[]"; custom.RouteAttribution = "Retained";
            custom.RouteStorageMode = "retained"; custom.EstimatedDistanceKm = 123.25;
            custom.EstimatedDuration = TimeSpan.FromTicks(123456789);
            custom.EstimatedDurationSource = EstimatedDurationSource.Manual;
            var fallback = Segment(trip, from, to, 12, null);
            Waypoint(fallback, first, 0, null);
            var loop = Segment(trip, first, first, 3, new LineString([new(2, 2), new(3, 3), new(2, 2)]) { SRID = 4326 });
            Waypoint(loop, second, 0, 1);
            var endpoint = Segment(trip, second, to, 2, new LineString([new(3, 3), new(5, 5)]) { SRID = 4326 });
            endpoints.AddRange([loop.Id, endpoint.Id]); survivors.AddRange([custom.Id, fallback.Id]);
        }
        await using var db = fixture.CreateContext();
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        if (first != null)
        {
            db.PlaceVisitEvents.Add(new PlaceVisitEvent { Id = Guid.NewGuid(), UserId = user.Id, PlaceId = first.Id,
                ArrivedAtUtc = DateTime.UtcNow.AddHours(-1), LastSeenAtUtc = DateTime.UtcNow,
                TripIdSnapshot = trip.Id, TripNameSnapshot = trip.Name, RegionNameSnapshot = source.Name,
                PlaceNameSnapshot = first.Name, NotesHtml = "Visit notes", PlaceLocationSnapshot = first.Location });
            await db.SaveChangesAsync();
        }
        return new(user.Id, trip.Id, source.Id, target?.Id, source.Places.Select(p => p.Id).ToArray(),
            sourceAreas.Select(a => a.Id).ToArray(), endpoints.ToArray(), survivors.ToArray(), from.Id);
    }

    private static Region Region(Trip trip, string name, int order)
    {
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = trip.UserId, Name = name, DisplayOrder = order };
        trip.Regions.Add(region); return region;
    }
    private static Place Place(Region region, string name, double x, double y, int? order)
    {
        var place = new Place { Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = region.UserId,
            Name = name, Notes = "<p>Authored notes</p>", Address = "Address", DisplayOrder = order,
            IconName = "marker", MarkerColor = "#ff6600", Location = new Point(x, y) { SRID = 4326 } };
        region.Places.Add(place); return place;
    }
    private static Area Area(Region region, string name, int? order)
    {
        var area = new Area { Id = Guid.NewGuid(), Region = region, RegionId = region.Id, Name = name, DisplayOrder = order,
            Notes = "<p>Area notes</p>", FillHex = "#123456", Geometry = new Polygon(
                new LinearRing([new(0, 0), new(0, 8), new(8, 8), new(8, 0), new(0, 0)]),
                [new LinearRing([new(1, 1), new(2, 1), new(2, 2), new(1, 2), new(1, 1)])]) { SRID = 4326 } };
        region.Areas.Add(area); return area;
    }
    private static Segment Segment(Trip trip, Place from, Place to, int order, LineString? route)
    {
        var segment = new Segment { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = trip.UserId,
            FromPlaceId = from.Id, ToPlaceId = to.Id, DisplayOrder = order, RouteGeometry = route, Notes = "Segment notes" };
        trip.Segments.Add(segment); return segment;
    }
    private static void Waypoint(Segment segment, Place place, int position, int? index) => segment.Waypoints.Add(
        new SegmentWaypoint { Segment = segment, SegmentId = segment.Id, Place = place, PlaceId = place.Id, Position = position, RouteVertexIndex = index });
}
