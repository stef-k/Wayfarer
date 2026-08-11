using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Util;

const string connectionVariable = "WAYFARER_TEST_POSTGRES_CONNECTION";
if (args.Length is < 2 or > 3)
    throw new InvalidOperationException("Usage: Wayfarer.LifecycleBrowserFixture <provision|drift|reset-drift|cleanup|verify-cleanup> <manifest> [password].");

var command = args[0];
var manifestPath = Path.GetFullPath(args[1]);
var connectionString = Environment.GetEnvironmentVariable(connectionVariable)
    ?? throw new InvalidOperationException($"{connectionVariable} is required.");
var services = new ServiceCollection().AddEntityFrameworkNpgsql().BuildServiceProvider();
var options = new DbContextOptionsBuilder<ApplicationDbContext>()
    .UseNpgsql(connectionString, provider => provider.UseNetTopologySuite())
    .Options;

await using var context = new ApplicationDbContext(options, services);
switch (command)
{
    case "provision":
        await context.Database.MigrateAsync();
        var password = args.Length == 3 ? args[2] : throw new InvalidOperationException("Provision requires a run-owned password.");
        var manifest = await ProvisionAsync(context, password);
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, FixtureJson.Options));
        Console.WriteLine(manifestPath);
        break;
    case "drift":
        await ApplyDriftAsync(context, await ReadManifestAsync(manifestPath));
        break;
    case "phone-drift":
        await ApplyPhoneDriftAsync(context, await ReadManifestAsync(manifestPath));
        break;
    case "reset-drift":
        await ResetPhoneDriftAsync(context, await ReadManifestAsync(manifestPath));
        break;
    case "cleanup":
        await CleanupAsync(context, await ReadManifestAsync(manifestPath));
        await VerifyCleanupAsync(context, await ReadManifestAsync(manifestPath));
        break;
    case "verify-cleanup":
        await VerifyCleanupAsync(context, await ReadManifestAsync(manifestPath));
        break;
    default:
        throw new InvalidOperationException($"Unknown fixture command '{command}'.");
}

