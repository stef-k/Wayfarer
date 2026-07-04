using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Create-flow redirect coverage for canonical Trip Editor entry points.
/// </summary>
public class TripControllerCreateRedirectTests : TestBase
{
    [Fact]
    public async Task Create_Post_RedirectsToCanonicalEdit_WhenSaveEditRequested()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "creator");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db, user.Id);
        var tripId = Guid.NewGuid();
        var model = new Trip
        {
            Id = tripId,
            Name = "New Trip",
            IsPublic = false,
            Notes = string.Empty
        };

        var result = await controller.Create(model, "save-edit");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(TripController.Edit), redirect.ActionName);
        Assert.Equal("Trip", redirect.ControllerName);
        Assert.Equal("User", redirect.RouteValues?["area"]);
        Assert.Equal(tripId, redirect.RouteValues?["id"]);
    }

    private static TripController BuildController(ApplicationDbContext db, string userId)
    {
        var httpContext = BuildHttpContextWithUser(userId);
        var controller = new TripController(
            NullLogger<TripController>.Instance,
            db,
            Mock.Of<ITripMapThumbnailGenerator>(),
            Mock.Of<ITripTagService>(),
            Mock.Of<ICacheWarmupScheduler>(),
            SettingsService());

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());
        return controller;
    }

    /// <summary>
    /// Provides default application settings for controller construction.
    /// </summary>
    private static IApplicationSettingsService SettingsService()
    {
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(s => s.GetSettings()).Returns(new ApplicationSettings());
        return settings.Object;
    }
}
