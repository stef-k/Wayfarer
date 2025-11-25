using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Covers BaseController helper/validation methods in isolation.
/// </summary>
public class BaseControllerHelperTests : TestBase
{
    [Fact]
    public void ValidateModelState_ReturnsFalse_AndSetsAlert()
    {
        var db = CreateDbContext();
        var controller = BuildController(db, "user1");
        controller.ModelState.AddModelError("Name", "Required");

        var isValid = controller.ValidateModelState();

        Assert.False(isValid);
        Assert.Equal("danger", controller.TempData["AlertType"]);
        Assert.NotNull(controller.TempData["AlertMessage"]);
    }

    [Fact]
    public void EnsureUserIsAuthorized_ReturnsFalse_WhenRoleMissing()
    {
        var db = CreateDbContext();
        var controller = BuildController(db, "user1", role: "User");

        var authorized = controller.EnsureUserIsAuthorized("Admin");

        Assert.False(authorized);
        Assert.Equal("danger", controller.TempData["AlertType"]);
    }

    [Fact]
    public void EnsureUserIsAuthorized_ReturnsTrue_WhenRolePresent()
    {
        var db = CreateDbContext();
        var controller = BuildController(db, "manager", role: "Manager");

        var authorized = controller.EnsureUserIsAuthorized("Manager");

        Assert.True(authorized);
    }

    [Fact]
    public void RedirectWithAlert_AddsAlertAndArea()
    {
        var db = CreateDbContext();
        var controller = BuildController(db, "user1");

        var result = controller.RedirectWithAlert("Index", "Users", "done", "info", new { id = 5 }, "Admin");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Users", redirect.ControllerName);
        Assert.Equal("Admin", redirect.RouteValues!["area"]);
        Assert.Equal("info", controller.TempData["AlertType"]);
        Assert.Equal("done", controller.TempData["AlertMessage"]);
    }

    private static FakeBaseController BuildController(ApplicationDbContext db, string userId, string role = "User")
    {
        var controller = new FakeBaseController(db);
        var httpContext = TestBase.BuildHttpContextWithUser(userId, role);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());
        return controller;
    }

    private class FakeBaseController : BaseController
    {
        public FakeBaseController(ApplicationDbContext db)
            : base(NullLogger<BaseController>.Instance, db)
        {
        }
    }
}
