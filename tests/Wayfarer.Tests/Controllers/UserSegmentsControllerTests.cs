using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
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
    public async Task CreateOrUpdate_Get_ReturnsNotFound_ForMissingTrip()
    {
        var db = CreateDbContext();
        var controller = new SegmentsController(NullLogger<SegmentsController>.Instance, db);
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser("u1") };

        var result = await controller.CreateOrUpdate(null, Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

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

    [Fact]
    public async Task Delete_ReturnsNotFound_WhenSegmentMissing()
    {
        var db = CreateDbContext();
        var controller = new SegmentsController(NullLogger<SegmentsController>.Instance, db);
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser("u1") };

        var result = await controller.Delete(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Delete_RemovesSegment_WhenOwnedByUser()
    {
        var db = CreateDbContext();
        var (user, seg) = await SeedSegment(db);
        var controller = new SegmentsController(NullLogger<SegmentsController>.Instance, db);
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(user.Id) };

        var result = await controller.Delete(seg.Id);

        var json = Assert.IsType<JsonResult>(result);
        Assert.Empty(db.Segments);
        Assert.Contains(seg.Id.ToString(), json.Value!.ToString());
    }

    [Fact]
    public async Task Reorder_UpdatesDisplayOrder_ForOwnedSegments()
    {
        var db = CreateDbContext();
        var (user, seg) = await SeedSegment(db);
        var seg2 = new Segment
        {
            Id = Guid.NewGuid(),
            TripId = seg.TripId,
            UserId = user.Id,
            FromPlaceId = seg.FromPlaceId,
            ToPlaceId = seg.ToPlaceId,
            Mode = "walk",
            EstimatedDistanceKm = 1,
            DisplayOrder = 0
        };
        db.Segments.Add(seg2);
        await db.SaveChangesAsync();

        var controller = new SegmentsController(NullLogger<SegmentsController>.Instance, db);
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(user.Id) };

        var result = await controller.Reorder(new List<OrderDto>
        {
            new(seg.Id, 2),
            new(seg2.Id, 1)
        });

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(2, db.Segments.First(s => s.Id == seg.Id).DisplayOrder);
        Assert.Equal(1, db.Segments.First(s => s.Id == seg2.Id).DisplayOrder);
    }

    [Fact]
    public async Task GetItemPartial_ReturnsNotFound_WhenMissing()
    {
        var db = CreateDbContext();
        var controller = new SegmentsController(NullLogger<SegmentsController>.Instance, db);
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser("u1") };

        var result = await controller.GetItemPartial(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetItemPartial_ReturnsPartial_WhenExists()
    {
        var db = CreateDbContext();
        var (user, seg) = await SeedSegment(db);
        var controller = new SegmentsController(NullLogger<SegmentsController>.Instance, db);
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(user.Id) };

        var result = await controller.GetItemPartial(seg.Id);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Equal("~/Areas/User/Views/Trip/Partials/_SegmentItemPartial.cshtml", partial.ViewName);
    }

    [Fact]
    public async Task GetSegments_ReturnsOnlyUserSegments_ForTrip()
    {
        var db = CreateDbContext();
        var (user, seg) = await SeedSegment(db);
        var otherUser = TestDataFixtures.CreateUser();
        db.Users.Add(otherUser);
        db.Segments.Add(new Segment
        {
            Id = Guid.NewGuid(),
            TripId = seg.TripId,
            UserId = otherUser.Id,
            FromPlaceId = seg.FromPlaceId,
            ToPlaceId = seg.ToPlaceId,
            Mode = "car",
            EstimatedDistanceKm = 2
        });
        await db.SaveChangesAsync();

        var controller = new SegmentsController(NullLogger<SegmentsController>.Instance, db);
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(user.Id) };

        var result = await controller.GetSegments(seg.TripId);

        var json = Assert.IsType<JsonResult>(result);
        var list = Assert.IsAssignableFrom<IEnumerable<SegmentDto>>(json.Value);
        Assert.Single(list);
        Assert.Equal(seg.Id, list.First().Id);
    }

    private async Task<(ApplicationUser user, Segment segment)> SeedSegment(ApplicationDbContext db)
    {
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, UserId = user.Id, Name = "R1" };
        var from = new Place { Id = Guid.NewGuid(), UserId = user.Id, RegionId = region.Id, Name = "From", Location = new Point(0, 0) { SRID = 4326 } };
        var to = new Place { Id = Guid.NewGuid(), UserId = user.Id, RegionId = region.Id, Name = "To", Location = new Point(1, 1) { SRID = 4326 } };
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.Places.AddRange(from, to);
        var seg = new Segment
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = user.Id,
            FromPlaceId = from.Id,
            ToPlaceId = to.Id,
            Mode = "walk",
            EstimatedDistanceKm = 1,
            DisplayOrder = 0
        };
        db.Segments.Add(seg);
        await db.SaveChangesAsync();
        return (user, seg);
    }
}
