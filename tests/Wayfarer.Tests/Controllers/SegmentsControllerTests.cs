using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
/// Segment CRUD + reorder flows for User area.
/// </summary>
public class SegmentsControllerTests : TestBase
{
    [Fact]
    public async Task Delete_ReturnsNotFound_ForOtherUser()
    {
        var db = CreateDbContext();
        db.Segments.Add(new Segment { Id = Guid.NewGuid(), TripId = Guid.NewGuid(), UserId = "other", Mode = "walk" });
        await db.SaveChangesAsync();
        var controller = BuildController(db, "current");

        var result = await controller.Delete(db.Segments.First().Id);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Reorder_SetsDisplayOrder()
    {
        var db = CreateDbContext();
        var userId = "u1";
        var seg1 = new Segment { Id = Guid.NewGuid(), TripId = Guid.NewGuid(), UserId = userId, DisplayOrder = 0, Mode = "walk" };
        var seg2 = new Segment { Id = Guid.NewGuid(), TripId = seg1.TripId, UserId = userId, DisplayOrder = 1, Mode = "walk" };
        db.Segments.AddRange(seg1, seg2);
        await db.SaveChangesAsync();
        var controller = BuildController(db, userId);
        var items = new List<OrderDto>
        {
            new(seg1.Id, 3),
            new(seg2.Id, 2)
        };

        var result = await controller.Reorder(items);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(3, db.Segments.Single(s => s.Id == seg1.Id).DisplayOrder);
        Assert.Equal(2, db.Segments.Single(s => s.Id == seg2.Id).DisplayOrder);
    }

    [Fact]
    public async Task CreateOrUpdate_SavesRouteGeometry_AndReturnsPartial()
    {
        var db = CreateDbContext();
        var userId = "u1";
        var trip = new Trip { Id = Guid.NewGuid(), UserId = userId, Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, UserId = userId, Name = "Region" };
        var from = new Place { Id = Guid.NewGuid(), UserId = userId, RegionId = region.Id, Name = "From", Location = new Point(1, 1) { SRID = 4326 } };
        var to = new Place { Id = Guid.NewGuid(), UserId = userId, RegionId = region.Id, Name = "To", Location = new Point(2, 2) { SRID = 4326 } };
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.Places.AddRange(from, to);
        await db.SaveChangesAsync();

        var controller = BuildController(db, userId);
        var formFields = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["RouteJson"] = "[[1,2],[3,4]]",
            ["EstimatedDurationMinutes"] = ""
        };
        controller.ControllerContext.HttpContext.Features.Set<IFormFeature>(new FormFeature(new FormCollection(formFields)));

        var model = new Segment
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            FromPlaceId = from.Id,
            ToPlaceId = to.Id,
            Mode = "walk"
        };

        var result = await controller.CreateOrUpdate(model);

        var view = Assert.IsType<PartialViewResult>(result);
        var segment = Assert.IsType<Segment>(view.Model);
        Assert.NotNull(segment.RouteGeometry);
        Assert.Equal(userId, segment.UserId);
        Assert.Equal(from.Id, segment.FromPlaceId);
        Assert.Equal(to.Id, segment.ToPlaceId);
    }

    private SegmentsController BuildController(ApplicationDbContext db, string userId)
    {
        var controller = new SegmentsController(NullLogger<SegmentsController>.Instance, db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = CreateHttpContextWithUser(userId)
        };
        return controller;
    }
}
