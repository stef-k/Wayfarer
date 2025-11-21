using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Admin.Controllers;
using Wayfarer.Models;
using Wayfarer.Util;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Admin ActivityTypeController simple guardrails.
/// </summary>
public class AdminActivityTypeControllerTests : TestBase
{
    [Fact]
    public async Task Create_InvalidModel_ReturnsView()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);
        controller.ModelState.AddModelError("Name", "required");

        var result = await controller.Create(new ActivityType { Name = "", Description = "desc" });

        var view = Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
    }

    [Fact]
    public async Task Edit_IdMismatch_ReturnsNotFound()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);

        var result = await controller.Edit(2, new ActivityType { Id = 1, Name = "Hike" });

        Assert.IsType<NotFoundResult>(result);
    }

    private static ActivityTypeController BuildController(ApplicationDbContext db)
    {
        var controller = new ActivityTypeController(
            NullLogger<ActivityTypeController>.Instance,
            db);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "admin"),
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, ApplicationRoles.Admin)
            }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, new TestTempDataProvider());
        return controller;
    }
}

internal sealed class TestTempDataProvider : ITempDataProvider
{
    public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
    public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
}
