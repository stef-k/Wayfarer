using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for <see cref="TripImportService"/> covering KML import operations,
/// import modes (Auto, Upsert, CreateNew), and entity synchronization.
/// </summary>
public class TripImportServiceTests : TestBase
{
    #region KML Test Data

    /// <summary>
    /// Creates a minimal Wayfarer-Extended-KML string for testing.
    /// </summary>
    private static string CreateWayfarerKml(
        Guid tripId,
        string tripName = "Test Trip",
        string? notes = null,
        double centerLat = 40.7128,
        double centerLon = -74.0060,
        int zoom = 10)
    {
        var notesXml = notes != null ? $@"
      <Data name=""NotesHtml""><value>{notes}</value></Data>" : "";

        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<kml xmlns=""http://www.opengis.net/kml/2.2"">
  <Document>
    <name>{tripName}</name>
    <ExtendedData>
      <Data name=""WayfarerSchemaVersion""><value>1</value></Data>
      <Data name=""TripId""><value>{tripId}</value></Data>
      <Data name=""CenterLat""><value>{centerLat}</value></Data>
      <Data name=""CenterLon""><value>{centerLon}</value></Data>
      <Data name=""Zoom""><value>{zoom}</value></Data>{notesXml}
    </ExtendedData>
  </Document>
</kml>";
    }

    /// <summary>
    /// Creates a Wayfarer KML with regions and places for testing.
    /// </summary>
    private static string CreateWayfarerKmlWithRegions(
        Guid tripId,
        string tripName,
        List<(Guid regionId, string regionName, List<(Guid placeId, string placeName, double lat, double lon)> places)> regions)
    {
        var regionXml = new StringBuilder();
        foreach (var (regionId, regionName, places) in regions)
        {
            regionXml.AppendLine($@"    <Folder>
      <name>{regionName}</name>
      <ExtendedData>
        <Data name=""RegionId""><value>{regionId}</value></Data>
        <Data name=""DisplayOrder""><value>1</value></Data>
      </ExtendedData>");

            foreach (var (placeId, placeName, lat, lon) in places)
            {
                regionXml.AppendLine($@"      <Placemark>
        <name>{placeName}</name>
        <ExtendedData>
          <Data name=""PlaceId""><value>{placeId}</value></Data>
          <Data name=""DisplayOrder""><value>1</value></Data>
        </ExtendedData>
        <Point>
          <coordinates>{lon},{lat},0</coordinates>
        </Point>
      </Placemark>");
            }

            regionXml.AppendLine("    </Folder>");
        }

        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<kml xmlns=""http://www.opengis.net/kml/2.2"">
  <Document>
    <name>{tripName}</name>
    <ExtendedData>
      <Data name=""WayfarerSchemaVersion""><value>1</value></Data>
      <Data name=""TripId""><value>{tripId}</value></Data>
      <Data name=""CenterLat""><value>40.7128</value></Data>
      <Data name=""CenterLon""><value>-74.0060</value></Data>
      <Data name=""Zoom""><value>10</value></Data>
    </ExtendedData>
{regionXml}
  </Document>
</kml>";
    }

    /// <summary>
    /// Creates a Google MyMaps KML string for testing format detection.
    /// </summary>
    private static string CreateGoogleMyMapsKml(string tripName = "Google Trip")
    {
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<kml xmlns=""http://www.opengis.net/kml/2.2"">
  <Document>
    <name>{tripName}</name>
    <Folder>
      <name>Test Region</name>
      <Placemark>
        <name>Test Place</name>
        <Point>
          <coordinates>-74.0060,40.7128,0</coordinates>
        </Point>
      </Placemark>
    </Folder>
  </Document>
</kml>";
    }

    /// <summary>
    /// Converts a string to a MemoryStream for testing.
    /// </summary>
    private static MemoryStream ToStream(string content)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(content));
    }

