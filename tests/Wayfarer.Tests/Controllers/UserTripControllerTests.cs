using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// User TripController basics.
/// </summary>
public class UserTripControllerTests : TestBase
{
    [Fact]
    public void Index_ReturnsViewResult_WithNoModel()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1", username: "alice"));
        db.SaveChanges();
        var controller = BuildController(db, "u1");

        var result = controller.Index();

        // Index now returns a model-less view shell (data loaded via AJAX)
        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }

    [Fact]
    public async Task View_ReturnsNotFound_WhenNotOwned()
    {
        var db = CreateDbContext();
        db.Users.AddRange(
            TestDataFixtures.CreateUser(id: "owner"),
            TestDataFixtures.CreateUser(id: "other"));
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "owner", Name = "Trip1" };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db, "other");

        var result = await controller.View(trip.Id);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task View_ExplicitlyLoadsOrderedWaypointPlaces()
    {
        var db = CreateDbContext();
        var userId = "owner";
        var trip = new Trip { Id = Guid.NewGuid(), UserId = userId, Name = "Waypoint trip" };
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, Name = "Region" };
        var start = new Place { Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = userId, Name = "A", Location = new Point(1, 1) { SRID = 4326 } };
        var via = new Place { Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = userId, Name = "B", Location = new Point(2, 2) { SRID = 4326 } };
        var end = new Place { Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = userId, Name = "C", Location = new Point(3, 3) { SRID = 4326 } };
        var segment = new Segment { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId, FromPlace = start, FromPlaceId = start.Id, ToPlace = end, ToPlaceId = end.Id };
        segment.Waypoints.Add(new SegmentWaypoint { Segment = segment, SegmentId = segment.Id, Place = via, PlaceId = via.Id, Position = 0 });
        region.Places.Add(start);
        region.Places.Add(via);
        region.Places.Add(end);
        trip.Regions.Add(region);
        trip.Segments.Add(segment);
        db.Users.Add(TestDataFixtures.CreateUser(id: userId, username: userId));
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await BuildController(db, userId).View(trip.Id);

        var model = Assert.IsType<Trip>(Assert.IsType<ViewResult>(result).Model);
        var loadedWaypoint = Assert.Single(Assert.Single(model.Segments).Waypoints);
        Assert.Equal("B", loadedWaypoint.Place.Name);
    }

    [Fact]
    public async Task Edit_Get_ReturnsView_WhenOwned()
    {
        var db = CreateDbContext();
        var userId = "u1";
        db.Users.Add(TestDataFixtures.CreateUser(id: userId, username: "alice"));
        var trip = new Trip { Id = Guid.NewGuid(), UserId = userId, Name = "Trip1" };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db, userId);

        var result = await controller.Edit(trip.Id);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("~/Areas/User/Views/Trip/Edit.cshtml", view.ViewName);
        var model = Assert.IsType<TripEditorShellViewModel>(view.Model);
        Assert.Equal(trip.Id, model.TripId);
        Assert.Equal($"/api/trips/{trip.Id}/editor", model.EditorEndpointUrl);
    }

    [Fact]
    public async Task Edit_Get_ReturnsNotFound_WhenNotOwned()
    {
        var db = CreateDbContext();
        db.Users.AddRange(
            TestDataFixtures.CreateUser(id: "owner"),
            TestDataFixtures.CreateUser(id: "other"));
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "owner", Name = "Trip1" };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db, "other");

        var result = await controller.Edit(trip.Id);

        Assert.IsType<NotFoundResult>(result);
    }

    private TripController BuildController(ApplicationDbContext db, string userId)
    {
        var controller = new TripController(
            NullLogger<TripController>.Instance,
            db,
            Mock.Of<ITripMapThumbnailGenerator>(),
            Mock.Of<ITripTagService>(),
            Mock.Of<ICacheWarmupScheduler>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = BuildHttpContextWithUser(userId)
        };
        controller.TempData = new TempDataDictionary(
            controller.ControllerContext.HttpContext,
            Mock.Of<ITempDataProvider>());
        return controller;
    }
}
