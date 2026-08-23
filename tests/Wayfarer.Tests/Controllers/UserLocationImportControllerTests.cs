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
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationEnrichment;
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

    [Fact]
    public void UploadChoicesExcludeGenericGeoJsonButRetainWayfarerGeoJson()
    {
        var controller = BuildController(CreateDbContext(), "u1");

        controller.Upload();

        var choices = Assert.IsAssignableFrom<IEnumerable<Microsoft.AspNetCore.Mvc.Rendering.SelectListItem>>(
            controller.ViewData["FileTypes"]);
        Assert.DoesNotContain(choices, item => item.Value == nameof(LocationImportFileType.GeoJson));
        Assert.Contains(choices, item => item.Value == nameof(LocationImportFileType.WayfarerGeoJson));
    }

    [Fact]
    public async Task UploadRejectsCraftedGenericGeoJsonBeforePersistingImport()
    {
        var db = CreateDbContext();
        var controller = BuildController(db, "u1");
        var file = new Mock<IFormFile>();
        file.SetupGet(item => item.FileName).Returns("crafted.geojson");
        file.SetupGet(item => item.Length).Returns(2);

        var result = await controller.Upload(new LocationImportUploadViewModel
        {
            File = file.Object,
            FileType = LocationImportFileType.GeoJson
        });

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(db.LocationImports);
    }

    [Fact]
    public async Task RetryDeferredUsesOnlyAuthenticatedClaimIdentity()
    {
        var handoff = new Mock<IImportEnrichmentHandoff>();
        handoff.Setup(item => item.RetryDeferredAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(EnrichmentCommandResult.Success("scheduled"));
        var controller = BuildController(CreateDbContext(), "u1", handoff.Object);

        var result = await controller.RetryDeferredEnrichment();

        Assert.IsType<RedirectToActionResult>(result);
        handoff.Verify(item => item.RetryDeferredAsync("u1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PauseWhileIdleReturnsBoundedConflict()
    {
        var db = CreateDbContext();
        db.Add(LocationEnrichmentWorkflow.Create("u1", DateTime.UtcNow));
        await db.SaveChangesAsync();
        var controller = BuildController(db, "u1");

        var result = await controller.PauseEnrichment();

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("invalid-state", conflict.Value);
    }

    [Fact]
    public async Task ResumeWithoutCurrentProviderAuthorityReturnsBoundedConflict()
    {
        var db = CreateDbContext();
        var workflow = LocationEnrichmentWorkflow.Create("u1", DateTime.UtcNow);
        workflow.Start(DateTime.UtcNow);
        workflow.Pause(DateTime.UtcNow);
        db.Add(workflow);
        await db.SaveChangesAsync();
        var controller = BuildController(db, "u1");

        var result = await controller.ResumeEnrichment();

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("authority-unavailable", conflict.Value);
    }

    private LocationImportController BuildController(ApplicationDbContext db, string userId,
        IImportEnrichmentHandoff? handoff = null, IWorkflowScheduleProjection? projection = null)
    {
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(Path.GetTempPath());
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(s => s.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), default)).ReturnsAsync(DateTimeOffset.UtcNow);

        var controller = new LocationImportController(db, NullLogger<LocationImportController>.Instance,
            env.Object, scheduler.Object, handoff, projection);
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(userId) };
        return controller;
    }
}
