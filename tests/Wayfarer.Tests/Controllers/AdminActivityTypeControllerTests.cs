using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

    private ActivityTypeController BuildController(ApplicationDbContext db)
    {
        var controller = new ActivityTypeController(NullLogger<ActivityTypeController>.Instance, db);
        var httpContext = BuildHttpContextWithUser("admin", "Admin");
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());
        return controller;
    }
}
