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

    [Fact]
    public async Task CreateOrUpdate_Get_ReturnsPartialView()
    {
        var db = CreateDbContext();
        var userId = "u1";
        var regionId = Guid.NewGuid();
        var region = new Region { Id = regionId, TripId = Guid.NewGuid(), UserId = userId, Name = "Region" };
        db.Regions.Add(region);
        await db.SaveChangesAsync();
        var controller = BuildController(db, userId);

        var result = await controller.CreateOrUpdate(regionId);

        var partial = Assert.IsType<PartialViewResult>(result);
        var model = Assert.IsType<Place>(partial.Model);
        Assert.Equal(regionId, model.RegionId);
    }

    [Fact]
    public async Task CreateOrUpdate_UpdatesExistingPlace()
    {
        var db = CreateDbContext();
        var userId = "u1";
        var region = new Region { Id = Guid.NewGuid(), TripId = Guid.NewGuid(), UserId = userId, Name = "Region" };
        var existing = new Place { Id = Guid.NewGuid(), RegionId = region.Id, UserId = userId, Name = "Old", Notes = "notes" };
        db.Regions.Add(region);
        db.Places.Add(existing);
        db.Users.Add(TestDataFixtures.CreateUser(id: userId, username: "alice"));
        await db.SaveChangesAsync();

        var controller = BuildController(db, userId);
        controller.ControllerContext.HttpContext.Features.Set<IFormFeature>(new FormFeature(new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>())));

        var result = await controller.CreateOrUpdate(new Place
        {
            Id = existing.Id,
            RegionId = region.Id,
            Name = "Updated",
            Notes = "new notes",
            IconName = "star",
            MarkerColor = "blue"
        });

        var partial = Assert.IsType<PartialViewResult>(result);
        var updated = db.Places.Single(p => p.Id == existing.Id);
        Assert.Equal("Updated", updated.Name);
        Assert.Equal("new notes", updated.Notes);
        Assert.Equal("star", updated.IconName);
        Assert.Equal("blue", updated.MarkerColor);
    }

    [Fact]
    public async Task CreateOrUpdate_MovesPlaceToNewRegion()
    {
        var db = CreateDbContext();
        var userId = "u1";
        var region1 = new Region { Id = Guid.NewGuid(), TripId = Guid.NewGuid(), UserId = userId, Name = "Region1" };
        var region2 = new Region { Id = Guid.NewGuid(), TripId = Guid.NewGuid(), UserId = userId, Name = "Region2" };
        var place = new Place { Id = Guid.NewGuid(), RegionId = region1.Id, UserId = userId, Name = "Place", DisplayOrder = 0 };
        db.Regions.AddRange(region1, region2);
        db.Places.Add(place);
        db.Users.Add(TestDataFixtures.CreateUser(id: userId, username: "alice"));
        await db.SaveChangesAsync();

        var controller = BuildController(db, userId);
        controller.ControllerContext.HttpContext.Features.Set<IFormFeature>(new FormFeature(new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>())));

        var result = await controller.CreateOrUpdate(new Place
        {
            Id = place.Id,
            RegionId = region2.Id,
            Name = "Place"
        });

        var updated = db.Places.Single(p => p.Id == place.Id);
        Assert.Equal(region2.Id, updated.RegionId);
        Assert.Equal(0, updated.DisplayOrder); // first in new region
    }

    [Fact]
    public async Task CreateOrUpdate_WithRegionIdOverride_UsesOverride()
    {
        var db = CreateDbContext();
        var userId = "u1";
        var region1 = new Region { Id = Guid.NewGuid(), TripId = Guid.NewGuid(), UserId = userId, Name = "Region1" };
        var region2 = new Region { Id = Guid.NewGuid(), TripId = Guid.NewGuid(), UserId = userId, Name = "Region2" };
        var place = new Place { Id = Guid.NewGuid(), RegionId = region1.Id, UserId = userId, Name = "Place" };
        db.Regions.AddRange(region1, region2);
        db.Places.Add(place);
        db.Users.Add(TestDataFixtures.CreateUser(id: userId, username: "alice"));
        await db.SaveChangesAsync();

        var controller = BuildController(db, userId);
        controller.ControllerContext.HttpContext.Features.Set<IFormFeature>(new FormFeature(new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["RegionIdOverride"] = region2.Id.ToString()
        })));

        var result = await controller.CreateOrUpdate(new Place
        {
            Id = place.Id,
            RegionId = region1.Id, // this should be overridden
            Name = "Place"
        });

        var updated = db.Places.Single(p => p.Id == place.Id);
        Assert.Equal(region2.Id, updated.RegionId);
    }

    [Fact]
    public async Task CreateOrUpdate_UpdatesCoordinates()
    {
        var db = CreateDbContext();
        var userId = "u1";
        var region = new Region { Id = Guid.NewGuid(), TripId = Guid.NewGuid(), UserId = userId, Name = "Region" };
        var place = new Place { Id = Guid.NewGuid(), RegionId = region.Id, UserId = userId, Name = "Place" };
        db.Regions.Add(region);
        db.Places.Add(place);
        db.Users.Add(TestDataFixtures.CreateUser(id: userId, username: "alice"));
        await db.SaveChangesAsync();

        var controller = BuildController(db, userId);
        controller.ControllerContext.HttpContext.Features.Set<IFormFeature>(new FormFeature(new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["Latitude"] = "45.5",
            ["Longitude"] = "-122.6"
        })));

        var result = await controller.CreateOrUpdate(new Place
        {
            Id = place.Id,
            RegionId = region.Id,
            Name = "Place"
        });

        var updated = db.Places.Single(p => p.Id == place.Id);
        Assert.NotNull(updated.Location);
        Assert.Equal(45.5, updated.Location!.Y);
        Assert.Equal(-122.6, updated.Location!.X);
    }

    [Fact]
    public async Task CreateOrUpdate_ReturnsNotFound_WhenRegionMissing()
    {
        var db = CreateDbContext();
        var userId = "u1";
        db.Users.Add(TestDataFixtures.CreateUser(id: userId, username: "alice"));
        await db.SaveChangesAsync();

        var controller = BuildController(db, userId);
        controller.ControllerContext.HttpContext.Features.Set<IFormFeature>(new FormFeature(new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>())));

        var result = await controller.CreateOrUpdate(new Place
        {
            Id = Guid.NewGuid(),
            RegionId = Guid.NewGuid(), // non-existent region
            Name = "Place"
        });

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task CreateOrUpdate_SetsDisplayOrder_ForNewPlace()
    {
        var db = CreateDbContext();
        var userId = "u1";
        var region = new Region { Id = Guid.NewGuid(), TripId = Guid.NewGuid(), UserId = userId, Name = "Region" };
        db.Regions.Add(region);
        db.Places.AddRange(
            new Place { Id = Guid.NewGuid(), RegionId = region.Id, UserId = userId, Name = "p1", DisplayOrder = 5 },
            new Place { Id = Guid.NewGuid(), RegionId = region.Id, UserId = userId, Name = "p2", DisplayOrder = 10 }
        );
        db.Users.Add(TestDataFixtures.CreateUser(id: userId, username: "alice"));
        await db.SaveChangesAsync();

        var controller = BuildController(db, userId);
        controller.ControllerContext.HttpContext.Features.Set<IFormFeature>(new FormFeature(new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>())));

        var result = await controller.CreateOrUpdate(new Place
        {
            Id = Guid.NewGuid(),
            RegionId = region.Id,
            Name = "New"
        });

        var created = db.Places.Single(p => p.Name == "New");
        Assert.Equal(11, created.DisplayOrder); // max (10) + 1
    }

    [Fact]
    public async Task Delete_RemovesPlace_WhenOwned()
    {
        var db = CreateDbContext();
        var userId = "u1";
        var region = new Region { Id = Guid.NewGuid(), TripId = Guid.NewGuid(), UserId = userId, Name = "Region" };
        var place = new Place { Id = Guid.NewGuid(), RegionId = region.Id, UserId = userId, Name = "ToDelete" };
        db.Regions.Add(region);
        db.Places.Add(place);
        await db.SaveChangesAsync();
        var controller = BuildController(db, userId);

        var result = await controller.Delete(place.Id);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Empty(db.Places.Where(p => p.Id == place.Id));
    }

    [Fact]
    public async Task Edit_ReturnsPartialView_WhenOwned()
    {
        var db = CreateDbContext();
        var userId = "u1";
        var region = new Region { Id = Guid.NewGuid(), TripId = Guid.NewGuid(), UserId = userId, Name = "Region" };
        var place = new Place { Id = Guid.NewGuid(), RegionId = region.Id, UserId = userId, Name = "Place" };
        db.Regions.Add(region);
        db.Places.Add(place);
        await db.SaveChangesAsync();
        var controller = BuildController(db, userId);

        var result = await controller.Edit(place.Id);

        var partial = Assert.IsType<PartialViewResult>(result);
        var model = Assert.IsType<Place>(partial.Model);
        Assert.Equal(place.Id, model.Id);
    }

    [Fact]
    public async Task Reorder_IgnoresPlacesNotOwned()
    {
        var db = CreateDbContext();
        var userId = "u1";
        var otherPlace = new Place { Id = Guid.NewGuid(), RegionId = Guid.NewGuid(), UserId = "other", Name = "other", DisplayOrder = 5 };
        db.Places.Add(otherPlace);
        await db.SaveChangesAsync();
        var controller = BuildController(db, userId);

        var result = await controller.Reorder(new List<OrderDto> { new(otherPlace.Id, 99) });

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(5, db.Places.Single(p => p.Id == otherPlace.Id).DisplayOrder); // unchanged
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
