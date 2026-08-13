using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Util;

const string connectionVariable = "WAYFARER_TEST_POSTGRES_CONNECTION";
if (args.Length is < 2 or > 3)
    throw new InvalidOperationException("Usage: Wayfarer.WaypointBrowserFixture <provision|drift|verify-preserved|verify-route-work|verify-failed-save|verify-ui|cleanup|verify-cleanup> <manifest> [password].");

var command = args[0];
var manifestPath = Path.GetFullPath(args[1]);
var connection = Environment.GetEnvironmentVariable(connectionVariable)
    ?? throw new InvalidOperationException($"{connectionVariable} is required.");
var services = new ServiceCollection().AddEntityFrameworkNpgsql().BuildServiceProvider();
var options = new DbContextOptionsBuilder<ApplicationDbContext>()
    .UseNpgsql(connection, provider => provider.UseNetTopologySuite()).Options;
await using var context = new ApplicationDbContext(options, services);

switch (command)
{
    case "provision":
        await context.Database.MigrateAsync();
        var password = args.Length == 3 ? args[2] : throw new InvalidOperationException("Provision requires a run-owned password.");
        var provisioned = await ProvisionAsync(context, password, manifestPath);
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(provisioned, FixtureJson.Options));
        Console.WriteLine(manifestPath);
        break;
    case "drift":
        await DriftAsync(context, await ReadAsync(manifestPath));
        break;
    case "verify-preserved":
        await VerifyPreservedAsync(context, await ReadAsync(manifestPath));
        break;
    case "verify-route-work":
        await VerifyRouteWorkAsync(context, await ReadAsync(manifestPath));
        break;
    case "verify-failed-save":
        await VerifyFailedSaveAsync(context, await ReadAsync(manifestPath));
        break;
    case "verify-ui":
        await VerifyUiAsync(context, await ReadAsync(manifestPath));
        break;
    case "cleanup":
        var cleanupManifest = await ReadAsync(manifestPath);
        await CleanupAsync(context, cleanupManifest);
        await VerifyAsync(context, cleanupManifest);
        break;
    case "verify-cleanup":
        await VerifyAsync(context, await ReadAsync(manifestPath));
        break;
    default:
        throw new InvalidOperationException($"Unknown fixture command '{command}'.");
}

