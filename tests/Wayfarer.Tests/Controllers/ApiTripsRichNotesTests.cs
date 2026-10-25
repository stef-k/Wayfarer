using Microsoft.AspNetCore.Mvc;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Retains unconditional writer bypass rejection across scalar Notes payloads.</summary>
public partial class ApiTripsControllerTests
{
    /// <summary>Unconditional writers never normalize, echo or persist submitted fields.</summary>
    [Theory]
    [InlineData("trip")]
    [InlineData("region-update")]
    [InlineData("place-update")]
    [InlineData("area")]
    [InlineData("segment")]
    public async Task UnconditionalWriters_RejectWithoutMutation(string writer)
    {
        const string input = "<p onclick='bad()'>safe</p><script>bad()</script>";
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
            "region-update" => await controller.UpdateRegion(region.Id, new RegionUpdateRequestDto { Notes = input }),
            "place-update" => await controller.UpdatePlace(place.Id, new PlaceUpdateRequestDto { Notes = input }),
            "area" => await controller.UpdateArea(area.Id, new AreaUpdateRequestDto { Notes = input }),
            _ => await controller.UpdateSegmentNotes(segment.Id, new SegmentUpdateRequestDto { Notes = input })
        };
        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Null(trip.Notes);
        Assert.Null(region.Notes);
        Assert.Null(place.Notes);
        Assert.Null(area.Notes);
        Assert.Null(segment.Notes);
        Assert.False(db.ChangeTracker.HasChanges());
    }
}