    /// <summary>Creates the compact native-v2 fallback A to B to C fixture.</summary>
    private static string CreateNativeV2Fallback(Guid tripId, Guid regionId, Guid fromId, Guid viaId, Guid toId, Guid segmentId) => $@"
<kml xmlns=""http://www.opengis.net/kml/2.2""><Document><name>Native v2</name><ExtendedData>
<Data name=""WayfarerSchemaVersion""><value>2</value></Data><Data name=""TripId""><value>{tripId:D}</value></Data>
</ExtendedData><Folder><name>Region</name><ExtendedData><Data name=""RegionId""><value>{regionId:D}</value></Data><Data name=""DisplayOrder""><value>0</value></Data></ExtendedData>
<Placemark><name>A</name><ExtendedData><Data name=""PlaceId""><value>{fromId:D}</value></Data></ExtendedData><Point><coordinates>0,0,0</coordinates></Point></Placemark>
<Placemark><name>B</name><ExtendedData><Data name=""PlaceId""><value>{viaId:D}</value></Data></ExtendedData><Point><coordinates>1,1,0</coordinates></Point></Placemark>
<Placemark><name>C</name><ExtendedData><Data name=""PlaceId""><value>{toId:D}</value></Data></ExtendedData><Point><coordinates>2,2,0</coordinates></Point></Placemark>
</Folder><Folder><name>Segments</name><Placemark><name>walk</name><ExtendedData>
<Data name=""SegmentId""><value>{segmentId:D}</value></Data><Data name=""FromPlaceId""><value>{fromId:D}</value></Data><Data name=""ToPlaceId""><value>{toId:D}</value></Data>
<Data name=""Mode""><value>walk</value></Data><Data name=""TransportProfileKey""><value>walk</value></Data><Data name=""DistanceKm""><value></value></Data>
<Data name=""DurationSeconds""><value></value></Data><Data name=""DurationSource""><value>Automatic</value></Data><Data name=""DisplayOrder""><value>0</value></Data>
<Data name=""NotesHtml""><value></value></Data><Data name=""HasCustomRoute""><value>false</value></Data><Data name=""WaypointPlaceIds""><value>{viaId:D}</value></Data>
<Data name=""WaypointRouteVertexIndices""><value>null</value></Data></ExtendedData><LineString><coordinates>0,0,0 1,1,0 2,2,0</coordinates></LineString>
</Placemark></Folder></Document></kml>";

    #endregion