/// <summary>Creates one exact run-owned #407 Identity and waypoint aggregate fixture.</summary>
static async Task<WaypointFixtureManifest> ProvisionAsync(ApplicationDbContext context, string password, string manifestPath)
{
    var run = Guid.NewGuid().ToString("N");
    var user = new ApplicationUser
    {
        Id = $"issue407-{run}", UserName = $"issue407-{run}", NormalizedUserName = $"ISSUE407-{run}".ToUpperInvariant(),
        DisplayName = "Issue 407 browser fixture", IsActive = true, EmailConfirmed = true,
        SecurityStamp = Guid.NewGuid().ToString("N"), ConcurrencyStamp = Guid.NewGuid().ToString("N")
    };
    user.PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(user, password);
    var role = await context.Roles.SingleOrDefaultAsync(item => item.NormalizedName == "USER");
    if (role == null)
    {
        role = new IdentityRole(ApplicationRoles.User) { NormalizedName = "USER" };
        context.Roles.Add(role);
    }
    var trip = new Trip
    {
        Id = Guid.NewGuid(), User = user, UserId = user.Id, Name = $"Issue 407 waypoint {run}",
        UpdatedAt = DateTime.UtcNow, CenterLat = 37.98, CenterLon = 23.73, Zoom = 12
    };
    var shadow = Region(trip, user.Id, "Unassigned Places", 0);
    var region = Region(trip, user.Id, $"Waypoint region {run}", 1);
    var from = Place(region, user.Id, $"From {run}", 1, 23.70, 37.97);
    var waypoint = Place(region, user.Id, $"Waypoint {run}", 2, 23.74, 37.99);
    var alternate = Place(region, user.Id, $"Alternate {run}", 3, 23.76, 38.00);
    var to = Place(region, user.Id, $"To {run}", 4, 23.78, 38.01);
    var staleWaypoint = Place(shadow, user.Id, $"Shadow waypoint {run}", 1, 23.73, 38.05);
    var profile = new TransportProfile
    {
        Id = Guid.NewGuid(), Key = $"issue407-{run}"[..40], Label = "Issue 407 fixture walk", Category = "fixture",
        PlanningSpeedKmh = 5, SortOrder = 9407, IsActive = true, IsSeeded = false
    };
    var waypointSegment = Segment(trip, user.Id, profile, 1, from, to, "Waypoint browser original", true);
    waypointSegment.Waypoints.Add(new SegmentWaypoint
    {
        Segment = waypointSegment, SegmentId = waypointSegment.Id, Place = waypoint, PlaceId = waypoint.Id,
        Position = 0, RouteVertexIndex = 2
    });
    waypointSegment.RouteGeometry = Line(from.Location!.Coordinate,
        new Coordinate(23.72, 37.98), waypoint.Location!.Coordinate, to.Location!.Coordinate);
    AssertCanonicalDistance(waypointSegment.RouteGeometry.Coordinates, waypointSegment.EstimatedDistanceKm!.Value);
    var zeroSegment = Segment(trip, user.Id, profile, 2, from, to, "Zero waypoint browser", false);
    var staleSegment = Segment(trip, user.Id, profile, 3, from, to, "Stale waypoint browser", false);
    staleSegment.Waypoints.Add(new SegmentWaypoint
    {
        Segment = staleSegment, SegmentId = staleSegment.Id, Place = staleWaypoint, PlaceId = staleWaypoint.Id,
        Position = 0, RouteVertexIndex = null
    });
    var routeWorkSegment = Segment(trip, user.Id, profile, 4, from, to, "Anchor-aware route work", true);
    routeWorkSegment.Waypoints.Add(new SegmentWaypoint
    {
        Segment = routeWorkSegment, SegmentId = routeWorkSegment.Id, Place = waypoint, PlaceId = waypoint.Id,
        Position = 0, RouteVertexIndex = 2
    });
    routeWorkSegment.RouteGeometry = Line(from.Location!.Coordinate,
        new Coordinate(23.72, 37.98), waypoint.Location!.Coordinate, to.Location!.Coordinate);
    var closedLoopSegment = Segment(trip, user.Id, profile, 5, from, from, "Closed-loop route work", true);
    closedLoopSegment.Waypoints.Add(new SegmentWaypoint
    {
        Segment = closedLoopSegment, SegmentId = closedLoopSegment.Id, Place = waypoint, PlaceId = waypoint.Id,
        Position = 0, RouteVertexIndex = 2
    });
    closedLoopSegment.RouteGeometry = Line(from.Location!.Coordinate, new Coordinate(23.71, 37.985),
        waypoint.Location!.Coordinate, new Coordinate(23.72, 37.975), from.Location!.Coordinate);
    var cleanupSegment = AnchoredRouteSegment(trip, user.Id, profile, 6, from, waypoint, to, "Cleanup route work");
    var responsiveSegment = AnchoredRouteSegment(trip, user.Id, profile, 7, from, waypoint, to, "Responsive route work");
    var failedSaveSegment = AnchoredRouteSegment(trip, user.Id, profile, 8, from, waypoint, to, "Failed-save route work");
    trip.Segments.Add(waypointSegment);
    trip.Segments.Add(zeroSegment);
    trip.Segments.Add(staleSegment);
    trip.Segments.Add(routeWorkSegment);
    trip.Segments.Add(closedLoopSegment);
    trip.Segments.Add(cleanupSegment);
    trip.Segments.Add(responsiveSegment);
    trip.Segments.Add(failedSaveSegment);
    context.Users.Add(user);
    context.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = role.Id });
    context.Trips.Add(trip);
    context.Set<TransportProfile>().Add(profile);
    var manifest = new WaypointFixtureManifest(trip.Id, user.Id, user.UserName!, password, profile.Id, profile.Key,
        waypointSegment.Id, zeroSegment.Id, staleSegment.Id, routeWorkSegment.Id, closedLoopSegment.Id,
        cleanupSegment.Id, responsiveSegment.Id, failedSaveSegment.Id,
        from.Id, waypoint.Id, staleWaypoint.Id, alternate.Id, to.Id,
        waypointSegment.EstimatedDistanceKm!.Value, waypointSegment.EstimatedDuration!.Value.TotalMinutes,
        waypointSegment.EstimatedDurationSource.ToString(), 0,
        waypointSegment.RouteGeometry!.Coordinates.Select(item => new[] { item.X, item.Y }).ToArray(),
        [shadow.Id, region.Id], [from.Id, waypoint.Id, staleWaypoint.Id, alternate.Id, to.Id]);
    await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, FixtureJson.Options));
    await context.SaveChangesAsync();
    return manifest with { OriginalRowVersion = waypointSegment.RowVersion };
}

