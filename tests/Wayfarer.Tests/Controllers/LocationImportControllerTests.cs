using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// User location import controller flows.
/// </summary>
public class LocationImportControllerTests : TestBase
{
    [Fact]
    public async Task Index_ListsOnlyCurrentUserImports()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        var other = TestDataFixtures.CreateUser(id: "u2", username: "bob");
        db.Users.AddRange(user, other);
        db.LocationImports.AddRange(
            NewImport(1, user.Id, ImportStatus.Stopped),
            NewImport(2, other.Id, ImportStatus.Stopped));
        await db.SaveChangesAsync();
        var controller = BuildController(db, user, Mock.Of<IScheduler>());

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var imports = Assert.IsAssignableFrom<IEnumerable<LocationImport>>(view.Model!);
        Assert.Single(imports);
    }

    [Fact]
    public async Task StartImport_SchedulesJobWhenOwnedAndNotInProgress()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        db.LocationImports.Add(NewImport(10, user.Id, ImportStatus.Stopped));
        await db.SaveChangesAsync();
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(s => s.CheckExists(It.IsAny<JobKey>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var controller = BuildController(db, user, scheduler.Object);

        var result = await controller.StartImport(10);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        scheduler.Verify(s => s.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(ImportStatus.InProgress, db.LocationImports.Single(i => i.Id == 10).Status);
    }

    [Fact]
    public async Task StartImport_DeniesOtherUser()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        db.LocationImports.Add(NewImport(10, "other", ImportStatus.Stopped));
        await db.SaveChangesAsync();
        var controller = BuildController(db, user, Mock.Of<IScheduler>());

        var result = await controller.StartImport(10);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal(ImportStatus.Stopped, db.LocationImports.Single(i => i.Id == 10).Status);
    }

    [Fact]
    public async Task StartImport_NoopWhenAlreadyInProgress()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        db.LocationImports.Add(NewImport(10, user.Id, ImportStatus.InProgress));
        await db.SaveChangesAsync();
        var controller = BuildController(db, user, Mock.Of<IScheduler>());

        var result = await controller.StartImport(10);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal(ImportStatus.InProgress, db.LocationImports.Single(i => i.Id == 10).Status);
    }

    [Fact]
    public async Task StopImport_TransitionsToStopped()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        db.LocationImports.Add(NewImport(20, user.Id, ImportStatus.InProgress));
        await db.SaveChangesAsync();
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(s => s.CheckExists(It.IsAny<JobKey>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        scheduler.Setup(s => s.GetJobDetail(It.IsAny<JobKey>(), It.IsAny<CancellationToken>())).ReturnsAsync(Mock.Of<IJobDetail>());
        var controller = BuildController(db, user, scheduler.Object);

        var result = await controller.StopImport(20);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        scheduler.Verify(s => s.Interrupt(It.IsAny<JobKey>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(ImportStatus.Stopped, db.LocationImports.Single(i => i.Id == 20).Status);
    }

    [Fact]
    public async Task StopImport_RejectsNotInProgress()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        db.LocationImports.Add(NewImport(20, user.Id, ImportStatus.Stopped));
        await db.SaveChangesAsync();
        var controller = BuildController(db, user, Mock.Of<IScheduler>());

        var result = await controller.StopImport(20);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal(ImportStatus.Stopped, db.LocationImports.Single(i => i.Id == 20).Status);
    }

    [Fact]
    public async Task Delete_RemovesOwnedImport()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        db.LocationImports.Add(NewImport(30, user.Id, ImportStatus.Completed));
        await db.SaveChangesAsync();
        var controller = BuildController(db, user, Mock.Of<IScheduler>());

        var result = await controller.Delete(30);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Empty(db.LocationImports);
    }

    [Fact]
    public async Task Delete_RejectsOtherUser()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        db.LocationImports.Add(NewImport(30, "other", ImportStatus.Completed));
        await db.SaveChangesAsync();
        var controller = BuildController(db, user, Mock.Of<IScheduler>());

        var result = await controller.Delete(30);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Single(db.LocationImports);
    }

    private static LocationImportController BuildController(ApplicationDbContext db, ApplicationUser user, IScheduler scheduler)
    {
        var controller = new LocationImportController(
            db,
            NullLogger<LocationImportController>.Instance,
            Mock.Of<IWebHostEnvironment>(),
            scheduler);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id),
                new Claim(ClaimTypes.Name, user.UserName!)
            }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
        return controller;
    }

    private static LocationImport NewImport(int id, string userId, ImportStatus status)
    {
        return new LocationImport
        {
            Id = id,
            UserId = userId,
            FileType = LocationImportFileType.GoogleTimeline,
            TotalRecords = 0,
            FilePath = $"path-{id}",
            LastProcessedIndex = 0,
            Status = status,
            CreatedAt = DateTime.UtcNow
        };
    }
}
