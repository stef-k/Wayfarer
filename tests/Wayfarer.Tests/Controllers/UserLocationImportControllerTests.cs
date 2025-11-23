using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Models.ViewModels;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Location import UI controller.
/// </summary>
public class UserLocationImportControllerTests : TestBase
{
    [Fact]
    public async Task Index_FiltersByUser()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1"));
        db.LocationImports.Add(new LocationImport { Id = 1, UserId = "u1", FilePath = "f", FileType = LocationImportFileType.GeoJson, TotalRecords = 0, LastProcessedIndex = 0, Status = ImportStatus.Stopped });
        db.LocationImports.Add(new LocationImport { Id = 2, UserId = "other", FilePath = "x", FileType = LocationImportFileType.GeoJson, TotalRecords = 0, LastProcessedIndex = 0, Status = ImportStatus.Stopped });
        db.SaveChanges();
        var controller = BuildController(db, "u1");

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<IEnumerable<LocationImport>>(view.Model);
        Assert.Single(model);
    }

    [Fact]
    public async Task Upload_ReturnsView_WhenMissingFile()
    {
        var controller = BuildController(CreateDbContext(), "u1");

        var result = await controller.Upload(new LocationImportUploadViewModel
        {
            File = null!,
            FileType = LocationImportFileType.GeoJson
        });

        Assert.IsType<ViewResult>(result);
    }

    private LocationImportController BuildController(ApplicationDbContext db, string userId)
    {
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(Path.GetTempPath());
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(s => s.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), default)).ReturnsAsync(DateTimeOffset.UtcNow);

        var controller = new LocationImportController(db, NullLogger<LocationImportController>.Instance, env.Object, scheduler.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(userId) };
        return controller;
    }
}