/// <summary>Creates an independently owned anchor-aware Segment for one browser workflow.</summary>
static Segment AnchoredRouteSegment(Trip trip, string userId, TransportProfile profile, int order, Place from, Place waypoint, Place to, string notes)
{
    var segment = Segment(trip, userId, profile, order, from, to, notes, true);
    segment.Waypoints.Add(new SegmentWaypoint
    {
        Segment = segment, SegmentId = segment.Id, Place = waypoint, PlaceId = waypoint.Id,
        Position = 0, RouteVertexIndex = 2
    });
    segment.RouteGeometry = Line(from.Location!.Coordinate,
        new Coordinate(23.72, 37.98), waypoint.Location!.Coordinate, to.Location!.Coordinate);
    return segment;
}

/// <summary>Rereads the exact deterministic route-work proposal independently of the browser response.</summary>
static async Task VerifyRouteWorkAsync(ApplicationDbContext context, WaypointFixtureManifest manifest)
{
    double[][] expected = [[23.70, 37.97], [23.71, 37.975], [23.72, 37.98], [23.74, 37.99], [23.78, 38.01]];
    var segment = await context.Segments.AsNoTracking()
        .Include(item => item.Waypoints.OrderBy(waypoint => waypoint.Position))
        .SingleAsync(item => item.Id == manifest.RouteWorkSegmentId);
    var coordinates = segment.RouteGeometry?.Coordinates.Select(item => new[] { item.X, item.Y }).ToArray();
    var failures = new List<string>();
    if (coordinates == null || !coordinates.SelectMany(item => item).SequenceEqual(expected.SelectMany(item => item))) failures.Add("coordinate order");
    if (!segment.Waypoints.Select(item => item.PlaceId).SequenceEqual(new[] { manifest.WaypointId })) failures.Add("waypoint identity");
    if (!segment.Waypoints.Select(item => item.RouteVertexIndex).SequenceEqual(new int?[] { 3 })) failures.Add("shifted route index");
    if (segment.RouteGeometry == null) failures.Add("custom route");
    if (segment.Mode != manifest.Mode || segment.TransportProfileId != manifest.ProfileId) failures.Add("profile authority");
    if (segment.EstimatedDistanceKm == null || segment.EstimatedDuration == null || segment.EstimatedDurationSource.ToString() != manifest.EstimatedDurationSource) failures.Add("measurement authority");
    if (segment.RowVersion == manifest.OriginalRowVersion) failures.Add("token refresh");
    if (failures.Count > 0) throw new InvalidOperationException("Route-work provider reread mismatch: " + string.Join(", ", failures));
    Console.WriteLine("provider-reread: exact custom coordinate order, waypoint identity, shifted index 3, profile, measurements, provenance, and refreshed token verified");
}

