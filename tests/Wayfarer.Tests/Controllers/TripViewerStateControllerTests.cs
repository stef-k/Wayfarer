using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.TripViewer;
using Wayfarer.Models.ViewModels;
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

    [Fact]
    public async Task ViewNextState_PreservesPublicDetailFactsWhileEmbedKeepsOwnerDataRedacted()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var trip = TestDataFixtures.CreateTrip(owner, "Public", isPublic: true);
        var region = TestDataFixtures.CreateRegion(trip, "Region", displayOrder: 1);
        var area = new Area
        {
            Id = Guid.NewGuid(),
            RegionId = region.Id,
            Region = region,
            Name = "Area",
            FillHex = "#00ff00",
            DisplayOrder = 1,
            Geometry = new Polygon(new LinearRing(new[]
            {
                new Coordinate(0, 0), new Coordinate(1, 0), new Coordinate(1, 1), new Coordinate(0, 0)
            })) { SRID = 4326 }
        };
        var segment = new Segment { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = owner.Id, EstimatedDistanceKm = 3.5, EstimatedDuration = TimeSpan.FromMinutes(50), DisplayOrder = 1 };
        region.Areas = new List<Area> { area };
        trip.Regions = new List<Region> { region };
        trip.Segments = new List<Segment> { segment };
        db.Users.Add(owner);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var publicState = StateFrom(await controller.ViewNextState(trip.Id));
        var embedState = StateFrom(await controller.ViewNextState(trip.Id, embed: true));

        Assert.Equal("#00ff00", publicState.AreasById[area.Id].FillHex);
        Assert.Equal(3.5, publicState.SegmentsById[segment.Id].EstimatedDistanceKm);
        Assert.Equal(50, publicState.SegmentsById[segment.Id].EstimatedDurationMinutes);
        Assert.Equal("#00ff00", embedState.AreasById[area.Id].FillHex);
        Assert.False(embedState.Actions.Edit.Allowed);
        Assert.Null(embedState.Trip.PrivateUrl);
    }

    [Fact]
    public async Task ViewNext_Get_ReturnsPublicViewerShell_ForPublicTrip()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(owner);
        var trip = TestDataFixtures.CreateTrip(owner, "Public", isPublic: true);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.ViewNext(trip.Id);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("~/Views/Trip/ViewNext.cshtml", view.ViewName);
        var model = Assert.IsType<TripViewerShellViewModel>(view.Model);
        Assert.Equal("public", model.ViewerMode);
        Assert.Equal($"/Public/TripsNext/{trip.Id}/state", model.ViewerStateEndpoint);
        Assert.Equal($"/Public/TripsNext/{trip.Id}", model.PublicViewUrl);
        Assert.Null(model.OpenCanonicalUrl);
    }

    [Fact]
    public async Task ViewNext_Get_ReturnsEmbedViewerShell_WithEmbedStateEndpoint()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(owner);
        var trip = TestDataFixtures.CreateTrip(owner, "Public", isPublic: true);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.ViewNext(trip.Id, embed: true);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<TripViewerShellViewModel>(view.Model);
        Assert.Equal("embed", model.ViewerMode);
        Assert.Equal($"/Public/TripsNext/{trip.Id}/state?embed=true", model.ViewerStateEndpoint);
        Assert.Equal($"/Public/TripsNext/{trip.Id}", model.OpenCanonicalUrl);
        Assert.True(model.IsEmbed);
    }

    [Fact]
    public async Task ViewNext_Get_PreservesAllowedMapQueryParameters_ForEmbedStateEndpoint()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(owner);
        var trip = TestDataFixtures.CreateTrip(owner, "Public", isPublic: true);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        var controller = BuildController(db);
        SetRequestQuery(controller, ("embed", "true"), ("lat", "1"), ("lon", "2"), ("zoom", "3"), ("mode", "private"));

        var result = await controller.ViewNext(trip.Id, embed: true);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<TripViewerShellViewModel>(view.Model);
        Assert.Equal("embed", model.ViewerMode);
        Assert.Equal($"/Public/TripsNext/{trip.Id}/state?embed=true&lat=1&lon=2&zoom=3", model.ViewerStateEndpoint);
        Assert.DoesNotContain("mode=", model.ViewerStateEndpoint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewNext_Get_PreservesLngCompatibility_ForEmbedStateEndpoint()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(owner);
        var trip = TestDataFixtures.CreateTrip(owner, "Public", isPublic: true);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        var controller = BuildController(db);
        SetRequestQuery(controller, ("lat", "1"), ("lng", "2"), ("zoom", "3"));

        var result = await controller.ViewNext(trip.Id, embed: true);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<TripViewerShellViewModel>(view.Model);
        Assert.Equal($"/Public/TripsNext/{trip.Id}/state?embed=true&lat=1&lng=2&zoom=3", model.ViewerStateEndpoint);
    }

    [Fact]
    public async Task ViewNext_Get_ForwardsLonAndLng_WhenBothMapParametersExist()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(owner);
        var trip = TestDataFixtures.CreateTrip(owner, "Public", isPublic: true);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        var controller = BuildController(db);
        SetRequestQuery(controller, ("lat", "1"), ("lon", "2"), ("lng", "9"), ("zoom", "3"));

        var result = await controller.ViewNext(trip.Id, embed: true);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<TripViewerShellViewModel>(view.Model);
        Assert.Equal($"/Public/TripsNext/{trip.Id}/state?embed=true&lat=1&lon=2&lng=9&zoom=3", model.ViewerStateEndpoint);
    }

    [Fact]
    public async Task ViewNext_Get_ReturnsNotFound_ForPrivateTrip()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "owner"));
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "owner", Name = "Private", IsPublic = false };
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.ViewNext(trip.Id);

        Assert.IsType<NotFoundResult>(result);
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

    private static void SetRequestQuery(Controller controller, params (string Key, string Value)[] values)
    {
        var queryValues = values.ToDictionary(
            value => value.Key,
            value => new StringValues(value.Value));
        controller.ControllerContext.HttpContext.Request.Query = new QueryCollection(queryValues);
    }
}
