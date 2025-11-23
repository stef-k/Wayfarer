using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
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
        var controller = BuildController(CreateDbContext());
        controller.ModelState.AddModelError("Name", "required");

        var result = await controller.Create(new ActivityType());

        Assert.IsType<ViewResult>(result);
    }

    [Fact]
    public async Task Create_Persists_WhenValid()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);

        var result = await controller.Create(new ActivityType { Name = "Walk" });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(ActivityTypeController.Index), redirect.ActionName);
        Assert.Single(db.ActivityTypes);
    }

    private ActivityTypeController BuildController(ApplicationDbContext db)
    {
        var controller = new ActivityTypeController(NullLogger<ActivityTypeController>.Instance, db);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }
}