/// <summary>Independently rereads the retry result after the fixture-scoped provider outage.</summary>
static async Task VerifyFailedSaveAsync(ApplicationDbContext context, WaypointFixtureManifest manifest)
{
    double[][] expected = [[23.70, 37.97], [23.71, 37.975], [23.72, 37.98], [23.74, 37.99], [23.78, 38.01]];
    var segment = await context.Segments.AsNoTracking()
        .Include(item => item.Waypoints.OrderBy(waypoint => waypoint.Position))
        .SingleAsync(item => item.Id == manifest.FailedSaveSegmentId);
    var coordinates = segment.RouteGeometry?.Coordinates.Select(item => new[] { item.X, item.Y }).ToArray();
    if (coordinates == null || !coordinates.SelectMany(item => item).SequenceEqual(expected.SelectMany(item => item))
        || !segment.Waypoints.Select(item => item.RouteVertexIndex).SequenceEqual(new int?[] { 3 }))
        throw new InvalidOperationException("Failed-save retry provider reread did not preserve exact geometry and shifted waypoint index.");
    Console.WriteLine("provider-reread: failed-save retry persisted exact geometry and shifted index 3");
}

/// <summary>Changes the Segment row version through a second EF context for stale-token browser coverage.</summary>
static async Task DriftAsync(ApplicationDbContext context, WaypointFixtureManifest manifest)
{
    var segment = await context.Segments.SingleAsync(item => item.Id == manifest.WaypointSegmentId);
    segment.Notes = $"Externally drifted {Guid.NewGuid():N}";
    await context.SaveChangesAsync();
}

/// <summary>Rereads the ordinary notes save and proves every hidden aggregate field against independent constants.</summary>
static async Task VerifyPreservedAsync(ApplicationDbContext context, WaypointFixtureManifest manifest)
{
    const string expectedNotes = "<p>Browser ordinary visible edit</p>";
    var segment = await context.Segments.AsNoTracking()
        .Include(item => item.Waypoints.OrderBy(waypoint => waypoint.Position))
        .SingleAsync(item => item.Id == manifest.WaypointSegmentId);
    var coordinates = segment.RouteGeometry?.Coordinates.Select(item => new[] { item.X, item.Y }).ToArray();
    var failures = new List<string>();
    AssertCanonicalDistance(segment.RouteGeometry?.Coordinates ?? [], segment.EstimatedDistanceKm ?? double.NaN);
    if (segment.EstimatedDistanceKm != 8.303d) failures.Add($"distance={segment.EstimatedDistanceKm}");
    if (segment.EstimatedDuration != TimeSpan.FromMinutes(manifest.EstimatedDurationMinutes)) failures.Add($"duration={segment.EstimatedDuration}");
    if (segment.EstimatedDurationSource.ToString() != manifest.EstimatedDurationSource) failures.Add($"provenance={segment.EstimatedDurationSource}");
    if (segment.Mode != manifest.Mode) failures.Add($"mode={segment.Mode}");
    if (segment.TransportProfileId != manifest.ProfileId) failures.Add($"profile={segment.TransportProfileId}");
    if (coordinates == null || !coordinates.SelectMany(item => item).SequenceEqual(manifest.RouteCoordinates.SelectMany(item => item))) failures.Add("geometry");
    if (!segment.Waypoints.Select(item => item.PlaceId).SequenceEqual(new[] { manifest.WaypointId })) failures.Add("waypoint IDs");
    if (!segment.Waypoints.Select(item => item.Position).SequenceEqual(new[] { 0 })) failures.Add("waypoint positions");
    if (!segment.Waypoints.Select(item => item.RouteVertexIndex).SequenceEqual(new int?[] { 2 })) failures.Add("route vertex indices");
    if (segment.Notes != expectedNotes) failures.Add($"notes={segment.Notes}");
    if (segment.RowVersion == manifest.OriginalRowVersion) failures.Add($"token={segment.RowVersion}");
    if (failures.Count > 0)
        throw new InvalidOperationException("Provider reread mismatch: " + string.Join(", ", failures));
    Console.WriteLine("provider-reread: measurements, provenance, profile, geometry, waypoint identity/order/indices, notes, and token refresh verified");
}

