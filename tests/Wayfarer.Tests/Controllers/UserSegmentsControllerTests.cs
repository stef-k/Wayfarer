using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// User Segments controller essentials.
/// </summary>
public class UserSegmentsControllerTests : TestBase
{
    [Fact]
    public async Task Create_ReturnsUnauthorized_WhenNoUser()
    {
        var controller = new SegmentsController(NullLogger<SegmentsController>.Instance, CreateDbContext());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await controller.Create(new SegmentCreateRequestDto());

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Create_AddsSegment_WhenValid()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1"));
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "u1", Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, UserId = "u1", Name = "R1" };
        db.Trips.Add(trip);
        db.Regions.Add(region);
        await db.SaveChangesAsync();
        var controller = new SegmentsController(NullLogger<SegmentsController>.Instance, db);
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser("u1") };

        var result = await controller.Create(new SegmentCreateRequestDto
        {
            Name = "Seg1",
            TripId = trip.Id,
            StartPlaceId = Guid.NewGuid(),
            EndPlaceId = Guid.NewGuid(),
            GeoJson = "{\"type\":\"LineString\",\"coordinates\":[[0,0],[1,1]]}"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SegmentsController.Index), redirect.ActionName);
        Assert.Single(db.Segments);
    }
}
