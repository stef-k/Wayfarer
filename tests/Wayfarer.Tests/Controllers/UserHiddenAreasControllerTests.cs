using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Hidden areas CRUD basics.
/// </summary>
public class UserHiddenAreasControllerTests : TestBase
{
    [Fact]
    public async Task Create_ReturnsView_WhenInvalidModel()
    {
        var controller = BuildController(CreateDbContext());
        controller.ModelState.AddModelError("Name", "required");

        var result = await controller.Create(new HiddenAreaCreateViewModel());

        Assert.IsType<ViewResult>(result);
    }

    [Fact]
    public async Task Create_Persists_WhenValid()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1", username: "alice"));
        db.SaveChanges();
        var controller = BuildController(db);

        var result = await controller.Create(new HiddenAreaCreateViewModel
        {
            Name = "Area1",
            Description = "desc",
            AreaWKT = "POLYGON((0 0,1 0,1 1,0 1,0 0))"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(HiddenAreasController.Index), redirect.ActionName);
        Assert.Equal(1, db.HiddenAreas.Count());
    }

    [Fact]
    public async Task Edit_ReturnsNotFound_WhenMissing()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.Edit(999);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_WhenMissing()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.DeleteConfirmed(123);

        Assert.IsType<RedirectToActionResult>(result);
    }

    [Fact]
    public async Task Index_ReturnsEmptyList_WhenNoAreasExist()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1", username: "alice"));
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<List<HiddenArea>>(view.Model);
        Assert.Empty(model);
    }

    [Fact]
    public async Task Index_ReturnsUserAreas_WhenAreasExist()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        var otherUser = TestDataFixtures.CreateUser(id: "u2", username: "bob");
        db.Users.AddRange(user, otherUser);
        var userArea = new HiddenArea
        {
            Id = 1,
            UserId = user.Id,
            User = user,
            Name = "My Area",
            Area = new Polygon(new LinearRing(new[] {
                new Coordinate(0, 0),
                new Coordinate(1, 0),
                new Coordinate(1, 1),
                new Coordinate(0, 1),
                new Coordinate(0, 0)
            })) { SRID = 4326 }
        };
        var otherArea = new HiddenArea
        {
            Id = 2,
            UserId = otherUser.Id,
            User = otherUser,
            Name = "Other Area",
            Area = new Polygon(new LinearRing(new[] {
                new Coordinate(0, 0),
                new Coordinate(1, 0),
                new Coordinate(1, 1),
                new Coordinate(0, 1),
                new Coordinate(0, 0)
            })) { SRID = 4326 }
        };
        db.HiddenAreas.AddRange(userArea, otherArea);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<List<HiddenArea>>(view.Model);
        Assert.Single(model);
        Assert.Equal("My Area", model[0].Name);
    }

    [Fact]
    public void Create_Get_ReturnsView()
    {
        var controller = BuildController(CreateDbContext());

        var result = controller.Create();

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<HiddenAreaCreateViewModel>(view.Model);
    }

    [Fact]
    public async Task Create_ReturnsView_WhenAreaWKTMissing()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.Create(new HiddenAreaCreateViewModel
        {
            Name = "Area",
            AreaWKT = ""
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
    }

    [Fact]
    public async Task Create_ReturnsView_OnException()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1", username: "alice"));
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Create(new HiddenAreaCreateViewModel
        {
            Name = "Area",
            AreaWKT = "INVALID WKT"
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<HiddenAreaCreateViewModel>(view.Model);
    }

    [Fact]
    public async Task Edit_Get_ReturnsView_WhenExists()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        var area = new HiddenArea
        {
            Id = 1,
            UserId = user.Id,
            User = user,
            Name = "My Area",
            Description = "Test description",
            Area = new Polygon(new LinearRing(new[] {
                new Coordinate(0, 0),
                new Coordinate(1, 0),
                new Coordinate(1, 1),
                new Coordinate(0, 1),
                new Coordinate(0, 0)
            })) { SRID = 4326 }
        };
        db.HiddenAreas.Add(area);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Edit(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<HiddenAreaEditViewModel>(view.Model);
        Assert.Equal(1, model.Id);
        Assert.Equal("My Area", model.Name);
        Assert.Equal("Test description", model.Description);
    }

    [Fact]
    public async Task Edit_Post_ReturnsView_WhenModelInvalid()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1", username: "alice"));
        await db.SaveChangesAsync();
        var controller = BuildController(db);
        controller.ModelState.AddModelError("Name", "required");

        var result = await controller.Edit(new HiddenAreaEditViewModel
        {
            Id = 1,
            Name = "",
            AreaWKT = "POLYGON((0 0,1 0,1 1,0 1,0 0))"
        });

        Assert.IsType<ViewResult>(result);
    }

    [Fact]
    public async Task Edit_Post_ReturnsNotFound_WhenAreaDoesNotExist()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1", username: "alice"));
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Edit(new HiddenAreaEditViewModel
        {
            Id = 999,
            Name = "Area",
            AreaWKT = "POLYGON((0 0,1 0,1 1,0 1,0 0))"
        });

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Edit_Post_UpdatesArea_WhenValid()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        var area = new HiddenArea
        {
            Id = 1,
            UserId = user.Id,
            User = user,
            Name = "Old Name",
            Description = "Old desc",
            Area = new Polygon(new LinearRing(new[] {
                new Coordinate(0, 0),
                new Coordinate(1, 0),
                new Coordinate(1, 1),
                new Coordinate(0, 1),
                new Coordinate(0, 0)
            })) { SRID = 4326 }
        };
        db.HiddenAreas.Add(area);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Edit(new HiddenAreaEditViewModel
        {
            Id = 1,
            Name = "New Name",
            Description = "New desc",
            AreaWKT = "POLYGON((0 0,2 0,2 2,0 2,0 0))"
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(HiddenAreasController.Index), redirect.ActionName);
        var updated = await db.HiddenAreas.FindAsync(1);
        Assert.Equal("New Name", updated!.Name);
        Assert.Equal("New desc", updated.Description);
    }

    [Fact]
    public async Task Edit_Post_ReturnsView_OnException()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        var area = new HiddenArea
        {
            Id = 1,
            UserId = user.Id,
            User = user,
            Name = "Area",
            Area = new Polygon(new LinearRing(new[] {
                new Coordinate(0, 0),
                new Coordinate(1, 0),
                new Coordinate(1, 1),
                new Coordinate(0, 1),
                new Coordinate(0, 0)
            })) { SRID = 4326 }
        };
        db.HiddenAreas.Add(area);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Edit(new HiddenAreaEditViewModel
        {
            Id = 1,
            Name = "Area",
            AreaWKT = "INVALID WKT"
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<HiddenAreaEditViewModel>(view.Model);
    }

    [Fact]
    public async Task Delete_Get_ReturnsView_WhenExists()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        var area = new HiddenArea
        {
            Id = 1,
            UserId = user.Id,
            User = user,
            Name = "My Area",
            Area = new Polygon(new LinearRing(new[] {
                new Coordinate(0, 0),
                new Coordinate(1, 0),
                new Coordinate(1, 1),
                new Coordinate(0, 1),
                new Coordinate(0, 0)
            })) { SRID = 4326 }
        };
        db.HiddenAreas.Add(area);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Delete(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<HiddenArea>(view.Model);
        Assert.Equal("My Area", model.Name);
    }

    [Fact]
    public async Task Delete_Get_ReturnsNotFound_WhenIdNull()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.Delete(null);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Delete_Get_ReturnsNotFound_WhenAreaNotFound()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1", username: "alice"));
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Delete(999);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DeleteConfirmed_RemovesArea_WhenExists()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        var area = new HiddenArea
        {
            Id = 1,
            UserId = user.Id,
            User = user,
            Name = "My Area",
            Area = new Polygon(new LinearRing(new[] {
                new Coordinate(0, 0),
                new Coordinate(1, 0),
                new Coordinate(1, 1),
                new Coordinate(0, 1),
                new Coordinate(0, 0)
            })) { SRID = 4326 }
        };
        db.HiddenAreas.Add(area);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.DeleteConfirmed(1);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(HiddenAreasController.Index), redirect.ActionName);
        Assert.Empty(db.HiddenAreas);
    }

    [Fact]
    public async Task DeleteConfirmed_RedirectsToIndex_WhenNotFound()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1", username: "alice"));
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.DeleteConfirmed(999);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(HiddenAreasController.Index), redirect.ActionName);
    }

    private HiddenAreasController BuildController(ApplicationDbContext db)
    {
        var controller = new HiddenAreasController(
            NullLogger<BaseController>.Instance,
            db);
        var httpContext = BuildHttpContextWithUser("alice");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());
        return controller;
    }
}