/// <summary>Rereads the final #408 visible workflow without duplicating #407 provider coverage.</summary>
static async Task VerifyUiAsync(ApplicationDbContext context, WaypointFixtureManifest manifest)
{
    var segment = await context.Segments.AsNoTracking()
        .Include(item => item.Waypoints.OrderBy(waypoint => waypoint.Position))
        .SingleAsync(item => item.Id == manifest.ZeroSegmentId);
    if (!segment.Waypoints.Select(item => item.PlaceId).SequenceEqual(new[] { manifest.AlternateId }) ||
        !segment.Waypoints.Select(item => item.Position).SequenceEqual(new[] { 0 }) ||
        segment.Waypoints.Single().RouteVertexIndex != null || segment.RouteGeometry != null)
        throw new InvalidOperationException("#408 provider reread did not preserve the visible waypoint order/removal result.");
    Console.WriteLine("provider-reread: #408 visible waypoint order, removal, null indices, and fallback state verified");
}

/// <summary>Deletes only captured fixture identities.</summary>
static async Task CleanupAsync(ApplicationDbContext context, WaypointFixtureManifest manifest)
{
    await context.Trips.Where(item => item.Id == manifest.TripId && item.UserId == manifest.UserId).ExecuteDeleteAsync();
    await context.Set<TransportProfile>().Where(item => item.Id == manifest.ProfileId).ExecuteDeleteAsync();
    await context.UserRoles.Where(item => item.UserId == manifest.UserId).ExecuteDeleteAsync();
    await context.Users.Where(item => item.Id == manifest.UserId).ExecuteDeleteAsync();
}

/// <summary>Fails unless every captured row and association was removed.</summary>
static async Task VerifyAsync(ApplicationDbContext context, WaypointFixtureManifest manifest)
{
    var segmentIds = new[] { manifest.WaypointSegmentId, manifest.ZeroSegmentId, manifest.StaleSegmentId, manifest.RouteWorkSegmentId, manifest.ClosedLoopSegmentId,
        manifest.CleanupSegmentId, manifest.ResponsiveSegmentId, manifest.FailedSaveSegmentId };
    var counts = new Dictionary<string, int>
    {
        ["Trip"] = await context.Trips.CountAsync(item => item.Id == manifest.TripId),
        ["User"] = await context.Users.CountAsync(item => item.Id == manifest.UserId),
        ["UserRole"] = await context.UserRoles.CountAsync(item => item.UserId == manifest.UserId),
        ["Profile"] = await context.Set<TransportProfile>().CountAsync(item => item.Id == manifest.ProfileId),
        ["Region"] = await context.Regions.CountAsync(item => manifest.RegionIds.Contains(item.Id)),
        ["Place"] = await context.Places.CountAsync(item => manifest.PlaceIds.Contains(item.Id)),
        ["Segment"] = await context.Segments.CountAsync(item => segmentIds.Contains(item.Id)),
        ["Waypoint"] = await context.Set<SegmentWaypoint>().CountAsync(item => segmentIds.Contains(item.SegmentId))
    };
    if (counts.Any(item => item.Value != 0))
        throw new InvalidOperationException("Fixture cleanup left rows: " + string.Join(", ", counts.Select(item => $"{item.Key}={item.Value}")));
    Console.WriteLine("Fixture cleanup verified: " + string.Join(", ", counts.Select(item => $"{item.Key}=0")));
}

static Region Region(Trip trip, string userId, string name, int order)
{
    var region = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = name, DisplayOrder = order };
    trip.Regions.Add(region);
    return region;
}

static Place Place(Region region, string userId, string name, int order, double x, double y)
{
    var place = new Place
    {
        Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = userId, Name = name,
        DisplayOrder = order, Location = new Point(x, y) { SRID = 4326 }
    };
    region.Places.Add(place);
    return place;
}

