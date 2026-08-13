using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Compact native-v2 create, replacement, profile, provenance, and generic-boundary workflow.</summary>
public sealed class NativeKmlV2ImportTests : TestBase
{
    [Fact]
    public async Task CreateNewFallback_RemapsAndRestoresNullCustomState()
    {
        var (db, user) = await CreateContextAsync();
        var walk = await db.Set<TransportProfile>().SingleAsync(profile => profile.Key == "walk");
        walk.IsActive = false;
        await db.SaveChangesAsync();
        var ids = Ids();
        var importedId = await Service(db).ImportWayfarerKmlAsync(Stream(Fallback(ids)), user.Id, TripImportMode.CreateNew);

        var segment = await db.Segments.Include(item => item.Waypoints).SingleAsync(item => item.TripId == importedId);
        var waypoint = Assert.Single(segment.Waypoints);
        Assert.NotEqual(ids[3], waypoint.PlaceId);
        Assert.Null(segment.RouteGeometry);
        Assert.Null(waypoint.RouteVertexIndex);
        Assert.NotNull(segment.EstimatedDistanceKm);
    }

    [Fact]
    public async Task CreateNewCustom_PreservesGeometryAndIndex()
    {
        var (db, user) = await CreateContextAsync();
        var ids = Ids();
        var kml = Fallback(ids).Replace("<value>false</value>", "<value>true</value>", StringComparison.Ordinal)
            .Replace("<value>null</value>", "<value>1</value>", StringComparison.Ordinal);

        var importedId = await Service(db).ImportWayfarerKmlAsync(Stream(kml), user.Id, TripImportMode.CreateNew);

        var segment = await db.Segments.Include(item => item.Waypoints).SingleAsync(item => item.TripId == importedId);
        Assert.Equal(new[] { 0d, 1d, 2d }, segment.RouteGeometry!.Coordinates.Select(coordinate => coordinate.X));
        Assert.Equal(1, Assert.Single(segment.Waypoints).RouteVertexIndex);
    }

    [Fact]
    public async Task Manual_PreservesExactSecondsAndProvenance()
    {
        var (db, user) = await CreateContextAsync();
        var ids = Ids();
        var kml = Fallback(ids).Replace("<Data name=\"DurationSeconds\"><value></value></Data>",
                "<Data name=\"DurationSeconds\"><value>90</value></Data>", StringComparison.Ordinal)
            .Replace("<value>Automatic</value>", "<value>Manual</value>", StringComparison.Ordinal);

        var importedId = await Service(db).ImportWayfarerKmlAsync(Stream(kml), user.Id, TripImportMode.CreateNew);

        var segment = await db.Segments.SingleAsync(item => item.TripId == importedId);
        Assert.Equal(EstimatedDurationSource.Manual, segment.EstimatedDurationSource);
        Assert.Equal(90, segment.EstimatedDuration!.Value.TotalSeconds);
    }

    [Fact]
    public async Task Upsert_ReplacesStaleChildren()
    {
        var (db, user) = await CreateContextAsync();
        var ids = Ids();
        var staleRegionId = Guid.NewGuid();
        var target = TestDataFixtures.CreateTrip(user.Id, "Old native trip");
        target.Id = ids[0];
        target.Regions.Add(new Region { Id = staleRegionId, TripId = target.Id, UserId = user.Id, Name = "Stale" });
        db.Trips.Add(target);
        await db.SaveChangesAsync();

        var importedId = await Service(db).ImportWayfarerKmlAsync(Stream(Fallback(ids)), user.Id, TripImportMode.Upsert);

        Assert.False(await db.Regions.AnyAsync(region => region.Id == staleRegionId));
        Assert.Equal(3, (await db.Regions.Include(region => region.Places).SingleAsync(region => region.TripId == importedId)).Places.Count);
        Assert.Equal(ids[3], Assert.Single((await db.Segments.Include(segment => segment.Waypoints)
            .SingleAsync(segment => segment.TripId == importedId)).Waypoints).PlaceId);
    }

