using Microsoft.Extensions.Logging;
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

    /// <summary>Generic MVC errors never copy exception payloads to audit storage or logs.</summary>
    [Fact]
    public void HandleError_PreservesFriendlyAlertAndBoundedAudit()
    {
        using var logs = new TestLogProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var db = CreateDbContext();
        var controller = new FakeBaseController(db, factory.CreateLogger<BaseController>());
        var context = BuildHttpContextWithUser("user1");
        context.TraceIdentifier = "request-663";
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, Mock.Of<ITempDataProvider>());
        controller.HandleError(new InvalidOperationException("secret-exception-663"));
        var audit = Assert.Single(db.AuditLogs);
        Assert.Equal("An error occurred during an action.: InvalidOperationException; request request-663", audit.Details);
        Assert.Equal("An unexpected error occurred. Please try again later.", controller.TempData["AlertMessage"]);
        Assert.All(logs.Entries, entry => {
            Assert.Null(entry.Exception);
            Assert.DoesNotContain("secret-exception-663", entry.Message + string.Join(",", entry.Fields.Values));
        });
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
        /// <summary>Exposes validation solely for existing helper tests.</summary>
        public new bool ValidateModelState() => base.ValidateModelState();

        /// <summary>Exposes role checking solely for existing helper tests.</summary>
        public new bool EnsureUserIsAuthorized(string role) => base.EnsureUserIsAuthorized(role);

        /// <summary>Exposes alert redirects solely for existing helper tests.</summary>
        public new IActionResult RedirectWithAlert(string action, string controller, string message,
            string alertType = "success", object? routeValues = null, string? area = null) =>
            base.RedirectWithAlert(action, controller, message, alertType, routeValues, area);

        /// <summary>Exposes generic error handling for privacy regression coverage.</summary>
        public new void HandleError(Exception exception) => base.HandleError(exception);

        public FakeBaseController(ApplicationDbContext db, ILogger<BaseController>? logger = null)
            : base(logger ?? NullLogger<BaseController>.Instance, db)
        {
        }
    }
}
