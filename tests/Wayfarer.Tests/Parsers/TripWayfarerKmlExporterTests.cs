using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>
/// Tests for the TripWayfarerKmlExporter which exports trips to Wayfarer-Extended-KML format.
/// </summary>
public class TripWayfarerKmlExporterTests
{
    [Fact]
    public void BuildKml_MinimalTrip_ReturnsValidKml()
    {
        // Arrange
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            Name = "Test Trip",
            UpdatedAt = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc)
        };

        // Act
        var kml = TripWayfarerKmlExporter.BuildKml(trip);

        // Assert
        Assert.NotNull(kml);
        Assert.Contains("<kml", kml);
        Assert.Contains("<Document>", kml);
        Assert.Contains("<name>Test Trip</name>", kml);
        Assert.Contains("</kml>", kml);
    }

    [Fact]
    public void BuildKml_TripWithMetadata_IncludesExtendedData()
    {
        // Arrange
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            Name = "Metadata Trip",
            UpdatedAt = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc),
            CoverImageUrl = "https://example.com/cover.jpg",
            Notes = "<p>Trip notes</p>",
            CenterLat = 40.7128,
            CenterLon = -74.0060,
            Zoom = 12
        };

        // Act
        var kml = TripWayfarerKmlExporter.BuildKml(trip);

        // Assert
        Assert.Contains("TripId", kml);
        Assert.Contains("UpdatedAt", kml);
        Assert.Contains("CoverImageUrl", kml);
        Assert.Contains("https://example.com/cover.jpg", kml);
        Assert.Contains("NotesHtml", kml);
        Assert.Contains("CenterLat", kml);
        Assert.Contains("40.7128", kml);
        Assert.Contains("CenterLon", kml);
        Assert.Contains("-74.006", kml);
        Assert.Contains("Zoom", kml);
        Assert.Contains("12", kml);
    }

    [Fact]
    public void BuildKml_TripWithRegion_CreatesFolderForRegion()
    {
        // Arrange
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            Name = "Trip with Region",
            UpdatedAt = DateTime.UtcNow,
            Regions = new List<Region>
            {
                new Region
                {
                    Id = Guid.NewGuid(),
                    Name = "New York City",
                    DisplayOrder = 0,
                    Notes = "NYC notes"
                }
            }
        };

        // Act
        var kml = TripWayfarerKmlExporter.BuildKml(trip);

        // Assert
        Assert.Contains("<Folder>", kml);
        Assert.Contains("<name>New York City</name>", kml);
        Assert.Contains("RegionId", kml);
        Assert.Contains("DisplayOrder", kml);
    }

    [Fact]
    public void BuildKml_TripWithPlace_CreatesPlacemark()
    {
        // Arrange
        var regionId = Guid.NewGuid();
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            Name = "Trip with Place",
            UpdatedAt = DateTime.UtcNow,
            Regions = new List<Region>
            {
                new Region
                {
                    Id = regionId,
                    Name = "Manhattan",
                    DisplayOrder = 0,
                    Places = new List<Place>
                    {
                        new Place
                        {
                            Id = Guid.NewGuid(),
                            Name = "Times Square",
                            Location = new Point(-73.9855, 40.7580) { SRID = 4326 },
                            IconName = "landmark",
                            MarkerColor = "blue",
                            Address = "Times Square, NYC",
                            DisplayOrder = 0
                        }
                    }
                }
            }
        };

        // Act
        var kml = TripWayfarerKmlExporter.BuildKml(trip);

        // Assert
        Assert.Contains("<Placemark>", kml);
        Assert.Contains("<name>Times Square</name>", kml);
        Assert.Contains("<Point>", kml);
        Assert.Contains("<coordinates>", kml);
        Assert.Contains("-73.9855", kml);
        Assert.Contains("40.758", kml);
        Assert.Contains("PlaceId", kml);
        Assert.Contains("IconName", kml);
        Assert.Contains("landmark", kml);
        Assert.Contains("MarkerColor", kml);
        Assert.Contains("blue", kml);
        Assert.Contains("Address", kml);
    }

    [Fact]
    public void BuildKml_TripWithMultiplePlaces_CreatesStylesForEach()
    {
        // Arrange
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            Name = "Multi-place Trip",
            UpdatedAt = DateTime.UtcNow,
            Regions = new List<Region>
            {
                new Region
                {
                    Id = Guid.NewGuid(),
                    Name = "Region",
                    DisplayOrder = 0,
                    Places = new List<Place>
                    {
                        new Place
                        {
                            Id = Guid.NewGuid(),
                            Name = "Place 1",
                            Location = new Point(-74.0060, 40.7128) { SRID = 4326 },
                            IconName = "restaurant",
                            MarkerColor = "red",
                            DisplayOrder = 0
                        },
                        new Place
                        {
                            Id = Guid.NewGuid(),
                            Name = "Place 2",
                            Location = new Point(-73.9855, 40.7580) { SRID = 4326 },
                            IconName = "hotel",
                            MarkerColor = "green",
                            DisplayOrder = 1
                        }
                    }
                }
            }
        };

        // Act
        var kml = TripWayfarerKmlExporter.BuildKml(trip);

        // Assert
        Assert.Contains("<Style", kml);
        Assert.Contains("wf_restaurant_red", kml);
        Assert.Contains("wf_hotel_green", kml);
        Assert.Contains("<styleUrl>#wf_restaurant_red</styleUrl>", kml);
        Assert.Contains("<styleUrl>#wf_hotel_green</styleUrl>", kml);
    }

    [Fact]
    public void BuildKml_TripWithArea_CreatesPolygonPlacemark()
    {
        // Arrange
        var coordinates = new[]
        {
            new Coordinate(-74.0, 40.7),
            new Coordinate(-73.9, 40.7),
            new Coordinate(-73.9, 40.8),
            new Coordinate(-74.0, 40.8),
            new Coordinate(-74.0, 40.7) // Close the ring
        };
        var polygon = new Polygon(new LinearRing(coordinates)) { SRID = 4326 };

        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            Name = "Trip with Area",
            UpdatedAt = DateTime.UtcNow,
            Regions = new List<Region>
            {
                new Region
                {
                    Id = Guid.NewGuid(),
                    Name = "Region",
                    DisplayOrder = 0,
                    Areas = new List<Area>
                    {
                        new Area
                        {
                            Id = Guid.NewGuid(),
                            Name = "Central Park",
                            Geometry = polygon,
                            FillHex = "#00FF00",
                            DisplayOrder = 0
                        }
                    }
                }
            }
        };

        // Act
        var kml = TripWayfarerKmlExporter.BuildKml(trip);

        // Assert
        Assert.Contains("<Polygon>", kml);
        Assert.Contains("<outerBoundaryIs>", kml);
        Assert.Contains("<LinearRing>", kml);
        Assert.Contains("AreaId", kml);
        Assert.Contains("FillHex", kml);
        Assert.Contains("#00FF00", kml);
    }

    [Fact]
    public void BuildKml_TripWithSegment_CreatesLineStringPlacemark()
    {
        // Arrange
        var lineCoordinates = new[]
        {
            new Coordinate(-74.0060, 40.7128),
            new Coordinate(-73.9855, 40.7580),
            new Coordinate(-73.9776, 40.7614)
        };
        var lineString = new LineString(lineCoordinates) { SRID = 4326 };

        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            Name = "Trip with Segment",
            UpdatedAt = DateTime.UtcNow,
            Segments = new List<Segment>
            {
                new Segment
                {
                    Id = Guid.NewGuid(),
                    Mode = "driving",
                    RouteGeometry = lineString,
                    EstimatedDistanceKm = 5.5,
                    EstimatedDuration = TimeSpan.FromMinutes(15),
                    DisplayOrder = 0
                }
            }
        };

        // Act
        var kml = TripWayfarerKmlExporter.BuildKml(trip);

        // Assert
        Assert.Contains("<Folder>", kml);
        Assert.Contains("<name>Segments</name>", kml);
        Assert.Contains("<LineString>", kml);
        Assert.Contains("SegmentId", kml);
        Assert.Contains("Mode", kml);
        Assert.Contains("driving", kml);
        Assert.Contains("DistanceKm", kml);
        Assert.Contains("5.5", kml);
        Assert.Contains("DurationMin", kml);
    }

    [Fact]
    public void BuildKml_TripWithTags_IncludesTagsAsCommaSeparated()
    {
        // Arrange
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            Name = "Tagged Trip",
            UpdatedAt = DateTime.UtcNow,
            Tags = new List<Tag>
            {
                new Tag { Name = "Adventure", Slug = "adventure" },
                new Tag { Name = "Beach", Slug = "beach" }
            }
        };

        // Act
        var kml = TripWayfarerKmlExporter.BuildKml(trip);

        // Assert
        Assert.Contains("Tags", kml);
        // Tags are sorted by name, so "adventure,beach"
        Assert.Contains("adventure", kml);
        Assert.Contains("beach", kml);
    }

    [Fact]
    public void BuildKml_TripWithMultipleRegions_OrdersByDisplayOrder()
    {
        // Arrange
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            Name = "Multi-region Trip",
            UpdatedAt = DateTime.UtcNow,
            Regions = new List<Region>
            {
                new Region { Id = Guid.NewGuid(), Name = "Third", DisplayOrder = 2 },
                new Region { Id = Guid.NewGuid(), Name = "First", DisplayOrder = 0 },
                new Region { Id = Guid.NewGuid(), Name = "Second", DisplayOrder = 1 }
            }
        };

        // Act
        var kml = TripWayfarerKmlExporter.BuildKml(trip);

        // Assert - Check that First appears before Second which appears before Third
        var firstIndex = kml.IndexOf("<name>First</name>");
        var secondIndex = kml.IndexOf("<name>Second</name>");
        var thirdIndex = kml.IndexOf("<name>Third</name>");
        Assert.True(firstIndex < secondIndex);
        Assert.True(secondIndex < thirdIndex);
    }

    [Fact]
    public void BuildKml_PlaceWithoutLocation_SkipsPlace()
    {
        // Arrange
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            Name = "Trip with null location",
            UpdatedAt = DateTime.UtcNow,
            Regions = new List<Region>
            {
                new Region
                {
                    Id = Guid.NewGuid(),
                    Name = "Region",
                    DisplayOrder = 0,
                    Places = new List<Place>
                    {
                        new Place
                        {
                            Id = Guid.NewGuid(),
                            Name = "No Location Place",
                            Location = null, // No location
                            DisplayOrder = 0
                        }
                    }
                }
            }
        };

        // Act
        var kml = TripWayfarerKmlExporter.BuildKml(trip);

        // Assert - Place should be skipped
        Assert.DoesNotContain("<name>No Location Place</name>", kml);
    }

    [Fact]
    public void BuildKml_EmptyTrip_ReturnsValidKmlWithNoFolders()
    {
        // Arrange
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            Name = "Empty Trip",
            UpdatedAt = DateTime.UtcNow,
            Regions = new List<Region>()
        };

        // Act
        var kml = TripWayfarerKmlExporter.BuildKml(trip);

        // Assert
        Assert.Contains("<Document>", kml);
        Assert.Contains("<name>Empty Trip</name>", kml);
        Assert.DoesNotContain("<Folder>", kml);
    }

    [Fact]
    public void BuildKml_RegionWithCenter_IncludesCenterCoordinates()
    {
        // Arrange
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            Name = "Trip",
            UpdatedAt = DateTime.UtcNow,
            Regions = new List<Region>
            {
                new Region
                {
                    Id = Guid.NewGuid(),
                    Name = "Centered Region",
                    DisplayOrder = 0,
                    Center = new Point(-74.0060, 40.7128) { SRID = 4326 }
                }
            }
        };

        // Act
        var kml = TripWayfarerKmlExporter.BuildKml(trip);

        // Assert
        Assert.Contains("CenterLat", kml);
        Assert.Contains("CenterLon", kml);
        Assert.Contains("40.7128", kml);
        Assert.Contains("-74.006", kml);
    }
}
