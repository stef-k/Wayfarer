using Microsoft.AspNetCore.Mvc;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Exercises independently supplied legacy note writers with one shared adversarial fixture.</summary>
public partial class ApiTripsControllerTests
{
    [Theory]
    [InlineData("trip")]
    [InlineData("region-create")]
    [InlineData("region-update")]
    [InlineData("place-create")]
    [InlineData("place-update")]
    [InlineData("area")]
    [InlineData("segment")]
    public async Task LegacyWriters_PersistCanonicalNotes(string writer)
    {
        const string input = "<p onclick='bad()'>safe</p><script>bad()</script>";
        const string expected = "<p>safe</p>";
        using var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, UserId = user.Id, Name = "Region" };
        var place = new Place { Id = Guid.NewGuid(), Region = region, UserId = user.Id, Name = "Place", Location = new Point(1, 1) { SRID = 4326 } };
        var area = new Area { Id = Guid.NewGuid(), Region = region, Name = "Area", Geometry = new Polygon(new LinearRing([new(0, 0), new(1, 0), new(1, 1), new(0, 0)])) { SRID = 4326 } };
        var segment = new Segment { Id = Guid.NewGuid(), Trip = trip, UserId = user.Id, Mode = "walk", FromPlace = place, ToPlace = place };
        db.AddRange(trip, region, place, area, segment);
        await db.SaveChangesAsync();
        var controller = BuildController(db, token: "tok");
        IActionResult result = writer switch
        {
            "trip" => await controller.UpdateTrip(trip.Id, new TripUpdateRequestDto { Notes = input }),
            "region-create" => await controller.CreateRegion(trip.Id, new RegionCreateRequestDto { Name = "Created", Notes = input }),
            "region-update" => await controller.UpdateRegion(region.Id, new RegionUpdateRequestDto { Notes = input }),
            "place-create" => await controller.CreatePlace(trip.Id, new PlaceCreateRequestDto { RegionId = region.Id, Name = "Created", Notes = input }),
            "place-update" => await controller.UpdatePlace(place.Id, new PlaceUpdateRequestDto { Notes = input }),
            "area" => await controller.UpdateArea(area.Id, new AreaUpdateRequestDto { Notes = input }),
            _ => await controller.UpdateSegmentNotes(segment.Id, new SegmentUpdateRequestDto { Notes = input })
        };
        Assert.IsType<OkObjectResult>(result);
        var stored = writer switch
        {
            "trip" => trip.Notes,
            "region-create" => db.Regions.Single(item => item.Name == "Created").Notes,
            "region-update" => region.Notes,
            "place-create" => db.Places.Single(item => item.Name == "Created").Notes,
            "place-update" => db.Places.Single(item => item.Id == place.Id).Notes,
            "area" => area.Notes,
            _ => segment.Notes
        };
        Assert.Equal(expected, stored);
    }
}