/// <summary>Creates one exact, uniquely identified #406 browser aggregate.</summary>
static async Task<LifecycleFixtureManifest> ProvisionAsync(ApplicationDbContext context, string password)
{
    var run = Guid.NewGuid().ToString("N");
    var user = new ApplicationUser
    {
        Id = $"issue406-{run}",
        UserName = $"issue406-{run}",
        NormalizedUserName = $"ISSUE406-{run}".ToUpperInvariant(),
        DisplayName = "Issue 406 browser fixture",
        IsActive = true,
        EmailConfirmed = true,
        SecurityStamp = Guid.NewGuid().ToString("N"),
        ConcurrencyStamp = Guid.NewGuid().ToString("N")
    };
    user.PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(user, password);
    var role = await context.Roles.SingleOrDefaultAsync(candidate => candidate.NormalizedName == "USER");
    if (role is null)
    {
        role = new IdentityRole(ApplicationRoles.User) { NormalizedName = "USER" };
        context.Roles.Add(role);
    }

    var trip = new Trip
    {
        Id = Guid.NewGuid(), UserId = user.Id, User = user, Name = $"Issue 406 lifecycle {run}",
        UpdatedAt = DateTime.UtcNow, CenterLat = 37.98, CenterLon = 23.73, Zoom = 12
    };
    var shadow = Region(trip, user.Id, "Unassigned Places", 0);
    var primary = Region(trip, user.Id, $"Lifecycle keep {run}", 1);
    var deletedRegion = Region(trip, user.Id, $"Lifecycle region delete {run}", 2);

    var start = Place(primary, user.Id, $"Start {run}", 1, 23.70, 37.97);
    var end = Place(primary, user.Id, $"End {run}", 2, 23.78, 38.01);
    var waypointOnly = Place(primary, user.Id, $"Waypoint only {run}", 3, 23.72, 37.98);
    var mixed = Place(primary, user.Id, $"Mixed delete {run}", 4, 23.73, 37.99);
    var stale = Place(primary, user.Id, $"Stale delete {run}", 5, 23.74, 38.00);
    var failure = Place(primary, user.Id, $"Failure delete {run}", 6, 23.75, 38.005);
    var phoneStale = Place(primary, user.Id, $"Phone stale {run}", 7, 23.755, 38.006);
    var phoneFailure = Place(primary, user.Id, $"Phone failure {run}", 8, 23.758, 38.008);
    var regionEndpoint = Place(deletedRegion, user.Id, $"Region endpoint {run}", 1, 23.76, 37.96);
    var regionWaypoint = Place(deletedRegion, user.Id, $"Region waypoint {run}", 2, 23.77, 37.965);
    var phoneRegion = Region(trip, user.Id, $"Phone region {run}", 3);
    var phoneRegionEndpoint = Place(phoneRegion, user.Id, $"Phone region endpoint {run}", 1, 23.765, 37.955);
    var phoneRegionWaypoint = Place(phoneRegion, user.Id, $"Phone region waypoint {run}", 2, 23.775, 37.966);
    var area = new Area
    {
        Id = Guid.NewGuid(), Region = deletedRegion, RegionId = deletedRegion.Id, Name = $"Region area {run}", DisplayOrder = 1,
        Geometry = Polygon(23.75, 37.95)
    };
    deletedRegion.Areas.Add(area);
    var phoneArea = new Area
    {
        Id = Guid.NewGuid(), Region = phoneRegion, RegionId = phoneRegion.Id, Name = $"Phone area {run}", DisplayOrder = 1,
        Geometry = Polygon(23.76, 37.95)
    };
    phoneRegion.Areas.Add(phoneArea);

    var profile = new TransportProfile
    {
        Id = Guid.NewGuid(), Key = $"issue406-{run}", Label = "Issue 406 fixture walk", Category = "fixture",
        PlanningSpeedKmh = 5, SortOrder = 9000, IsActive = true, IsSeeded = false
    };
    var segments = new List<Segment>();
    var waypointSegment = CustomSegment(trip, user.Id, profile, 1, start, end, waypointOnly, EstimatedDurationSource.Automatic);
    var mixedEndpoint = FallbackSegment(trip, user.Id, profile, 2, mixed, start, EstimatedDurationSource.Manual);
    var mixedSurvivor = CustomSegment(trip, user.Id, profile, 3, start, end, mixed, EstimatedDurationSource.Manual);
    var regionEndpointSegment = FallbackSegment(trip, user.Id, profile, 4, regionEndpoint, start, EstimatedDurationSource.Automatic);
    var regionSurvivor = CustomSegment(trip, user.Id, profile, 5, start, end, regionWaypoint, EstimatedDurationSource.Automatic);
    var staleSegment = CustomSegment(trip, user.Id, profile, 6, start, end, stale, EstimatedDurationSource.Automatic);
    var staleDriftSegment = FallbackSegment(trip, user.Id, profile, 7, start, end, EstimatedDurationSource.Automatic);
    var failureSegment = CustomSegment(trip, user.Id, profile, 8, start, end, failure, EstimatedDurationSource.Automatic);
    var phoneStaleSegment = CustomSegment(trip, user.Id, profile, 9, start, end, phoneStale, EstimatedDurationSource.Automatic);
    var phoneStaleDriftSegment = FallbackSegment(trip, user.Id, profile, 10, start, end, EstimatedDurationSource.Automatic);
    var phoneFailureSegment = CustomSegment(trip, user.Id, profile, 11, start, end, phoneFailure, EstimatedDurationSource.Automatic);
    var phoneRegionEndpointSegment = FallbackSegment(trip, user.Id, profile, 12, phoneRegionEndpoint, start, EstimatedDurationSource.Automatic);
    var phoneRegionSurvivor = CustomSegment(trip, user.Id, profile, 13, start, end, phoneRegionWaypoint, EstimatedDurationSource.Automatic);
    segments.AddRange([waypointSegment, mixedEndpoint, mixedSurvivor, regionEndpointSegment, regionSurvivor, staleSegment, staleDriftSegment, failureSegment,
        phoneStaleSegment, phoneStaleDriftSegment, phoneFailureSegment, phoneRegionEndpointSegment, phoneRegionSurvivor]);
    foreach (var segment in segments) trip.Segments.Add(segment);

    context.Users.Add(user);
    context.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = role.Id });
    context.Trips.Add(trip);
    context.Set<TransportProfile>().Add(profile);
    await context.SaveChangesAsync();

    return new LifecycleFixtureManifest(
        trip.Id, user.Id, user.UserName!, password, profile.Id,
        new Target(waypointOnly.Id, waypointOnly.Name, 0, 1),
        new Target(mixed.Id, mixed.Name, 1, 1),
        new RegionTarget(deletedRegion.Id, deletedRegion.Name, 2, 1, 1, 1),
        new Target(stale.Id, stale.Name, 0, 1),
        new Target(failure.Id, failure.Name, 0, 1),
        staleDriftSegment.Id,
        new Target(phoneStale.Id, phoneStale.Name, 0, 1),
        phoneStaleDriftSegment.Id,
        new Target(phoneFailure.Id, phoneFailure.Name, 0, 1),
        new RegionTarget(phoneRegion.Id, phoneRegion.Name, 2, 1, 1, 1),
        segments.Select(segment => segment.Id).ToArray(),
        [shadow.Id, primary.Id, deletedRegion.Id, phoneRegion.Id],
        [start.Id, end.Id, waypointOnly.Id, mixed.Id, stale.Id, failure.Id, phoneStale.Id, phoneFailure.Id, regionEndpoint.Id, regionWaypoint.Id,
            phoneRegionEndpoint.Id, phoneRegionWaypoint.Id],
        [area.Id, phoneArea.Id]);
}

