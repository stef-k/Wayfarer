using System.Globalization;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for <see cref="TripExportService"/> covering KML export operations.
/// </summary>
[Collection(PlaywrightEnvironmentTestCollection.Name), PlaywrightEnvironmentIsolation]
public class TripExportServiceTests : TestBase
{
    /// <summary>
    /// Creates a TripExportService with minimal mocked dependencies for scoped export testing.
    /// </summary>
    private TripExportService CreateService(ApplicationDbContext db, IImageProxyService? imageProxyService = null)
    {
        var mockConfig = new Mock<IConfiguration>();

        return new TripExportService(
            db,
            null!, // MapSnapshotService - not needed for KML
            null!, // IHttpContextAccessor - not needed for KML
            null!, // LinkGenerator - not needed for KML
            null!, // IRazorViewRenderer - not needed for KML
            NullLogger<TripExportService>.Instance,
            mockConfig.Object,
            null!, imageProxyService ?? Mock.Of<IImageProxyService>()
        );
    }

    #region GenerateWayfarerKml Tests

    [Fact]
    public void GenerateWayfarerKml_ReturnsValidKml_ForMinimalTrip()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id, "Test Trip");
        trip.Regions = new List<Region>();
        trip.Segments = new List<Segment>();
        trip.Tags = new List<Tag>();

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.SaveChanges();

        var service = CreateService(db);

        // Act
        var kml = service.GenerateWayfarerKml(trip.Id);

        // Assert
        Assert.NotNull(kml);
        Assert.Contains("<kml", kml);
        Assert.Contains("Test Trip", kml);
    }

    [Fact]
    public void GenerateWayfarerKml_ThrowsException_WhenTripNotFound()
    {
        // Arrange
        var db = CreateDbContext();
        var service = CreateService(db);

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() =>
            service.GenerateWayfarerKml(Guid.NewGuid()));
        Assert.Contains("Trip not found", ex.Message);
    }

    [Fact]
    public void GenerateWayfarerKml_IncludesRegions()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id, "Trip with Regions");
        var region = new Region
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = user.Id,
            Name = "Test Region",
            DisplayOrder = 1,
            Places = new List<Place>(),
            Areas = new List<Area>()
        };
        trip.Regions = new List<Region> { region };
        trip.Segments = new List<Segment>();
        trip.Tags = new List<Tag>();

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.SaveChanges();

        var service = CreateService(db);

        // Act
        var kml = service.GenerateWayfarerKml(trip.Id);

        // Assert
        Assert.Contains("Test Region", kml);
        Assert.Contains(region.Id.ToString(), kml);
    }

    [Fact]
    public void GenerateWayfarerKml_IncludesPlaces()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id, "Trip with Places");
        var region = new Region
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = user.Id,
            Name = "Region 1",
            DisplayOrder = 1,
            Areas = new List<Area>()
        };
        var place = new Place
        {
            Id = Guid.NewGuid(),
            RegionId = region.Id,
            UserId = user.Id,
            Name = "Test Place",
            Location = new Point(-74.0060, 40.7128) { SRID = 4326 },
            DisplayOrder = 1
        };
        region.Places = new List<Place> { place };
        trip.Regions = new List<Region> { region };
        trip.Segments = new List<Segment>();
        trip.Tags = new List<Tag>();

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.SaveChanges();

        var service = CreateService(db);

        // Act
        var kml = service.GenerateWayfarerKml(trip.Id);

        // Assert
        Assert.Contains("Test Place", kml);
        Assert.Contains(place.Id.ToString(), kml);
        Assert.Contains("-74.006", kml); // longitude
        Assert.Contains("40.7128", kml); // latitude
    }

    [Fact]
    public void GenerateWayfarerKml_IncludesSegments()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id, "Trip with Segments");
        var region = new Region
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = user.Id,
            Name = "Region 1",
            DisplayOrder = 1,
            Places = new List<Place>(),
            Areas = new List<Area>()
        };
        var segment = new Segment
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = user.Id,
            DisplayOrder = 1,
            Mode = "driving",
            RouteGeometry = new LineString(new[]
            {
                new Coordinate(-74.0060, 40.7128),
                new Coordinate(-73.9855, 40.7580)
            }) { SRID = 4326 }
        }; segment.EstimatedDistanceKm = SegmentMeasurementCalculator.CalculateDistance(segment.RouteGeometry.Coordinates).RoundedKilometres;
        trip.Regions = new List<Region> { region };
        trip.Segments = new List<Segment> { segment };
        trip.Tags = new List<Tag>();

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.SaveChanges();

        var service = CreateService(db);

        // Act
        var kml = service.GenerateWayfarerKml(trip.Id);

        // Assert
        Assert.Contains(segment.Id.ToString(), kml);
        Assert.Contains("LineString", kml);
    }

    [Fact]
    public void GenerateWayfarerKml_IncludesTags()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id, "Trip with Tags");
        var tag = new Tag
        {
            Id = Guid.NewGuid(),
            Name = "Adventure",
            Slug = "adventure"
        };
        trip.Regions = new List<Region>();
        trip.Segments = new List<Segment>();
        trip.Tags = new List<Tag> { tag };

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.SaveChanges();

        var service = CreateService(db);

        // Act
        var kml = service.GenerateWayfarerKml(trip.Id);

        // Assert
        Assert.Contains("adventure", kml);
    }

    #endregion

    #region GenerateGoogleMyMapsKml Tests

    [Fact]
    public void GenerateGoogleMyMapsKml_ReturnsValidKml_ForMinimalTrip()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id, "Google Maps Trip");
        trip.Regions = new List<Region>();
        trip.Segments = new List<Segment>();
        trip.Tags = new List<Tag>();

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.SaveChanges();

        var service = CreateService(db);

        // Act
        var kml = service.GenerateGoogleMyMapsKml(trip.Id);

        // Assert
        Assert.NotNull(kml);
        Assert.Contains("<kml", kml);
        Assert.Contains("Google Maps Trip", kml);
    }

    [Fact]
    public void GenerateGoogleMyMapsKml_IncludesTagsAsExtendedData()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id, "Tagged Trip");
        var tag1 = new Tag { Id = Guid.NewGuid(), Name = "Adventure", Slug = "adventure" };
        var tag2 = new Tag { Id = Guid.NewGuid(), Name = "Beach", Slug = "beach" };
        trip.Regions = new List<Region>();
        trip.Segments = new List<Segment>();
        trip.Tags = new List<Tag> { tag1, tag2 };

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.SaveChanges();

        var service = CreateService(db);

        // Act
        var kml = service.GenerateGoogleMyMapsKml(trip.Id);

        // Assert
        Assert.Contains("adventure", kml);
        Assert.Contains("beach", kml);
        Assert.Contains("ExtendedData", kml);
    }

    [Fact]
    public void GenerateGoogleMyMapsKml_CreatesStylesForPlaces()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id, "Styled Trip");
        var region = new Region
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = user.Id,
            Name = "Region 1",
            DisplayOrder = 1,
            Areas = new List<Area>()
        };
        var place = new Place
        {
            Id = Guid.NewGuid(),
            RegionId = region.Id,
            UserId = user.Id,
            Name = "Styled Place",
            Location = new Point(-74.0060, 40.7128) { SRID = 4326 },
            IconName = "hotel",
            MarkerColor = "blue",
            DisplayOrder = 1
        };
        region.Places = new List<Place> { place };
        trip.Regions = new List<Region> { region };
        trip.Segments = new List<Segment>();
        trip.Tags = new List<Tag>();

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.SaveChanges();

        var service = CreateService(db);

        // Act
        var kml = service.GenerateGoogleMyMapsKml(trip.Id);

        // Assert
        Assert.Contains("<Style", kml);
        Assert.Contains("IconStyle", kml);
    }

    [Fact]
    public void GenerateGoogleMyMapsKml_CreatesFoldersForRegions()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id, "Multi-Region Trip");
        var region1 = new Region
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = user.Id,
            Name = "First Region",
            DisplayOrder = 1,
            Places = new List<Place>(),
            Areas = new List<Area>()
        };
        var region2 = new Region
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = user.Id,
            Name = "Second Region",
            DisplayOrder = 2,
            Places = new List<Place>(),
            Areas = new List<Area>()
        };
        trip.Regions = new List<Region> { region1, region2 };
        trip.Segments = new List<Segment>();
        trip.Tags = new List<Tag>();

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.SaveChanges();

        var service = CreateService(db);

        // Act
        var kml = service.GenerateGoogleMyMapsKml(trip.Id);

        // Assert
        Assert.Contains("<Folder>", kml);
        Assert.Contains("First Region", kml);
        Assert.Contains("Second Region", kml);
    }

    [Fact]
    public void GenerateGoogleMyMapsKml_IncludesAreasAsPolygons()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id, "Areas Trip");
        var region = new Region
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = user.Id,
            Name = "Region 1",
            DisplayOrder = 1,
            Places = new List<Place>()
        };
        var polygon = new Polygon(new LinearRing(new[]
        {
            new Coordinate(-74.0, 40.7),
            new Coordinate(-74.0, 40.8),
            new Coordinate(-73.9, 40.8),
            new Coordinate(-73.9, 40.7),
            new Coordinate(-74.0, 40.7) // closed ring
        })) { SRID = 4326 };
        var area = new Area
        {
            Id = Guid.NewGuid(),
            RegionId = region.Id,
            Name = "Test Area",
            Notes = "An area description",
            Geometry = polygon,
            DisplayOrder = 1
        };
        region.Areas = new List<Area> { area };
        trip.Regions = new List<Region> { region };
        trip.Segments = new List<Segment>();
        trip.Tags = new List<Tag>();

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.SaveChanges();

        var service = CreateService(db);

        // Act
        var kml = service.GenerateGoogleMyMapsKml(trip.Id);

        // Assert
        Assert.Contains("<Polygon>", kml);
        Assert.Contains("Test Area", kml);
        Assert.Contains("An area description", kml);
        Assert.Contains("<LinearRing>", kml);
    }

    /// <summary>Point and line tuples retain longitude/latitude order and full precision in any request culture.</summary>
    [Theory]
    [InlineData("el-GR")]
    [InlineData("")]
    public void GenerateGoogleMyMapsKml_IncludesPlacesAndLinesWithInvariantCoordinates(string culture)
    {
        using var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id, "Coordinates Trip");
        var region = TestDataFixtures.CreateRegion(trip, "Region 1");
        var from = TestDataFixtures.CreatePlace(region, "Start Point", 37.25, 23.25);
        var to = TestDataFixtures.CreatePlace(region, "End Point", 37.123456789012344, -73.12345678901234, 1);
        from.Notes = "A great park";
        region.Places = [from, to];
        trip.Regions = [region];
        trip.Segments = [new Segment
        {
            Id = Guid.NewGuid(), TripId = trip.Id, UserId = user.Id,
            FromPlaceId = from.Id, ToPlaceId = to.Id, Mode = "driving",
            RouteGeometry = new LineString([from.Location!.Coordinate, to.Location!.Coordinate]) { SRID = 4326 }
        }];
        db.Users.Add(user);
        db.Trips.Add(trip);
        db.SaveChanges();
        var service = CreateService(db);

        // CurrentCulture flows with the test execution context; always restore it, including on failure.
        var previousCulture = CultureInfo.CurrentCulture;
        string kml;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            kml = service.GenerateGoogleMyMapsKml(trip.Id);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }

        XNamespace ns = "http://www.opengis.net/kml/2.2";
        var doc = XDocument.Parse(kml);
        Assert.Equal(2, doc.Descendants(ns + "Point").Count());
        Assert.Single(doc.Descendants(ns + "LineString"));
        var tuples = doc.Descendants(ns + "coordinates")
            .SelectMany(element => element.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(tuple =>
            {
                var components = tuple.Split(',');
                Assert.Equal(3, components.Length);
                return components.Select(value => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
            }).ToArray();
        Assert.Equal(4, tuples.Length);
        Assert.Equal(new[] { from.Location.X, from.Location.Y, 0d }, tuples[0]);
        Assert.Equal(new[] { to.Location.X, to.Location.Y, 0d }, tuples[1]);
        Assert.Equal(tuples[0], tuples[2]);
        Assert.Equal(tuples[1], tuples[3]);
        Assert.Contains("Start Point", kml);
        Assert.Contains("End Point", kml);
        Assert.Contains("A great park", kml);
        Assert.Contains("driving", kml);
    }

    [Fact]
    public void GenerateGoogleMyMapsKml_SkipsPlacesWithoutLocation()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id, "Mixed Places Trip");
        var region = new Region
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = user.Id,
            Name = "Region 1",
            DisplayOrder = 1,
            Areas = new List<Area>()
        };
        var placeWithLocation = new Place
        {
            Id = Guid.NewGuid(),
            RegionId = region.Id,
            UserId = user.Id,
            Name = "Has Location",
            Location = new Point(-74.0060, 40.7128) { SRID = 4326 },
            IconName = "marker",
            MarkerColor = "blue",
            DisplayOrder = 1
        };
        var placeWithoutLocation = new Place
        {
            Id = Guid.NewGuid(),
            RegionId = region.Id,
            UserId = user.Id,
            Name = "No Location",
            Location = null,
            IconName = "marker",
            MarkerColor = "blue",
            DisplayOrder = 2
        };
        region.Places = new List<Place> { placeWithLocation, placeWithoutLocation };
        trip.Regions = new List<Region> { region };
        trip.Segments = new List<Segment>();
        trip.Tags = new List<Tag>();

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.SaveChanges();

        var service = CreateService(db);

        // Act
        var kml = service.GenerateGoogleMyMapsKml(trip.Id);

        // Assert
        Assert.Contains("Has Location", kml);
        Assert.DoesNotContain("No Location", kml);
    }

    [Fact]
    public void GenerateGoogleMyMapsKml_OrdersRegionsByDisplayOrder()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id, "Ordered Trip");
        var region1 = new Region
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = user.Id,
            Name = "Third",
            DisplayOrder = 3,
            Places = new List<Place>(),
            Areas = new List<Area>()
        };
        var region2 = new Region
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = user.Id,
            Name = "First",
            DisplayOrder = 1,
            Places = new List<Place>(),
            Areas = new List<Area>()
        };
        var region3 = new Region
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = user.Id,
            Name = "Second",
            DisplayOrder = 2,
            Places = new List<Place>(),
            Areas = new List<Area>()
        };
        trip.Regions = new List<Region> { region1, region2, region3 };
        trip.Segments = new List<Segment>();
        trip.Tags = new List<Tag>();

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.SaveChanges();

        var service = CreateService(db);

        // Act
        var kml = service.GenerateGoogleMyMapsKml(trip.Id);

        // Assert - Check order by finding positions
        var firstIndex = kml.IndexOf("01 – First");
        var secondIndex = kml.IndexOf("02 – Second");
        var thirdIndex = kml.IndexOf("03 – Third");

        Assert.True(firstIndex < secondIndex, "First should appear before Second");
        Assert.True(secondIndex < thirdIndex, "Second should appear before Third");
    }

    [Fact]
    public void GenerateGoogleMyMapsKml_IncludesLineStyle()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id, "Line Style Trip");
        trip.Regions = new List<Region>();
        trip.Segments = new List<Segment>();
        trip.Tags = new List<Tag>();

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.SaveChanges();

        var service = CreateService(db);

        // Act
        var kml = service.GenerateGoogleMyMapsKml(trip.Id);

        // Assert
        Assert.Contains("wf-line", kml);
        Assert.Contains("<LineStyle>", kml);
    }

    [Fact]
    public void GenerateGoogleMyMapsKml_IncludesPolyStyle()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id, "Poly Style Trip");
        trip.Regions = new List<Region>();
        trip.Segments = new List<Segment>();
        trip.Tags = new List<Tag>();

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.SaveChanges();

        var service = CreateService(db);

        // Act
        var kml = service.GenerateGoogleMyMapsKml(trip.Id);

        // Assert
        Assert.Contains("wf-area", kml);
        Assert.Contains("<PolyStyle>", kml);
    }

    [Fact]
    public void GenerateGoogleMyMapsKml_ProducesValidXml()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id, "XML Validation Trip");
        var region = new Region
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = user.Id,
            Name = "Region with Special Chars <>&",
            DisplayOrder = 1,
            Places = new List<Place>(),
            Areas = new List<Area>()
        };
        trip.Regions = new List<Region> { region };
        trip.Segments = new List<Segment>();
        trip.Tags = new List<Tag>();

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.SaveChanges();

        var service = CreateService(db);

        // Act
        var kml = service.GenerateGoogleMyMapsKml(trip.Id);

        // Assert - should parse as valid XML
        var doc = XDocument.Parse(kml);
        Assert.NotNull(doc);
        Assert.NotNull(doc.Root);
    }

    #endregion
}