static Segment Segment(Trip trip, string userId, TransportProfile profile, int order, Place from, Place to, string notes, bool custom)
{
    const double waypointDistanceKm = 8.303;
    return new()
    {
        Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, FromPlace = from, FromPlaceId = from.Id,
        ToPlace = to, ToPlaceId = to.Id, Mode = profile.Key, TransportProfile = profile, TransportProfileId = profile.Id,
        DisplayOrder = order, Notes = notes, EstimatedDistanceKm = custom ? waypointDistanceKm : 3.0,
        EstimatedDuration = TimeSpan.FromMinutes(custom ? 47 : 15), EstimatedDurationSource = EstimatedDurationSource.Manual
    };
}

static LineString Line(params Coordinate[] coordinates) => new(coordinates) { SRID = 4326 };

/// <summary>Calculates the literal fixture route independently with longitude/latitude Haversine evidence.</summary>
static void AssertCanonicalDistance(Coordinate[] coordinates, double observedKilometres)
{
    const double earthRadiusMetres = 6_371_000d;
    double[][] expectedCoordinates = [[23.70, 37.97], [23.72, 37.98], [23.74, 37.99], [23.78, 38.01]];
    if (!coordinates.Select(item => new[] { item.X, item.Y }).SelectMany(item => item)
            .SequenceEqual(expectedCoordinates.SelectMany(item => item)))
        throw new InvalidOperationException("Canonical #407 fixture geometry or coordinate order changed.");

    var unroundedMetres = 0d;
    for (var index = 1; index < expectedCoordinates.Length; index++)
    {
        var previous = expectedCoordinates[index - 1];
        var current = expectedCoordinates[index];
        var latitudeDelta = DegreesToRadians(current[1] - previous[1]);
        var longitudeDelta = DegreesToRadians(current[0] - previous[0]);
        var haversine = Math.Pow(Math.Sin(latitudeDelta / 2d), 2d)
            + Math.Cos(DegreesToRadians(previous[1])) * Math.Cos(DegreesToRadians(current[1]))
            * Math.Pow(Math.Sin(longitudeDelta / 2d), 2d);
        unroundedMetres += 2d * earthRadiusMetres * Math.Asin(Math.Sqrt(haversine));
    }

    var roundedKilometres = Math.Round(unroundedMetres / 1_000d, 3, MidpointRounding.AwayFromZero);
    if (roundedKilometres != 8.303d || observedKilometres != 8.303d || roundedKilometres == 9.407d)
        throw new InvalidOperationException($"Canonical #407 distance mismatch: {unroundedMetres:R} m -> {roundedKilometres:F3} km; observed {observedKilometres:F3} km.");
    Console.WriteLine($"independent-distance: {unroundedMetres:R} unrounded metres -> 8.303 km; former 9.407 km rejected");

    static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}
static async Task<WaypointFixtureManifest> ReadAsync(string path) =>
    JsonSerializer.Deserialize<WaypointFixtureManifest>(await File.ReadAllTextAsync(path), FixtureJson.Options)
    ?? throw new InvalidOperationException("Fixture manifest is empty.");

/// <summary>Exact captured identities used by #407 browser setup, mutation, and cleanup.</summary>
internal sealed record WaypointFixtureManifest(
    Guid TripId, string UserId, string Username, string Password, Guid ProfileId, string Mode,
    Guid WaypointSegmentId, Guid ZeroSegmentId, Guid StaleSegmentId, Guid RouteWorkSegmentId, Guid ClosedLoopSegmentId,
    Guid CleanupSegmentId, Guid ResponsiveSegmentId, Guid FailedSaveSegmentId,
    Guid FromId, Guid WaypointId, Guid StaleWaypointId, Guid AlternateId, Guid ToId,
    double EstimatedDistanceKm, double EstimatedDurationMinutes, string EstimatedDurationSource,
    uint OriginalRowVersion, double[][] RouteCoordinates,
    Guid[] RegionIds, Guid[] PlaceIds);

internal static class FixtureJson
{
    /// <summary>Provides the stable camel-case manifest consumed by the browser runner.</summary>
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
