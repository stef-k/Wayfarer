using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>
/// Tests for <see cref="TripDtoMapper"/>.
/// </summary>
public class TripDtoMapperTests
{
    [Fact]
    public void ToApiDto_Trip_MapsCollectionsAndOrder()
    {
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            Name = "Summer",
            Notes = "Notes",
            IsPublic = true,
            CenterLat = 1.1,
            CenterLon = 2.2,
            Zoom = 7,
            UpdatedAt = DateTime.UtcNow,
            Regions = new List<Region>
            {
                new Region { Id = Guid.NewGuid(), Name = "B", DisplayOrder = 2 },
                new Region { Id = Guid.NewGuid(), Name = "A", DisplayOrder = 1 }
            },
            Segments = new List<Segment>
            {
                new Segment { Id = Guid.NewGuid(), DisplayOrder = 2 },
                new Segment { Id = Guid.NewGuid(), DisplayOrder = 1 }
            },
            Tags = new List<Tag>
            {
                new Tag { Id = Guid.NewGuid(), Name = "Tag", Slug = "tag" }
            }
        };

        var dto = trip.ToApiDto();

        Assert.Equal(trip.Id, dto.Id);
        Assert.Equal(trip.Name, dto.Name);
        Assert.Equal(new[] { 1, 2 }, dto.Regions!.Select(r => r.DisplayOrder));
        Assert.Equal(new[] { 1, 2 }, dto.Segments!.Select(s => s.DisplayOrder));
        Assert.Equal("Tag", dto.Tags!.Single().Name);
    }

    [Fact]
    public void ToApiDto_Region_MapsPlacesAndAreas()
    {
        var region = new Region
        {
            Id = Guid.NewGuid(),
            Name = "R1",
            DisplayOrder = 3,
            Center = new Point(10, 20),
            Places = new List<Place>
            {
                new Place { Id = Guid.NewGuid(), Name = "Place", DisplayOrder = 1, Location = new Point(1, 2) }
            },
            Areas = new List<Area>
            {
                new Area
                {
                    Id = Guid.NewGuid(),
                    Name = "Area",
                    DisplayOrder = 1,
                    Geometry = new Polygon(new LinearRing(new[]
                    {
                        new Coordinate(0,0),
                        new Coordinate(1,0),
                        new Coordinate(1,1),
                        new Coordinate(0,1),
                        new Coordinate(0,0)
                    }))
                }
            }
        };

        var dto = region.ToApiDto();

        Assert.Equal(new[] { 10d, 20d }, dto.Center);
        Assert.Equal("Place", dto.Places!.Single().Name);
        Assert.Equal("Area", dto.Areas!.Single().Name);
        Assert.NotNull(dto.Areas!.Single().GeometryGeoJson);
    }

    [Fact]
    public void ToApiDto_Place_MapsBasics()
    {
        var place = new Place
        {
            Id = Guid.NewGuid(),
            Name = "P1",
            Notes = "N",
            Address = "Addr",
            IconName = "pin",
            MarkerColor = "#fff",
            DisplayOrder = 4,
            Location = new Point(3, 4)
        };

        var dto = place.ToApiDto();

        Assert.Equal(place.Id, dto.Id);
        Assert.Equal(new[] { 3d, 4d }, dto.Location);
    }

    [Fact]
    public void ToApiDto_Segment_HandlesMissingGeometryGracefully()
    {
        var segment = new Segment
        {
            Id = Guid.NewGuid(),
            Mode = "car",
            Notes = "N",
            DisplayOrder = 2,
            EstimatedDistanceKm = 12.5,
            EstimatedDuration = TimeSpan.FromMinutes(30),
            FromPlaceId = Guid.NewGuid(),
            ToPlaceId = Guid.NewGuid()
        };

        var dto = segment.ToApiDto();

        Assert.Equal(segment.Id, dto.Id);
        Assert.Equal("car", dto.Mode);
        Assert.Null(dto.RouteJson);
        Assert.Equal(30, dto.EstimatedDurationMinutes);
    }

    [Fact]
    public void ToApiDto_Segment_WritesGeoJsonWithSridCorrection()
    {
        var line = new LineString(new[] { new Coordinate(0, 0), new Coordinate(1, 1) }) { SRID = 3857 };
        var segment = new Segment { Id = Guid.NewGuid(), RouteGeometry = line };

        var dto = segment.ToApiDto();

        Assert.NotNull(dto.RouteJson);
        Assert.Equal(4326, segment.RouteGeometry!.SRID);
    }

    [Fact]
    public void ToApiDto_Area_WritesGeoJsonWithSridCorrection()
    {
        var polygon = new Polygon(new LinearRing(new[]
        {
            new Coordinate(0,0),
            new Coordinate(1,0),
            new Coordinate(1,1),
            new Coordinate(0,1),
            new Coordinate(0,0)
        }))
        { SRID = 3857 };

        var area = new Area { Id = Guid.NewGuid(), Geometry = polygon };

        var dto = area.ToApiDto();

        Assert.NotNull(dto.GeometryGeoJson);
        Assert.Equal(4326, area.Geometry!.SRID);
    }

    [Fact]
    public void ToApiDto_Area_ReturnsEmptyDtoOnNull()
    {
        var dto = TripDtoMapper.ToApiDto((Area)null!);

        Assert.NotNull(dto);
        Assert.Equal(Guid.Empty, dto.Id);
    }

    [Fact]
    public void ToApiDto_Segment_ReturnsEmptyDtoOnNull()
    {
        var dto = TripDtoMapper.ToApiDto((Segment)null!);

        Assert.NotNull(dto);
        Assert.Equal(Guid.Empty, dto.Id);
    }
}
