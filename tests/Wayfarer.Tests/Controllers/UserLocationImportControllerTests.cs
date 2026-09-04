using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Models.ViewModels;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationProviders;
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
    public async Task IndexProjectsAuthorityForAuthenticatedClaimIdentity()
    {
        var db = CreateDbContext();
        var projector = new Mock<ILocationEnrichmentPresentationProjector>();
        var expected = LocationEnrichmentPresentation.Build(null,
            new("geoapify", "Geoapify", false, "Provider verification is required.", true,
                7, 2500, "credits", "rolling 24 hours", null),
            new(0, 2, 1, 0, DateTime.UtcNow.AddHours(1)));
        projector.Setup(item => item.ProjectAsync("u1", It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = BuildController(db, "u1", presentation: projector.Object);

        await controller.Index();

        Assert.Same(expected, controller.ViewData["EnrichmentPresentation"]);
        projector.Verify(item => item.ProjectAsync("u1", It.IsAny<CancellationToken>()), Times.Once);
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
    public void UploadGetReturnsUnselectedEnrichmentOptInModel()
    {
        var controller = BuildController(CreateDbContext(), "u1");

        var view = Assert.IsType<ViewResult>(controller.Upload());

        var model = Assert.IsType<LocationImportUploadViewModel>(view.Model);
        Assert.False(model.EnrichmentRequested);
    }

    [Fact]
    public async Task UploadValidationRerenderPreservesEnrichmentOptIn()
    {
        var controller = BuildController(CreateDbContext(), "u1");
        controller.ModelState.AddModelError(nameof(LocationImportUploadViewModel.File), "required");
        var model = new LocationImportUploadViewModel { EnrichmentRequested = true };

        var view = Assert.IsType<ViewResult>(await controller.Upload(model));

        Assert.Same(model, view.Model);
        Assert.True(Assert.IsType<LocationImportUploadViewModel>(view.Model).EnrichmentRequested);
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
            .ReturnsAsync(EnrichmentCommandResult.Success("scheduled", 2));
        var controller = BuildController(CreateDbContext(), "u1", handoff.Object);

        var result = await controller.RetryDeferredEnrichment();

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Retry scheduled for 2 deferred locations.", controller.TempData["AlertMessage"]);
        handoff.Verify(item => item.RetryDeferredAsync("u1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RetryDeferredReportsWhenNoRowsRemainEligible()
    {
        var handoff = new Mock<IImportEnrichmentHandoff>();
        handoff.Setup(item => item.RetryDeferredAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(EnrichmentCommandResult.Satisfied("nothing-to-retry"));
        var controller = BuildController(CreateDbContext(), "u1", handoff.Object);

        var result = await controller.RetryDeferredEnrichment();

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("No deferred locations are currently eligible for retry.",
            controller.TempData["AlertMessage"]);
    }

    [Fact]
    public async Task RepairIncompleteUsesAuthenticatedIdentityAndCustomAlertFeedback()
    {
        var handoff = new Mock<IImportEnrichmentHandoff>();
        handoff.Setup(item => item.RepairIncompleteAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(EnrichmentCommandResult.Success("repair-scheduled", 2));
        var controller = BuildController(CreateDbContext(), "u1", handoff.Object);

        var result = await controller.RepairIncompleteAddresses();

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Repair scheduled for 2 incomplete locations.", controller.TempData["AlertMessage"]);
        Assert.Equal("success", controller.TempData["AlertType"]);
        handoff.Verify(item => item.RepairIncompleteAsync("u1", It.IsAny<CancellationToken>()), Times.Once);
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
        IImportEnrichmentHandoff? handoff = null, IWorkflowScheduleProjection? projection = null,
        ILocationEnrichmentPresentationProjector? presentation = null)
    {
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(Path.GetTempPath());
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(s => s.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), default)).ReturnsAsync(DateTimeOffset.UtcNow);
        projection ??= Mock.Of<IWorkflowScheduleProjection>();
        var inspection = new Mock<IPersonalProviderStatusReader>();
        inspection.Setup(item => item.InspectPersistentGeocodingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalProviderInspection(PersonalProviderAdmissionCategory.NoProviderSelected,
                null, false, false, null, null, null));
        var progress = new Mock<ILocationEnrichmentProgressQuery>();
        handoff ??= new ImportEnrichmentHandoff(db, projection, inspection.Object, progress.Object);

        var defaultPresentation = new Mock<ILocationEnrichmentPresentationProjector>();
        defaultPresentation.Setup(item => item.ProjectAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(LocationEnrichmentPresentation.Build(null,
                new(null, "Not selected", false, "No geocoding provider is selected.", false,
                    0, 0, "credits", "No active usage window", null),
                new(0, 0, 0, 0, null)));
        var controller = new LocationImportController(db, NullLogger<LocationImportController>.Instance,
            env.Object, scheduler.Object, presentation ?? defaultPresentation.Object, handoff, projection,
            contextFactory: new CloningFactory(db));
        controller.ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(userId) };
        controller.TempData = new TempDataDictionary(controller.HttpContext, Mock.Of<ITempDataProvider>());
        return controller;
    }

    private sealed class CloningFactory(ApplicationDbContext source) : IDbContextFactory<ApplicationDbContext>
    {
        private readonly DbContextOptions<ApplicationDbContext> options =
            Assert.IsType<DbContextOptions<ApplicationDbContext>>(source.GetService<IDbContextOptions>());
        private readonly IServiceProvider services = new ServiceCollection().BuildServiceProvider();
        public ApplicationDbContext CreateDbContext() => new(options, services);
    }
}