/// <summary>Adds one captured waypoint identity so the first confirmation token becomes stale.</summary>
static async Task ApplyDriftAsync(ApplicationDbContext context, LifecycleFixtureManifest manifest)
{
    var exists = await context.Set<SegmentWaypoint>().AnyAsync(waypoint =>
        waypoint.SegmentId == manifest.StaleDriftSegmentId && waypoint.PlaceId == manifest.StalePlace.Id);
    if (exists) return;
    context.Set<SegmentWaypoint>().Add(new SegmentWaypoint
    {
        SegmentId = manifest.StaleDriftSegmentId, PlaceId = manifest.StalePlace.Id, Position = 0, RouteVertexIndex = null
    });
    await context.SaveChangesAsync();
}

/// <summary>Resets and reapplies only the reusable phone stale-confirmation association.</summary>
static async Task ResetPhoneDriftAsync(ApplicationDbContext context, LifecycleFixtureManifest manifest)
{
    await context.Set<SegmentWaypoint>().Where(waypoint =>
        waypoint.SegmentId == manifest.PhoneStaleDriftSegmentId && waypoint.PlaceId == manifest.PhoneStalePlace.Id).ExecuteDeleteAsync();
}

/// <summary>Adds the reusable phone-only association after its warning has been rendered.</summary>
static async Task ApplyPhoneDriftAsync(ApplicationDbContext context, LifecycleFixtureManifest manifest)
{
    context.Set<SegmentWaypoint>().Add(new SegmentWaypoint
    {
        SegmentId = manifest.PhoneStaleDriftSegmentId, PlaceId = manifest.PhoneStalePlace.Id, Position = 0, RouteVertexIndex = null
    });
    await context.SaveChangesAsync();
}

/// <summary>Deletes only the exact aggregate, profile, role link, and user recorded in the manifest.</summary>
static async Task CleanupAsync(ApplicationDbContext context, LifecycleFixtureManifest manifest)
{
    await context.Trips.Where(trip => trip.Id == manifest.TripId && trip.UserId == manifest.UserId).ExecuteDeleteAsync();
    await context.Set<TransportProfile>().Where(profile => profile.Id == manifest.TransportProfileId).ExecuteDeleteAsync();
    await context.UserRoles.Where(link => link.UserId == manifest.UserId).ExecuteDeleteAsync();
    await context.Users.Where(user => user.Id == manifest.UserId).ExecuteDeleteAsync();
}

/// <summary>Fails cleanup unless every captured run-owned identity has disappeared.</summary>
static async Task VerifyCleanupAsync(ApplicationDbContext context, LifecycleFixtureManifest manifest)
{
    var counts = new Dictionary<string, int>
    {
        ["Trip"] = await context.Trips.CountAsync(row => row.Id == manifest.TripId),
        ["User"] = await context.Users.CountAsync(row => row.Id == manifest.UserId),
        ["UserRole"] = await context.UserRoles.CountAsync(row => row.UserId == manifest.UserId),
        ["Region"] = await context.Regions.CountAsync(row => manifest.RegionIds.Contains(row.Id)),
        ["Place"] = await context.Places.CountAsync(row => manifest.PlaceIds.Contains(row.Id)),
        ["Area"] = await context.Areas.CountAsync(row => manifest.AreaIds.Contains(row.Id)),
        ["Segment"] = await context.Segments.CountAsync(row => manifest.SegmentIds.Contains(row.Id)),
        ["SegmentWaypoint"] = await context.Set<SegmentWaypoint>().CountAsync(row => manifest.SegmentIds.Contains(row.SegmentId)),
        ["TransportProfile"] = await context.Set<TransportProfile>().CountAsync(row => row.Id == manifest.TransportProfileId)
    };
    var remaining = counts.Where(pair => pair.Value != 0).ToArray();
    if (remaining.Length > 0)
        throw new InvalidOperationException("Fixture cleanup left rows: " + string.Join(", ", remaining.Select(pair => $"{pair.Key}={pair.Value}")));
    Console.WriteLine("Fixture cleanup verified: " + string.Join(", ", counts.Select(pair => $"{pair.Key}=0")));
}

