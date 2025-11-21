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
/// Area create/edit/delete/reorder flows for User area.
/// </summary>
public class AreasControllerTests : TestBase
{
    [Fact]
    public async Task CreateOrUpdate_ReturnsPartial_WithParsedGeometry()
    {
        var db = CreateDbContext();
        var userId = "u1";
        var region = new Region { Id = Guid.NewGuid(), TripId = Guid.NewGuid(), UserId = userId, Name = "Region" };
        db.Regions.Add(region);
        await db.SaveChangesAsync();
        var controller = BuildController(db, userId);

        var form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["Geometry"] = """{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,1],[0,0]]]}"""
        });
        controller.ControllerContext.HttpContext.Features.Set<IFormFeature>(new FormFeature(form));

        var model = new Area
        {
            Id = Guid.NewGuid(),
            RegionId = region.Id,
            Name = "Test Area",
            FillHex = "#ff6600"
        };

        var result = await controller.CreateOrUpdate(model);

        var partial = Assert.IsType<PartialViewResult>(result);
        var updatedRegion = Assert.IsType<Region>(partial.Model);
        Assert.Single(updatedRegion.Areas);
        Assert.Equal("Test Area", updatedRegion.Areas.First().Name);
    }

    [Fact]
    public async Task CreateOrUpdate_ReturnsPartialWithErrors_WhenGeometryMissing()
    {
        var db = CreateDbContext();
        var userId = "u1";
        var region = new Region { Id = Guid.NewGuid(), TripId = Guid.NewGuid(), UserId = userId, Name = "Region" };
        db.Regions.Add(region);
        await db.SaveChangesAsync();
        var controller = BuildController(db, userId);
        controller.ControllerContext.HttpContext.Features.Set<IFormFeature>(new FormFeature(new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["Geometry"] = ""
        })));

        var model = new Area
        {
            Id = Guid.NewGuid(),
            RegionId = region.Id,
            Name = "NoGeom",
            FillHex = "#ff6600"
        };

        var result = await controller.CreateOrUpdate(model);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Equal("~/Areas/User/Views/Trip/Partials/_AreaFormPartial.cshtml", partial.ViewName);
        Assert.False(controller.ModelState.IsValid);
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_WhenNotOwned()
    {
        var db = CreateDbContext();
        db.Areas.Add(new Area
        {
            Id = Guid.NewGuid(),
            RegionId = Guid.NewGuid(),
            Geometry = new Polygon(new LinearRing(new[]
            {
                new Coordinate(0,0), new Coordinate(1,0), new Coordinate(1,1), new Coordinate(0,1), new Coordinate(0,0)
            })),
            FillHex = "#ff6600"
        });
        await db.SaveChangesAsync();
        var controller = BuildController(db, "current");

        var result = await controller.Delete(db.Areas.First().Id);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Reorder_UpdatesDisplayOrder()
    {
        var db = CreateDbContext();
        var userId = "u1";
        var region = new Region { Id = Guid.NewGuid(), TripId = Guid.NewGuid(), UserId = userId, Name = "Region" };
        var a1 = new Area { Id = Guid.NewGuid(), RegionId = region.Id, Geometry = CreatePoly(), Name = "a1", FillHex = "#ff6600" };
        var a2 = new Area { Id = Guid.NewGuid(), RegionId = region.Id, Geometry = CreatePoly(), Name = "a2", FillHex = "#ff6600" };
        db.Regions.Add(region);
        db.Areas.AddRange(a1, a2);
        await db.SaveChangesAsync();
        var controller = BuildController(db, userId);
        var items = new List<OrderDto>
        {
            new(a1.Id, 2),
            new(a2.Id, 1)
        };

        var result = await controller.Reorder(items);

        Assert.IsType<NoContentResult>(result);
        var orders = db.Areas.ToDictionary(a => a.Id, a => a.DisplayOrder);
        Assert.Equal(2, orders[a1.Id]);
        Assert.Equal(1, orders[a2.Id]);
    }

    private static Polygon CreatePoly()
    {
        return new Polygon(new LinearRing(new[]
        {
            new Coordinate(0,0),
            new Coordinate(1,0),
            new Coordinate(1,1),
            new Coordinate(0,1),
            new Coordinate(0,0)
        }))
        { SRID = 4326 };
    }

    private AreasController BuildController(ApplicationDbContext db, string userId)
    {
        var controller = new AreasController(NullLogger<AreasController>.Instance, db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = CreateHttpContextWithUser(userId)
        };
        return controller;
    }
}
