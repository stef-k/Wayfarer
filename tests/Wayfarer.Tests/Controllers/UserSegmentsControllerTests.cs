using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// User Segments controller essentials.
/// </summary>
public class UserSegmentsControllerTests : TestBase
{
    [Fact]
    public async Task CreateOrUpdate_FailsValidation_WhenNoUser()
    {
        var db = CreateDbContext();
        var controller = new SegmentsController(NullLogger<SegmentsController>.Instance, db);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Features.Set<IFormFeature>(new FormFeature(new FormCollection(new Dictionary<string, StringValues>())));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            await controller.CreateOrUpdate(new Segment
            {
                Mode = "walk",
                UserId = null
            });
        });
    }

    [Fact]
    public async Task CreateOrUpdate_AddsSegment_WhenValid()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, UserId = user.Id, Name = "R1" };
        var from = new Place { Id = Guid.NewGuid(), UserId = user.Id, RegionId = region.Id, Name = "From", Location = new Point(0, 0) { SRID = 4326 } };
        var to = new Place { Id = Guid.NewGuid(), UserId = user.Id, RegionId = region.Id, Name = "To", Location = new Point(1, 1) { SRID = 4326 } };
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.Places.AddRange(from, to);
        await db.SaveChangesAsync();

        var controller = new SegmentsController(NullLogger<SegmentsController>.Instance, db);
        var httpContext = BuildHttpContextWithUser(user.Id);
        httpContext.Features.Set<IFormFeature>(new FormFeature(new FormCollection(new Dictionary<string, StringValues>
        {
            { "RouteJson", new StringValues("[[0,0],[1,1]]") }
        })));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var model = new Segment
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = user.Id,
            FromPlaceId = from.Id,
            ToPlaceId = to.Id,
            Mode = "walk",
            EstimatedDistanceKm = 1
        };

        var result = await controller.CreateOrUpdate(model);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Equal("~/Areas/User/Views/Trip/Partials/_SegmentItemPartial.cshtml", partial.ViewName);
        Assert.Single(db.Segments);
    }
}
