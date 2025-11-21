using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Regions CRUD/reorder behavior (User area).
/// </summary>
public class RegionsControllerTests : TestBase
{
    [Fact]
    public async Task Delete_ReturnsNotFound_WhenRegionNotOwned()
    {
        var db = CreateDbContext();
        var region = new Region { Id = Guid.NewGuid(), TripId = Guid.NewGuid(), UserId = "other", Name = "r1" };
        db.Regions.Add(region);
        await db.SaveChangesAsync();
        var controller = BuildController(db, "current");

        var result = await controller.Delete(region.Id);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Reorder_UpdatesDisplayOrder()
    {
        var db = CreateDbContext();
        var userId = "user-1";
        var r1 = new Region { Id = Guid.NewGuid(), TripId = Guid.NewGuid(), UserId = userId, Name = "r1", DisplayOrder = 0 };
        var r2 = new Region { Id = Guid.NewGuid(), TripId = r1.TripId, UserId = userId, Name = "r2", DisplayOrder = 1 };
        db.Regions.AddRange(r1, r2);
        await db.SaveChangesAsync();
        var controller = BuildController(db, userId);
        var items = new List<OrderDto>
        {
            new(r1.Id, 2),
            new(r2.Id, 1)
        };

        var result = await controller.Reorder(items);

        Assert.IsType<NoContentResult>(result);
        var updated = db.Regions.ToDictionary(x => x.Id, x => x.DisplayOrder);
        Assert.Equal(2, updated[r1.Id]);
        Assert.Equal(1, updated[r2.Id]);
    }

    [Fact]
    public async Task CreateOrUpdate_CreatesNewRegion_WithNextDisplayOrder()
    {
        var db = CreateDbContext();
        var tripId = Guid.NewGuid();
        db.Regions.Add(new Region { Id = Guid.NewGuid(), TripId = tripId, UserId = "u1", Name = "existing", DisplayOrder = 3 });
        await db.SaveChangesAsync();
        var controller = BuildController(db, "u1");
        var model = new Region
        {
            Id = Guid.NewGuid(),
            TripId = tripId,
            Name = "new region"
        };
        controller.ControllerContext.HttpContext.Request.ContentType = "application/x-www-form-urlencoded";
        controller.ControllerContext.HttpContext.Features.Set<IFormFeature>(new FormFeature(new FormCollection(new Dictionary<string, StringValues>())));

        var result = await controller.CreateOrUpdate(model);

        var view = Assert.IsType<PartialViewResult>(result);
        var region = Assert.IsType<Region>(view.Model);
        Assert.Equal(4, region.DisplayOrder);
        Assert.Equal("new region", region.Name);
    }

    private RegionsController BuildController(ApplicationDbContext db, string userId)
    {
        var controller = new RegionsController(NullLogger<RegionsController>.Instance, db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = CreateHttpContextWithUser(userId)
        };
        return controller;
    }
}
