using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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

        Assert.IsType<NotFoundResult>(result);
    }

    private HiddenAreasController BuildController(ApplicationDbContext db)
    {
        var controller = new HiddenAreasController(
            NullLogger<BaseController>.Instance,
            db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }
}
