using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Admin.Controllers;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Admin activity type controller basics.
/// </summary>
public class AdminActivityTypeControllerTests : TestBase
{
    [Fact]
    public async Task Create_ReturnsView_WhenModelInvalid()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "admin"));
        db.SaveChanges();
        var controller = BuildController(db);
        controller.ModelState.AddModelError("Name", "required");

        var result = await controller.Create(new ActivityType { Name = "Test" });

        Assert.IsType<ViewResult>(result);
    }

    [Fact]
    public async Task Create_Persists_WhenValid()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "admin"));
        db.SaveChanges();
        var controller = BuildController(db);

        var result = await controller.Create(new ActivityType { Name = "Walk", Description = "desc" });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ActivityTypeController.Index), redirect.ActionName);
        Assert.Single(db.ActivityTypes);
    }

    [Fact]
    public async Task Create_Get_ReturnsView()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.Create();

        Assert.IsType<ViewResult>(result);
    }

    [Fact]
    public async Task Index_ReturnsAllActivities_WithoutSearch()
    {
        var db = CreateDbContext();
        db.ActivityTypes.AddRange(
            new ActivityType { Id = 1, Name = "Walk", Description = "Walking" },
            new ActivityType { Id = 2, Name = "Run", Description = "Running" },
            new ActivityType { Id = 3, Name = "Bike", Description = "Cycling" });
        db.SaveChanges();
        var controller = BuildController(db);

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<List<ActivityType>>(view.Model);
        Assert.Equal(3, model.Count);
    }

    [Fact]
    public async Task Index_FiltersActivities_WithSearch()
    {
        var db = CreateDbContext();
        db.ActivityTypes.AddRange(
            new ActivityType { Id = 1, Name = "Walk", Description = "Walking" },
            new ActivityType { Id = 2, Name = "Run", Description = "Running" },
            new ActivityType { Id = 3, Name = "Bike", Description = "Cycling" });
        db.SaveChanges();
        var controller = BuildController(db);

        var result = await controller.Index(search: "Run");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<List<ActivityType>>(view.Model);
        Assert.Single(model);
        Assert.Equal("Run", model.First().Name);
    }

    [Fact]
    public async Task Index_PaginatesResults()
    {
        var db = CreateDbContext();
        for (int i = 1; i <= 20; i++)
        {
            db.ActivityTypes.Add(new ActivityType { Id = i, Name = $"Activity{i}", Description = $"Desc{i}" });
        }
        db.SaveChanges();
        var controller = BuildController(db);

        var result = await controller.Index(page: 1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<List<ActivityType>>(view.Model);
        Assert.Equal(15, model.Count);
        Assert.Equal(2, controller.ViewBag.TotalPages);
    }

    [Fact]
    public async Task Edit_Get_ReturnsView_WhenExists()
    {
        var db = CreateDbContext();
        db.ActivityTypes.Add(new ActivityType { Id = 1, Name = "Walk", Description = "Walking" });
        db.SaveChanges();
        var controller = BuildController(db);

        var result = await controller.Edit(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ActivityType>(view.Model);
        Assert.Equal("Walk", model.Name);
    }

    [Fact]
    public async Task Edit_Get_ReturnsNotFound_WhenMissing()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.Edit(999);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Edit_Get_ReturnsNotFound_WhenIdNull()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.Edit(null);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Edit_Post_ReturnsNotFound_WhenIdMismatch()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.Edit(1, new ActivityType { Id = 2, Name = "Test" });

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Edit_Post_ReturnsView_WhenModelInvalid()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "admin"));
        db.ActivityTypes.Add(new ActivityType { Id = 1, Name = "Walk", Description = "Walking" });
        db.SaveChanges();
        var controller = BuildController(db);
        controller.ModelState.AddModelError("Name", "required");

        var result = await controller.Edit(1, new ActivityType { Id = 1, Name = "", Description = "desc" });

        Assert.IsType<ViewResult>(result);
    }

    [Fact]
    public async Task Delete_Get_ReturnsView_WhenExists()
    {
        var db = CreateDbContext();
        db.ActivityTypes.Add(new ActivityType { Id = 1, Name = "Walk", Description = "Walking" });
        db.SaveChanges();
        var controller = BuildController(db);

        var result = await controller.Delete(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ActivityType>(view.Model);
        Assert.Equal("Walk", model.Name);
    }

    [Fact]
    public async Task Delete_Get_ReturnsNotFound_WhenMissing()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.Delete(999);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Delete_Get_ReturnsNotFound_WhenIdNull()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.Delete(null);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DeleteConfirmed_RemovesActivity_WhenExists()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "admin"));
        db.ActivityTypes.Add(new ActivityType { Id = 1, Name = "Walk", Description = "Walking" });
        db.SaveChanges();
        var controller = BuildController(db);

        var result = await controller.DeleteConfirmed(1);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ActivityTypeController.Index), redirect.ActionName);
        Assert.Empty(db.ActivityTypes);
    }

    [Fact]
    public async Task DeleteConfirmed_RedirectsToIndex_WhenActivityNotFound()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "admin"));
        db.SaveChanges();
        var controller = BuildController(db);

        var result = await controller.DeleteConfirmed(999);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ActivityTypeController.Index), redirect.ActionName);
    }

    private ActivityTypeController BuildController(ApplicationDbContext db)
    {
        var controller = new ActivityTypeController(NullLogger<ActivityTypeController>.Instance, db);
        var httpContext = BuildHttpContextWithUser("admin", "Admin");
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());
        return controller;
    }

    private static ApplicationDbContext CreateSharedContext(string dbName)
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new ApplicationDbContext(options, new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider());
    }
}