    [Fact]
    public async Task UnknownProfile_LeavesUpsertTargetUnchangedAndTrackerClear()
    {
        var (db, user) = await CreateContextAsync();
        var ids = Ids();
        var target = TestDataFixtures.CreateTrip(user.Id, "Original name");
        target.Id = ids[0];
        db.Trips.Add(target);
        await db.SaveChangesAsync();
        var kml = Fallback(ids).Replace("<Data name=\"TransportProfileKey\"><value>walk</value></Data>",
            "<Data name=\"TransportProfileKey\"><value>unknown-profile</value></Data>", StringComparison.Ordinal);

        await Assert.ThrowsAsync<TripImportValidationException>(() =>
            Service(db).ImportWayfarerKmlAsync(Stream(kml), user.Id, TripImportMode.Upsert));

        Assert.Equal("Original name", (await db.Trips.AsNoTracking().SingleAsync(trip => trip.Id == ids[0])).Name);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GenericRoute_RemainsExactAndWaypointFree()
    {
        var (db, user) = await CreateContextAsync();
        const string kml = """
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document><name>Generic</name><Placemark><name>walk</name>
            <LineString><coordinates>0,0,0 0.5,0.25,0 1,1,0</coordinates></LineString></Placemark></Document></kml>
            """;

        var importedId = await Service(db).ImportWayfarerKmlAsync(Stream(kml), user.Id, TripImportMode.CreateNew);

        var segment = await db.Segments.Include(item => item.Waypoints).SingleAsync(item => item.TripId == importedId);
        Assert.Empty(segment.Waypoints);
        Assert.Equal(new[] { 0d, 0.5d, 1d }, segment.RouteGeometry!.Coordinates.Select(coordinate => coordinate.X));
    }

    private async Task<(ApplicationDbContext Db, ApplicationUser User)> CreateContextAsync()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (db, user);
    }

    private static TripImportService Service(ApplicationDbContext db) => new(db, NullLogger<TripImportService>.Instance);
    private static Guid[] Ids() => Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
    private static MemoryStream Stream(string value) => new(Encoding.UTF8.GetBytes(value));
    private static string Fallback(IReadOnlyList<Guid> id) => $@"
<kml xmlns=""http://www.opengis.net/kml/2.2""><Document><name>Native v2</name><ExtendedData>
<Data name=""WayfarerSchemaVersion""><value>2</value></Data><Data name=""TripId""><value>{id[0]:D}</value></Data>
</ExtendedData><Folder><name>Region</name><ExtendedData><Data name=""RegionId""><value>{id[1]:D}</value></Data><Data name=""DisplayOrder""><value>0</value></Data></ExtendedData>
<Placemark><name>A</name><ExtendedData><Data name=""PlaceId""><value>{id[2]:D}</value></Data></ExtendedData><Point><coordinates>0,0,0</coordinates></Point></Placemark>
<Placemark><name>B</name><ExtendedData><Data name=""PlaceId""><value>{id[3]:D}</value></Data></ExtendedData><Point><coordinates>1,1,0</coordinates></Point></Placemark>
<Placemark><name>C</name><ExtendedData><Data name=""PlaceId""><value>{id[4]:D}</value></Data></ExtendedData><Point><coordinates>2,2,0</coordinates></Point></Placemark>
</Folder><Folder><name>Segments</name><Placemark><name>walk</name><ExtendedData>
<Data name=""SegmentId""><value>{id[5]:D}</value></Data><Data name=""FromPlaceId""><value>{id[2]:D}</value></Data><Data name=""ToPlaceId""><value>{id[4]:D}</value></Data>
<Data name=""Mode""><value>walk</value></Data><Data name=""TransportProfileKey""><value>walk</value></Data><Data name=""DistanceKm""><value></value></Data>
<Data name=""DurationSeconds""><value></value></Data><Data name=""DurationSource""><value>Automatic</value></Data><Data name=""DisplayOrder""><value>0</value></Data>
<Data name=""NotesHtml""><value></value></Data><Data name=""HasCustomRoute""><value>false</value></Data><Data name=""WaypointPlaceIds""><value>{id[3]:D}</value></Data>
<Data name=""WaypointRouteVertexIndices""><value>null</value></Data></ExtendedData><LineString><coordinates>0,0,0 1,1,0 2,2,0</coordinates></LineString>
</Placemark></Folder></Document></kml>";
}
