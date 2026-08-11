using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;

namespace Wayfarer.Tests.Services;

/// <summary>Creates isolated canonical aggregates for the #407 PostgreSQL service matrix.</summary>
internal sealed class TripEditorSegmentMutationPostgresTestSupport(PostgresImportTestFixture fixture)
{
    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();

    /// <summary>Creates a service whose aggregate and confirmation tokens share one deterministic test provider.</summary>
    internal TripEditorSegmentMutationService Service(ApplicationDbContext context) => new(
        context,
        new SegmentAggregateTokenService(_protection),
        new SegmentRouteClearConfirmation(_protection, TimeProvider.System));

    /// <summary>Seeds one trip with canonical profiles, four places, and an optional custom waypoint Segment.</summary>
    internal async Task<SegmentSeed> SeedAsync(bool includeSegment = true, bool customRoute = true)
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = $"#407 provider {Guid.NewGuid():N}" };
        fixture.RegisterTrip(trip.Id);
        var region = new Region
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id,
            Name = "#407 region", DisplayOrder = 1
        };
        var from = Place(region, user.Id, "From", 0, 0, 1);
        var first = Place(region, user.Id, "First waypoint", 1, 1, 2);
        var second = Place(region, user.Id, "Second waypoint", 2, 2, 3);
        var to = Place(region, user.Id, "To", 3, 3, 4);
        var alternate = Place(region, user.Id, "Alternate", 1.5, 1.5, 5);
        var firstProfile = Profile("first", 5);
        var secondProfile = Profile("second", 20);
        fixture.RegisterTransportProfile(firstProfile.Id);
        fixture.RegisterTransportProfile(secondProfile.Id);
        Segment? segment = null;
        if (includeSegment)
        {
            segment = new Segment
            {
                Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id,
                FromPlace = from, FromPlaceId = from.Id, ToPlace = to, ToPlaceId = to.Id,
                Mode = firstProfile.Key, TransportProfile = firstProfile, TransportProfileId = firstProfile.Id,
                DisplayOrder = 1, Notes = "original notes", EstimatedDistanceKm = 471.652,
                EstimatedDuration = TimeSpan.FromMinutes(90),
                EstimatedDurationSource = EstimatedDurationSource.Manual,
                RouteGeometry = customRoute ? Line(from, first, second, to) : null
            };
            segment.Waypoints.Add(Waypoint(segment, first, 0, customRoute ? 1 : null));
            segment.Waypoints.Add(Waypoint(segment, second, 1, customRoute ? 2 : null));
        }
        await using var context = fixture.CreateContext();
        context.AddRange(trip, region, from, first, second, to, alternate, firstProfile, secondProfile);
        if (segment != null) context.Add(segment);
        await context.SaveChangesAsync();
        return new(user.Id, trip.Id, segment?.Id, from.Id, first.Id, second.Id, to.Id, alternate.Id,
            firstProfile.Id, firstProfile.Key, secondProfile.Id, secondProfile.Key);
    }

    /// <summary>Issues the current opaque token after a provider reread.</summary>
    internal async Task<string> TokenAsync(ApplicationDbContext context, SegmentSeed seed)
    {
        var segment = await context.Segments.AsNoTracking().SingleAsync(item => item.Id == seed.SegmentId!.Value);
        return Service(context).IssueAggregateToken(seed.UserId, seed.TripId, segment);
    }

    /// <summary>Builds a complete request body with every #407 property explicitly present.</summary>
    internal static Stream Body(
        SegmentSeed seed,
        IReadOnlyList<Guid> waypointIds,
        IReadOnlyList<int?> waypointIndices,
        string? token,
        Guid? fromId = null,
        Guid? toId = null,
        string? mode = null,
        string notes = "updated notes",
        bool customRoute = false,
        IReadOnlyList<(double X, double Y)>? route = null)
    {
        var coordinates = route ?? [(0d, 0d), (1d, 1d), (2d, 2d), (3d, 3d)];
        var routeJson = customRoute
            ? $"{{\"type\":\"LineString\",\"coordinates\":[{string.Join(',', coordinates.Select(item => $"[{item.X},{item.Y}]"))}]}}"
            : "null";
        var json = $$"""
        {
          "fromPlaceId": {{JsonGuid(fromId ?? seed.FromId)}},
          "toPlaceId": {{JsonGuid(toId ?? seed.ToId)}},
          "waypointPlaceIds": [{{string.Join(',', waypointIds.Select(JsonGuid))}}],
          "waypointRouteVertexIndices": [{{string.Join(',', waypointIndices.Select(item => item?.ToString() ?? "null"))}}],
          "mode": "{{mode ?? seed.FirstProfileKey}}",
          "estimatedDistanceKm": 999999,
          "estimatedDurationMinutes": 91,
          "estimatedDurationSource": "Manual",
          "notesHtml": "{{notes}}",
          "route": {{routeJson}},
          "aggregateConcurrencyToken": {{(token == null ? "null" : $"\"{token}\"")}}
        }
        """;
        return new MemoryStream(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Loads the complete persisted aggregate without relying on tracked state.</summary>
    internal static Task<Segment> ReadAsync(ApplicationDbContext context, Guid segmentId) =>
        context.Segments.AsNoTracking()
            .Include(item => item.Waypoints.OrderBy(waypoint => waypoint.Position))
            .SingleAsync(item => item.Id == segmentId);

    private static string JsonGuid(Guid value) => $"\"{value}\"";

    private static Place Place(Region region, string userId, string name, double x, double y, int order) => new()
    {
        Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = userId,
        Name = name, DisplayOrder = order, Location = new Point(x, y) { SRID = 4326 }
    };

    private static TransportProfile Profile(string suffix, double speed) => new()
    {
        Id = Guid.NewGuid(), Key = $"e407-{suffix}-{Guid.NewGuid():N}"[..40], Label = suffix,
        Category = "#407", PlanningSpeedKmh = speed, IsActive = true
    };

    private static SegmentWaypoint Waypoint(Segment segment, Place place, int position, int? index) => new()
    {
        Segment = segment, SegmentId = segment.Id, Place = place, PlaceId = place.Id,
        Position = position, RouteVertexIndex = index
    };

    private static LineString Line(params Place[] places) => new(
        places.Select(item => item.Location!.Coordinate).ToArray()) { SRID = 4326 };
}

/// <summary>Captured IDs and canonical profile keys for one run-owned #407 aggregate.</summary>
internal sealed record SegmentSeed(
    string UserId,
    Guid TripId,
    Guid? SegmentId,
    Guid FromId,
    Guid FirstWaypointId,
    Guid SecondWaypointId,
    Guid ToId,
    Guid AlternateId,
    Guid FirstProfileId,
    string FirstProfileKey,
    Guid SecondProfileId,
    string SecondProfileKey);
