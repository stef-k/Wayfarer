using System;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Temporary Trip Editor workspace route behavior after the canonical Edit cutover.
/// </summary>
public class TripWorkspaceControllerTests : TestBase
{
    [Fact]
    public void Controller_RequiresUserRole()
    {
        var authorize = typeof(TripWorkspaceController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();

        Assert.Equal("User", authorize.Roles);
    }

    [Fact]
    public async Task Workspace_RedirectsToCanonicalEdit_WhenTripOwned()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "owner");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Owned Trip" };
        db.Users.Add(user);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        var controller = BuildController(db, user.Id);

        var result = await controller.Workspace(trip.Id);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Edit", redirect.ActionName);
        Assert.Equal("Trip", redirect.ControllerName);
        Assert.Equal("User", redirect.RouteValues?["area"]);
        Assert.Equal(trip.Id, redirect.RouteValues?["id"]);
        Assert.False(redirect.Permanent);
    }

    [Fact]
    public async Task Workspace_ReturnsNotFound_WhenTripMissing()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db, user.Id);

        var result = await controller.Workspace(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Workspace_ReturnsNotFound_WhenTripNotOwned()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var other = TestDataFixtures.CreateUser(id: "other");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = owner.Id, Name = "Owned Trip" };
        db.Users.AddRange(owner, other);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        var controller = BuildController(db, other.Id);

        var result = await controller.Workspace(trip.Id);

        Assert.IsType<NotFoundResult>(result);
    }

    private static TripWorkspaceController BuildController(ApplicationDbContext db, string userId)
    {
        var controller = new TripWorkspaceController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = BuildHttpContextWithUser(userId)
        };
        return controller;
    }
}
