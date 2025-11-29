using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Admin.Controllers;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Basic coverage for AuditLogsController.
/// </summary>
public class AdminAuditLogsControllerTests : TestBase
{
    [Fact]
    public async Task Index_ReturnsPagedResults()
    {
        var db = CreateDbContext();
        db.AuditLogs.AddRange(
            new AuditLog { Action = "A", Details = "d1", Timestamp = DateTime.UtcNow, UserId = "u1" },
            new AuditLog { Action = "B", Details = "d2", Timestamp = DateTime.UtcNow.AddMinutes(-1), UserId = "u1" });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Index(search: null!, page: 1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<IEnumerable<AuditLog>>(view.Model);
        Assert.Equal(2, model.Count());
    }

    private static AuditLogsController BuildController(ApplicationDbContext db)
    {
        var controller = new AuditLogsController(
            NullLogger<AuditLogsController>.Instance,
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
        controller.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
        return controller;
    }
}
