using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
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
    public async Task Index_ReturnsTrips_ForCurrentUser()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1", username: "alice"));
        db.Trips.Add(new Trip { Id = Guid.NewGuid(), UserId = "u1", Name = "Trip1", UpdatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var controller = BuildController(db, "u1");

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<IEnumerable<Trip>>(view.Model);
        Assert.Single(model);
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

    private TripController BuildController(ApplicationDbContext db, string userId)
    {
        var controller = new TripController(
            NullLogger<TripController>.Instance,
            db,
            Mock.Of<ITripMapThumbnailGenerator>(),
            Mock.Of<ITripTagService>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = BuildHttpContextWithUser(userId)
        };
        return controller;
    }
}
