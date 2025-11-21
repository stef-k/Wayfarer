using System.Linq;
using System.Net.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Wayfarer.Areas.Api.Controllers;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Places CRUD/reorder behavior (User area).
/// </summary>
public class PlacesControllerTests : TestBase
{
    [Fact]
    public async Task Delete_ReturnsNotFound_ForOtherUser()
    {
        var db = CreateDbContext();
        db.Places.Add(new Place { Id = Guid.NewGuid(), RegionId = Guid.NewGuid(), UserId = "other", Name = "place" });
        await db.SaveChangesAsync();
        var controller = BuildController(db, "current");

        var result = await controller.Delete(db.Places.First().Id);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Reorder_UpdatesDisplayOrder()
    {
        var db = CreateDbContext();
        var userId = "u1";
        var region = new Region { Id = Guid.NewGuid(), TripId = Guid.NewGuid(), UserId = userId, Name = "Region" };
        var p1 = new Place { Id = Guid.NewGuid(), RegionId = region.Id, UserId = userId, Name = "p1" };
        var p2 = new Place { Id = Guid.NewGuid(), RegionId = region.Id, UserId = userId, Name = "p2" };
        db.Regions.Add(region);
        db.Places.AddRange(p1, p2);
        await db.SaveChangesAsync();
        var controller = BuildController(db, userId);
        var items = new List<OrderDto>
        {
            new(p1.Id, 3),
            new(p2.Id, 1)
        };

        var result = await controller.Reorder(items);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(3, db.Places.Single(x => x.Id == p1.Id).DisplayOrder);
        Assert.Equal(1, db.Places.Single(x => x.Id == p2.Id).DisplayOrder);
    }

    [Fact]
    public async Task CreateOrUpdate_CreatesPlace_WithCoordinates()
    {
        var db = CreateDbContext();
        var userId = "u1";
        var tripId = Guid.NewGuid();
        var region = new Region { Id = Guid.NewGuid(), TripId = tripId, UserId = userId, Name = "Region" };
        db.Regions.Add(region);
        db.Places.Add(new Place { Id = Guid.NewGuid(), RegionId = region.Id, UserId = userId, Name = "existing", DisplayOrder = 1 });
        db.Users.Add(TestDataFixtures.CreateUser(id: userId, username: "alice"));
        await db.SaveChangesAsync();

        var controller = BuildController(db, userId);
        controller.ControllerContext.HttpContext.Features.Set<IFormFeature>(new FormFeature(new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["Latitude"] = "10.5",
            ["Longitude"] = "20.25"
        })));

        var model = new Place
        {
            Id = Guid.NewGuid(),
            RegionId = region.Id,
            Name = "New place",
            MarkerColor = "red"
        };

        var result = await controller.CreateOrUpdate(model);

        var partial = Assert.IsType<PartialViewResult>(result);
        var updatedRegion = Assert.IsType<Region>(partial.Model);
        var created = updatedRegion.Places!.Single(p => p.Name == "New place");
        Assert.NotNull(created.Location);
        Assert.Equal(2, created.DisplayOrder);
    }

    [Fact]
    public async Task Edit_ReturnsNotFound_WhenNotOwned()
    {
        var db = CreateDbContext();
        db.Places.Add(new Place { Id = Guid.NewGuid(), RegionId = Guid.NewGuid(), UserId = "other", Name = "place" });
        await db.SaveChangesAsync();
        var controller = BuildController(db, "current");

        var result = await controller.Edit(db.Places.First().Id);

        Assert.IsType<NotFoundResult>(result);
    }

    private PlacesController BuildController(ApplicationDbContext db, string userId)
    {
        var controller = new PlacesController(
            NullLogger<PlacesController>.Instance,
            db,
            new ReverseGeocodingService(new HttpClient(), NullLogger<BaseApiController>.Instance));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = CreateHttpContextWithUser(userId)
        };
        return controller;
    }
}
