using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>
/// Tests for <see cref="TripDtoMapper"/>.
/// </summary>
public class TripDtoMapperTests : TestBase
{
    /// <summary>Tied authored orders use identity; null remains before explicit Place order, without storage edits.</summary>
    [Fact]
    public void ToApiDto_TiedCollectionsAreStableAcrossLoadedOrder()
    {
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var explicitOrder = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var region = new Region { Id = first, DisplayOrder = 7,
            Places = [new Place { Id = second }, new Place { Id = explicitOrder, DisplayOrder = -1 }, new Place { Id = first }],
            Areas = [new Area { Id = second, DisplayOrder = 4 }, new Area { Id = first, DisplayOrder = 4 }] };
        var trip = new Trip { Regions = [new Region { Id = second, DisplayOrder = 7 }, region],
            Tags = [new Tag { Id = second, Name = "B", Slug = "b" }, new Tag { Id = first, Name = "A", Slug = "a" }] };
        ApiTripSegmentDto[] segments = [new() { Id = second, DisplayOrder = 3 }, new() { Id = first, DisplayOrder = 3 }];
        var before = trip.ToApiDto(segments);
        Assert.Equal(new[] { first, second }, before.Regions!.Select(item => item.Id));
        Assert.Equal(new[] { first, second }, before.Segments!.Select(item => item.Id));
        Assert.Equal(new[] { first, second }, before.Tags!.Select(item => item.Id));
        Assert.Equal(new[] { first, second, explicitOrder }, before.Regions.First().Places!.Select(item => item.Id));
        Assert.Equal(new[] { first, second }, before.Regions.First().Areas!.Select(item => item.Id));
        trip.Regions = trip.Regions.Reverse().ToList();
        trip.Tags = trip.Tags.Reverse().ToList();
        region.Places = region.Places.Reverse().ToList();
        region.Areas = region.Areas.Reverse().ToList();
        var after = trip.ToApiDto(segments.Reverse().ToArray());
        Assert.Equal(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(before), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(after));
        Assert.All(region.Places.Where(item => item.Id != explicitOrder), item => Assert.Null(item.DisplayOrder));
        Assert.Equal(-1, region.Places.Single(item => item.Id == explicitOrder).DisplayOrder);
        Assert.All(trip.Regions, item => Assert.Equal(7, item.DisplayOrder));
        Assert.All(region.Areas, item => Assert.Equal(4, item.DisplayOrder));
    }

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

        var dto = trip.ToApiDto(trip.Segments
            .Select(segment => new ApiTripSegmentDto { Id = segment.Id, DisplayOrder = segment.DisplayOrder })
            .ToArray());

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
    public void ToApiDto_Area_WritesGeoJsonWithoutMutatingSrid()
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
        Assert.Equal(3857, area.Geometry!.SRID);
    }

    [Fact]
    public void ToApiDto_Area_ReturnsEmptyDtoOnNull()
    {
        var dto = TripDtoMapper.ToApiDto((Area)null!);

        Assert.NotNull(dto);
        Assert.Equal(Guid.Empty, dto.Id);
    }

    /// <summary>Strict full reads reject missing/unloaded state and invalid numbers that permissive mutation mapping retains.</summary>
    [Theory]
    [InlineData("unloaded")]
    [InlineData("null-collection")]
    [InlineData("null-child")]
    [InlineData("foreign")]
    [InlineData("point")]
    [InlineData("center")]
    [InlineData("area")]
    public async Task FullRead_RejectsIncompleteRequiredContent(string failure)
    {
        using var db = CreateDbContext();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "owner", Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, UserId = trip.UserId, Name = "Region" };
        var place = new Place { Id = Guid.NewGuid(), Region = region, UserId = trip.UserId, Name = "Place" };
        db.Places.Add(place);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        trip = await db.Trips.Include(item => item.Tags).Include(item => item.Segments)
            .Include(item => item.Regions).ThenInclude(item => item.Places)
            .Include(item => item.Regions).ThenInclude(item => item.Areas).SingleAsync();
        region = trip.Regions.Single();
        place = region.Places.Single();
        Assert.Single(trip.ToFullApiDto(db).Regions!);
        // Keep malformed unit inputs intact instead of invoking EF's write-side relationship fixup.
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        switch (failure)
        {
            case "unloaded": db.Entry(region).Collection(item => item.Areas).IsLoaded = false; break;
            case "null-collection": region.Places = null!; break;
            case "null-child": region.Places.Add(null!); break;
            case "foreign": place.RegionId = Guid.NewGuid(); break;
            case "point": place.Location = new Point(double.NaN, 37) { SRID = 4326 }; break;
            case "center": trip.CenterLon = double.PositiveInfinity; break;
            case "area": region.Areas.Add(new Area { RegionId = region.Id, Name = "Missing geometry" }); break;
        }
        Assert.Throws<InvalidDataException>(() => trip.ToFullApiDto(db));
    }

}
