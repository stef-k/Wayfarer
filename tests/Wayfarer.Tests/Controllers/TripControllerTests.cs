using System;
using System.Collections.Generic;
using System.Linq;
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
/// Covers high-traffic user trip screens without touching production code.
/// </summary>
public class TripControllerTests : TestBase
{
    [Fact]
    public async Task Index_ReturnsCurrentUserTrips_SortedByUpdatedAtDescending()
    {
        // Arrange
        var db = CreateDbContext();
        var currentUser = TestDataFixtures.CreateUser(id: "user-current");
        var otherUser = TestDataFixtures.CreateUser(id: "user-other");
        db.Users.AddRange(currentUser, otherUser);

        var newerTrip = TestDataFixtures.CreateTrip(currentUser, "Newer Trip");
        newerTrip.UpdatedAt = new DateTime(2024, 6, 15);
        var olderTrip = TestDataFixtures.CreateTrip(currentUser, "Older Trip");
        olderTrip.UpdatedAt = new DateTime(2024, 5, 10);
        var otherUserTrip = TestDataFixtures.CreateTrip(otherUser, "Foreign Trip");
        otherUserTrip.UpdatedAt = new DateTime(2024, 7, 1);

        db.Trips.AddRange(newerTrip, olderTrip, otherUserTrip);
        await db.SaveChangesAsync();

        var controller = new TripController(
            NullLogger<TripController>.Instance,
            db,
            Mock.Of<ITripMapThumbnailGenerator>(),
            Mock.Of<ITripTagService>());
        ConfigureControllerWithUser(controller, currentUser.Id);

        // Act
        var result = await controller.Index();

        // Assert
        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<List<Trip>>(view.Model);
        Assert.Equal(2, model.Count);
        Assert.All(model, trip => Assert.Equal(currentUser.Id, trip.UserId));
        Assert.Contains(model, trip => trip.Id == newerTrip.Id);
        Assert.Contains(model, trip => trip.Id == olderTrip.Id);
        var expectedOrder = model
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => t.Id)
            .ToList();
        Assert.Equal(expectedOrder, model.Select(t => t.Id).ToList());
    }
}
