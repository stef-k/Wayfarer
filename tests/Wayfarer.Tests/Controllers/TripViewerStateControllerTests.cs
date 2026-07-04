using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.TripViewer;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Covers public and embed preview Trip Viewer state endpoints.
/// </summary>
public sealed class TripViewerStateControllerTests : TestBase
{
    [Fact]
    public async Task ViewNextState_ReturnsNotFound_WhenPrivate()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "owner"));
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "owner", Name = "Private", IsPublic = false };
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.ViewNextState(trip.Id);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ViewNextState_ReturnsPublicMode_ForAuthenticatedOwnerPublicRoute()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(owner);
        var trip = TestDataFixtures.CreateTrip(owner, "Public", isPublic: true);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        var controller = BuildController(db);
        ConfigureControllerWithUser(controller, owner.Id);

        var result = await controller.ViewNextState(trip.Id);

        var state = StateFrom(result);
        Assert.Equal("public", state.ViewerMode);
        Assert.True(state.Permissions.IsOwner);
        Assert.False(state.Permissions.CanViewPrivateState);
        Assert.Null(state.Trip.PrivateUrl);
    }

    [Fact]
    public async Task ViewNextState_ReturnsEmbedModeAndRedactsOwnerActions()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(owner);
        var trip = TestDataFixtures.CreateTrip(owner, "Public", isPublic: true);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        var controller = BuildController(db);
        ConfigureControllerWithUser(controller, owner.Id);

        var result = await controller.ViewNextState(trip.Id, embed: true);

        var state = StateFrom(result);
        Assert.Equal("embed", state.ViewerMode);
        Assert.False(state.Permissions.IsOwner);
        Assert.False(state.Actions.Edit.Allowed);
        Assert.True(state.Actions.OpenCanonical.Allowed);
        Assert.Null(state.Trip.PrivateUrl);
    }

    private static TripViewerStateDto StateFrom(IActionResult result)
    {
        var json = Assert.IsType<JsonResult>(result);
        return Assert.IsType<TripViewerStateDto>(json.Value);
    }

    private static TripViewerController BuildController(ApplicationDbContext db)
    {
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(s => s.GetSettings()).Returns(new ApplicationSettings());
        var imageProxyService = new ImageProxyService(
            new HttpClient(),
            Mock.Of<IProxiedImageCacheService>(),
            settings.Object,
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<ImageProxyService>.Instance);

        var controller = new TripViewerController(
            NullLogger<TripViewerController>.Instance,
            db,
            new HttpClient(),
            Mock.Of<ITripThumbnailService>(),
            Mock.Of<ITripTagService>(),
            imageProxyService,
            settings.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }
}
