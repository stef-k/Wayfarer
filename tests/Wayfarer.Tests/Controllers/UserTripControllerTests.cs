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
        var model = Assert.IsType<Trip>(view.Model);
        Assert.Equal(trip.Id, model.Id);
    }

    [Fact]
    public async Task Edit_Get_RedirectsToIndex_WhenNotOwned()
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

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
    }

    [Fact]
    public async Task Edit_Post_UpdatesTrip_WhenValid()
    {
        var db = CreateDbContext();
        var userId = "u1";
        db.Users.Add(TestDataFixtures.CreateUser(id: userId, username: "alice"));
        var trip = new Trip { Id = Guid.NewGuid(), UserId = userId, Name = "Old Name", IsPublic = false };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db, userId);

        var result = await controller.Edit(trip.Id, new Trip
        {
            Id = trip.Id,
            Name = "Updated Name",
            IsPublic = true,
            Notes = "New notes",
            CoverImageUrl = "https://example.com/cover.jpg"
        }, null);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        var updated = await db.Trips.FindAsync(trip.Id);
        Assert.Equal("Updated Name", updated!.Name);
        Assert.True(updated.IsPublic);
        Assert.Equal("New notes", updated.Notes);
    }

    [Fact]
    public async Task Edit_Post_RedirectsToEdit_WhenSaveEdit()
    {
        var db = CreateDbContext();
        var userId = "u1";
        db.Users.Add(TestDataFixtures.CreateUser(id: userId, username: "alice"));
        var trip = new Trip { Id = Guid.NewGuid(), UserId = userId, Name = "Trip" };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db, userId);

        var result = await controller.Edit(trip.Id, new Trip
        {
            Id = trip.Id,
            Name = "Updated"
        }, "save-edit");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Edit", redirect.ActionName);
    }

    [Fact]
    public async Task Edit_Post_RedirectsToIndex_WhenIdMismatch()
    {
        var db = CreateDbContext();
        var userId = "u1";
        db.Users.Add(TestDataFixtures.CreateUser(id: userId, username: "alice"));
        var trip = new Trip { Id = Guid.NewGuid(), UserId = userId, Name = "Trip" };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db, userId);

        var result = await controller.Edit(trip.Id, new Trip
        {
            Id = Guid.NewGuid(), // different ID
            Name = "Updated"
        }, null);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
    }

    [Fact]
    public async Task Edit_Post_RedirectsToIndex_WhenNotOwned()
    {
        var db = CreateDbContext();
        db.Users.AddRange(
            TestDataFixtures.CreateUser(id: "owner"),
            TestDataFixtures.CreateUser(id: "other"));
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "owner", Name = "Trip" };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db, "other");

        var result = await controller.Edit(trip.Id, new Trip
        {
            Id = trip.Id,
            Name = "Updated"
        }, null);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
    }

    [Fact]
    public async Task Edit_Post_ReturnsView_WhenModelInvalid()
    {
        var db = CreateDbContext();
        var userId = "u1";
        db.Users.Add(TestDataFixtures.CreateUser(id: userId, username: "alice"));
        var trip = new Trip { Id = Guid.NewGuid(), UserId = userId, Name = "Trip" };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db, userId);
        controller.ModelState.AddModelError("Name", "Required");

        var result = await controller.Edit(trip.Id, new Trip
        {
            Id = trip.Id,
            Name = ""
        }, null);

        var view = Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
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