    /// <summary>Proves create-new remaps native waypoint identity and restores fallback state.</summary>
    [Fact]
    public async Task ImportWayfarerKmlAsync_NativeV2Fallback_RemapsAndRestoresNullCustomState()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var walk = await db.Set<TransportProfile>().SingleAsync(profile => profile.Key == "walk");
        walk.IsActive = false;
        await db.SaveChangesAsync();
        var sourceIds = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);

        var importedId = await service.ImportWayfarerKmlAsync(ToStream(CreateNativeV2Fallback(
            sourceIds[0], sourceIds[1], sourceIds[2], sourceIds[3], sourceIds[4], sourceIds[5])), user.Id, TripImportMode.CreateNew);

        var segment = await db.Segments.Include(item => item.Waypoints).SingleAsync(item => item.TripId == importedId);
        var waypoint = Assert.Single(segment.Waypoints);
        Assert.NotEqual(sourceIds[3], waypoint.PlaceId);
        Assert.Null(segment.RouteGeometry);
        Assert.Null(waypoint.RouteVertexIndex);
        Assert.Equal("walk", segment.Mode);
        Assert.NotNull(segment.EstimatedDistanceKm);
    }

    #region Format Detection Tests

    [Fact]
    public async Task ImportWayfarerKmlAsync_DetectsWayfarerFormat_WhenTripIdPresent()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var tripId = Guid.NewGuid();
        var kml = CreateWayfarerKml(tripId, "Wayfarer Trip");

        // Act
        var resultId = await service.ImportWayfarerKmlAsync(ToStream(kml), user.Id);

        // Assert
        var trip = await db.Trips.FindAsync(resultId);
        Assert.NotNull(trip);
        // New trips always get "(Imported)" suffix when no existing trip matches
        Assert.Equal("Wayfarer Trip (Imported)", trip.Name);
    }

    [Fact]
    public async Task ImportWayfarerKmlAsync_DetectsGoogleMyMapsFormat_WhenNoTripId()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var kml = CreateGoogleMyMapsKml("Google Trip");

        // Act
        var resultId = await service.ImportWayfarerKmlAsync(ToStream(kml), user.Id);

        // Assert
        var trip = await db.Trips.FindAsync(resultId);
        Assert.NotNull(trip);
        // New trips always get "(Imported)" suffix
        Assert.Equal("Google Trip (Imported)", trip.Name);
    }

    #endregion

    #region Auto Mode Tests

    [Fact]
    public async Task ImportWayfarerKmlAsync_AutoMode_CreatesNewTrip_WhenTripDoesNotExist()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var tripId = Guid.NewGuid();
        var kml = CreateWayfarerKml(tripId, "New Trip");

        // Act
        var resultId = await service.ImportWayfarerKmlAsync(ToStream(kml), user.Id, TripImportMode.Auto);

        // Assert
        var trip = await db.Trips.FindAsync(resultId);
        Assert.NotNull(trip);
        // New trips always get "(Imported)" suffix in Auto mode
        Assert.Equal("New Trip (Imported)", trip.Name);
        Assert.Equal(user.Id, trip.UserId);
    }

    [Fact]
    public async Task ImportWayfarerKmlAsync_AutoMode_ThrowsDuplicateException_WhenTripOwnedByUser()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var existingTrip = TestDataFixtures.CreateTrip(user.Id, "Existing Trip");
        db.Users.Add(user);
        db.Trips.Add(existingTrip);
        await db.SaveChangesAsync();

        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var kml = CreateWayfarerKml(existingTrip.Id, "Updated Trip");

        // Act & Assert
        var ex = await Assert.ThrowsAsync<TripDuplicateException>(() =>
            service.ImportWayfarerKmlAsync(ToStream(kml), user.Id, TripImportMode.Auto));
        Assert.Equal(existingTrip.Id, ex.TripId);
    }

    [Fact]
    public async Task ImportWayfarerKmlAsync_AutoMode_CreatesClone_WhenTripOwnedByOtherUser()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var importer = TestDataFixtures.CreateUser();
        var existingTrip = TestDataFixtures.CreateTrip(owner.Id, "Owner's Trip");
        db.Users.AddRange(owner, importer);
        db.Trips.Add(existingTrip);
        await db.SaveChangesAsync();

        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var kml = CreateWayfarerKml(existingTrip.Id, "Owner's Trip");

        // Act
        var resultId = await service.ImportWayfarerKmlAsync(ToStream(kml), importer.Id, TripImportMode.Auto);

        // Assert
        Assert.NotEqual(existingTrip.Id, resultId);
        var clonedTrip = await db.Trips.FindAsync(resultId);
        Assert.NotNull(clonedTrip);
        Assert.Equal("Owner's Trip (Imported)", clonedTrip.Name);
        Assert.Equal(importer.Id, clonedTrip.UserId);
    }

    #endregion

    #region Upsert Mode Tests

    // Note: Upsert mode full update tests are skipped due to EF Core InMemory provider
    // limitations with entity tracking and SetValues when syncing shadow regions.
    // The service creates a shadow region that triggers concurrency issues in the
    // in-memory provider. These scenarios work correctly in production with a real database.

    [Fact]
    public async Task ImportWayfarerKmlAsync_UpsertMode_ThrowsException_WhenNotOwned()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var otherUser = TestDataFixtures.CreateUser();
        var existingTrip = TestDataFixtures.CreateTrip(owner.Id, "Owner's Trip");
        db.Users.AddRange(owner, otherUser);
        db.Trips.Add(existingTrip);
        await db.SaveChangesAsync();

        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var kml = CreateWayfarerKml(existingTrip.Id, "Attempted Update");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportWayfarerKmlAsync(ToStream(kml), otherUser.Id, TripImportMode.Upsert));
    }

    [Fact]
    public async Task ImportWayfarerKmlAsync_UpsertMode_ThrowsException_WhenTripNotFound()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var tripId = Guid.NewGuid();
        var kml = CreateWayfarerKml(tripId, "Non-existent Trip");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportWayfarerKmlAsync(ToStream(kml), user.Id, TripImportMode.Upsert));
    }

    #endregion

    #region CreateNew Mode Tests

    [Fact]
    public async Task ImportWayfarerKmlAsync_CreateNewMode_AlwaysCreatesNewTrip()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var existingTrip = TestDataFixtures.CreateTrip(user.Id, "Existing Trip");
        db.Users.Add(user);
        db.Trips.Add(existingTrip);
        await db.SaveChangesAsync();

        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var kml = CreateWayfarerKml(existingTrip.Id, "Trip to Clone");

        // Act
        var resultId = await service.ImportWayfarerKmlAsync(ToStream(kml), user.Id, TripImportMode.CreateNew);

        // Assert
        Assert.NotEqual(existingTrip.Id, resultId);
        var newTrip = await db.Trips.FindAsync(resultId);
        Assert.NotNull(newTrip);
        Assert.Equal("Trip to Clone (Imported)", newTrip.Name);
        Assert.Equal(user.Id, newTrip.UserId);

        // Original trip should remain unchanged
        var originalTrip = await db.Trips.FindAsync(existingTrip.Id);
        Assert.NotNull(originalTrip);
        Assert.Equal("Existing Trip", originalTrip.Name);
    }

    [Fact]
    public async Task ImportWayfarerKmlAsync_CreateNewMode_GeneratesNewIds()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var originalTripId = Guid.NewGuid();
        var kml = CreateWayfarerKml(originalTripId, "New Trip");

        // Act
        var resultId = await service.ImportWayfarerKmlAsync(ToStream(kml), user.Id, TripImportMode.CreateNew);

        // Assert
        Assert.NotEqual(originalTripId, resultId);
    }

    #endregion

    #region Region and Place Sync Tests

    [Fact]
    public async Task ImportWayfarerKmlAsync_CreatesRegionsAndPlaces()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var tripId = Guid.NewGuid();
        var regionId = Guid.NewGuid();
        var placeId = Guid.NewGuid();

        var kml = CreateWayfarerKmlWithRegions(tripId, "Trip with Places",
            new List<(Guid, string, List<(Guid, string, double, double)>)>
            {
                (regionId, "Test Region", new List<(Guid, string, double, double)>
                {
                    (placeId, "Test Place", 40.7128, -74.0060)
                })
            });

        // Act
        var resultId = await service.ImportWayfarerKmlAsync(ToStream(kml), user.Id);

        // Assert
        var trip = db.Trips
            .Include(t => t.Regions)
            .ThenInclude(r => r.Places)
            .FirstOrDefault(t => t.Id == resultId);

        Assert.NotNull(trip);
        // Should have shadow region + imported region
        Assert.True(trip.Regions.Count >= 1);

        var importedRegion = trip.Regions.FirstOrDefault(r => r.Name == "Test Region");
        Assert.NotNull(importedRegion);
        Assert.Single(importedRegion.Places);
        Assert.Equal("Test Place", importedRegion.Places.First().Name);
    }

    [Fact]
    public async Task ImportWayfarerKmlAsync_CreatesShadowRegion_WhenNotPresent()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var tripId = Guid.NewGuid();
        var kml = CreateWayfarerKml(tripId, "Trip without Shadow");

        // Act
        var resultId = await service.ImportWayfarerKmlAsync(ToStream(kml), user.Id);

        // Assert
        var trip = db.Trips
            .Include(t => t.Regions)
            .FirstOrDefault(t => t.Id == resultId);

        Assert.NotNull(trip);
        Assert.Contains(trip.Regions, r => r.Name == "Unassigned Places");
    }

    [Fact]
    public async Task ImportWayfarerKmlAsync_DoesNotDuplicateShadowRegion_WhenAlreadyPresent()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var existingTrip = TestDataFixtures.CreateTrip(user.Id, "Existing Trip");
        var shadowRegion = new Region
        {
            Id = Guid.NewGuid(),
            TripId = existingTrip.Id,
            UserId = user.Id,
            Name = "Unassigned Places",
            DisplayOrder = 0
        };
        existingTrip.Regions = new List<Region> { shadowRegion };

        db.Users.Add(user);
        db.Trips.Add(existingTrip);
        await db.SaveChangesAsync();

        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var kml = CreateWayfarerKml(existingTrip.Id, "Updated Trip");

        // Act - Use CreateNew to avoid duplicate exception
        var resultId = await service.ImportWayfarerKmlAsync(ToStream(kml), user.Id, TripImportMode.CreateNew);

        // Assert
        var trip = db.Trips
            .Include(t => t.Regions)
            .FirstOrDefault(t => t.Id == resultId);

        Assert.NotNull(trip);
        var shadowCount = trip.Regions.Count(r => r.Name == "Unassigned Places");
        Assert.Equal(1, shadowCount);
    }

    #endregion

    #region Metadata Update Tests

    [Fact]
    public async Task ImportWayfarerKmlAsync_UpdatesScalarProperties_OnNewTrip()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var tripId = Guid.NewGuid();
        var kml = CreateWayfarerKml(tripId, "Test Trip", "Trip notes", 48.8566, 2.3522, 15);

        // Act
        var resultId = await service.ImportWayfarerKmlAsync(ToStream(kml), user.Id);

        // Assert
        var trip = await db.Trips.FindAsync(resultId);
        Assert.NotNull(trip);
        Assert.Equal("Trip notes", trip.Notes);
        Assert.Equal(48.8566, trip.CenterLat);
        Assert.Equal(2.3522, trip.CenterLon);
        Assert.Equal(15, trip.Zoom);
        Assert.True(trip.UpdatedAt <= DateTime.UtcNow);
    }

    [Fact]
    public async Task ImportWayfarerKmlAsync_SetsCorrectUserId()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var tripId = Guid.NewGuid();
        var kml = CreateWayfarerKml(tripId, "User's Trip");

        // Act
        var resultId = await service.ImportWayfarerKmlAsync(ToStream(kml), user.Id);

        // Assert
        var trip = await db.Trips.FindAsync(resultId);
        Assert.NotNull(trip);
        Assert.Equal(user.Id, trip.UserId);
    }

    #endregion

    #region Entity Remapping Tests

    [Fact]
    public async Task ImportWayfarerKmlAsync_CreateNewMode_RemapsAllEntityIds()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var originalTripId = Guid.NewGuid();
        var originalRegionId = Guid.NewGuid();
        var originalPlaceId = Guid.NewGuid();

        var kml = CreateWayfarerKmlWithRegions(originalTripId, "Trip to Clone",
            new List<(Guid, string, List<(Guid, string, double, double)>)>
            {
                (originalRegionId, "Region to Clone", new List<(Guid, string, double, double)>
                {
                    (originalPlaceId, "Place to Clone", 40.7128, -74.0060)
                })
            });

        // Act
        var resultId = await service.ImportWayfarerKmlAsync(ToStream(kml), user.Id, TripImportMode.CreateNew);

        // Assert
        var trip = db.Trips
            .Include(t => t.Regions)
            .ThenInclude(r => r.Places)
            .FirstOrDefault(t => t.Id == resultId);

        Assert.NotNull(trip);
        Assert.NotEqual(originalTripId, trip.Id);

        var region = trip.Regions.FirstOrDefault(r => r.Name == "Region to Clone");
        Assert.NotNull(region);
        Assert.NotEqual(originalRegionId, region.Id);
        Assert.Equal(trip.Id, region.TripId);

        var place = region.Places.First();
        Assert.NotEqual(originalPlaceId, place.Id);
        Assert.Equal(region.Id, place.RegionId);
    }

    [Fact]
    public async Task ImportWayfarerKmlAsync_CreateNewMode_SetsUserIdOnAllEntities()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var tripId = Guid.NewGuid();
        var regionId = Guid.NewGuid();
        var placeId = Guid.NewGuid();

        var kml = CreateWayfarerKmlWithRegions(tripId, "Trip",
            new List<(Guid, string, List<(Guid, string, double, double)>)>
            {
                (regionId, "Region", new List<(Guid, string, double, double)>
                {
                    (placeId, "Place", 40.7128, -74.0060)
                })
            });

        // Act
        var resultId = await service.ImportWayfarerKmlAsync(ToStream(kml), user.Id, TripImportMode.CreateNew);

        // Assert
        var trip = db.Trips
            .Include(t => t.Regions)
            .ThenInclude(r => r.Places)
            .FirstOrDefault(t => t.Id == resultId);

        Assert.NotNull(trip);
        Assert.Equal(user.Id, trip.UserId);

        foreach (var region in trip.Regions)
        {
            Assert.Equal(user.Id, region.UserId);
            foreach (var place in region.Places)
            {
                Assert.Equal(user.Id, place.UserId);
            }
        }
    }

    #endregion

    #region Multiple Regions Tests

    [Fact]
    public async Task ImportWayfarerKmlAsync_HandlesMultipleRegions()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = new TripImportService(db, NullLogger<TripImportService>.Instance);
        var tripId = Guid.NewGuid();

        var kml = CreateWayfarerKmlWithRegions(tripId, "Multi-Region Trip",
            new List<(Guid, string, List<(Guid, string, double, double)>)>
            {
                (Guid.NewGuid(), "Region 1", new List<(Guid, string, double, double)>
                {
                    (Guid.NewGuid(), "Place 1A", 40.7128, -74.0060),
                    (Guid.NewGuid(), "Place 1B", 40.7580, -73.9855)
                }),
                (Guid.NewGuid(), "Region 2", new List<(Guid, string, double, double)>
                {
                    (Guid.NewGuid(), "Place 2A", 34.0522, -118.2437)
                }),
                (Guid.NewGuid(), "Region 3", new List<(Guid, string, double, double)>())
            });

        // Act
        var resultId = await service.ImportWayfarerKmlAsync(ToStream(kml), user.Id);

        // Assert
        var trip = db.Trips
            .Include(t => t.Regions)
            .ThenInclude(r => r.Places)
            .FirstOrDefault(t => t.Id == resultId);

        Assert.NotNull(trip);

        // Should have 3 imported regions + shadow region
        Assert.True(trip.Regions.Count >= 3);

        var region1 = trip.Regions.FirstOrDefault(r => r.Name == "Region 1");
        var region2 = trip.Regions.FirstOrDefault(r => r.Name == "Region 2");
        var region3 = trip.Regions.FirstOrDefault(r => r.Name == "Region 3");

        Assert.NotNull(region1);
        Assert.Equal(2, region1.Places.Count);

        Assert.NotNull(region2);
        Assert.Single(region2.Places);

        Assert.NotNull(region3);
        Assert.Empty(region3.Places);
    }

    #endregion
}