static Region Region(Trip trip, string userId, string name, int order)
{
    var region = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = name, DisplayOrder = order };
    trip.Regions.Add(region);
    return region;
}

static Place Place(Region region, string userId, string name, int order, double longitude, double latitude)
{
    var place = new Place
    {
        Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = userId, Name = name,
        DisplayOrder = order, Location = Point(longitude, latitude), Address = $"Fixture address {order}"
    };
    region.Places.Add(place);
    return place;
}

static Segment CustomSegment(Trip trip, string userId, TransportProfile profile, int order, Place from, Place to, Place waypoint, EstimatedDurationSource source)
{
    var segment = CreateSegment(trip, userId, profile, order, from, to, source, null);
    var anonymous = new Coordinate((from.Location!.X + waypoint.Location!.X) / 2, (from.Location.Y + waypoint.Location.Y) / 2);
    segment.RouteGeometry = Line([from.Location.Coordinate, anonymous, waypoint.Location.Coordinate, to.Location!.Coordinate]);
    segment.Waypoints.Add(new SegmentWaypoint { Segment = segment, SegmentId = segment.Id, Place = waypoint, PlaceId = waypoint.Id, Position = 0, RouteVertexIndex = 2 });
    return segment;
}

static Segment FallbackSegment(Trip trip, string userId, TransportProfile profile, int order, Place from, Place to, EstimatedDurationSource source) =>
    CreateSegment(trip, userId, profile, order, from, to, source, null);

static Segment CreateSegment(Trip trip, string userId, TransportProfile profile, int order, Place from, Place to, EstimatedDurationSource source, LineString? geometry)
{
    return new Segment
    {
        Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, FromPlace = from, FromPlaceId = from.Id,
        ToPlace = to, ToPlaceId = to.Id, Mode = profile.Key, TransportProfile = profile, TransportProfileId = profile.Id,
        RouteGeometry = geometry, EstimatedDistanceKm = geometry is null ? 1.25 : 2.5,
        EstimatedDuration = source == EstimatedDurationSource.Manual ? TimeSpan.FromMinutes(17) : TimeSpan.FromMinutes(30),
        EstimatedDurationSource = source, DisplayOrder = order
    };
}

static Point Point(double longitude, double latitude) => new(longitude, latitude) { SRID = 4326 };
static LineString Line(Coordinate[] coordinates) => new(coordinates) { SRID = 4326 };
static Polygon Polygon(double longitude, double latitude) => new(new LinearRing([
    new(longitude, latitude), new(longitude + .01, latitude), new(longitude + .01, latitude + .01),
    new(longitude, latitude + .01), new(longitude, latitude)
])) { SRID = 4326 };

static async Task<LifecycleFixtureManifest> ReadManifestAsync(string path) =>
    JsonSerializer.Deserialize<LifecycleFixtureManifest>(await File.ReadAllTextAsync(path), FixtureJson.Options)
    ?? throw new InvalidOperationException("Fixture manifest is empty.");

internal sealed record Target(Guid Id, string Name, int EndpointSegments, int WaypointOnlySegments);
internal sealed record RegionTarget(Guid Id, string Name, int DeletedPlaces, int DeletedAreas, int EndpointSegments, int WaypointOnlySegments);
internal sealed record LifecycleFixtureManifest(
    Guid TripId, string UserId, string Username, string Password, Guid TransportProfileId,
    Target WaypointOnlyPlace, Target MixedPlace, RegionTarget DeletedRegion, Target StalePlace, Target FailurePlace,
    Guid StaleDriftSegmentId, Target PhoneStalePlace, Guid PhoneStaleDriftSegmentId, Target PhoneFailurePlace, RegionTarget PhoneRegion,
    Guid[] SegmentIds, Guid[] RegionIds, Guid[] PlaceIds, Guid[] AreaIds);

internal static class FixtureJson
{
    /// <summary>Provides the stable camel-case fixture manifest format consumed by Playwright.</summary>
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
