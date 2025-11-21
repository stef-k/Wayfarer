using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// API Trips controller high-ROI paths.
/// </summary>
public class ApiTripsControllerTests : TestBase
{
    [Fact]
    public void GetUserTrips_ReturnsUnauthorized_WhenNoToken()
    {
        var controller = BuildController(CreateDbContext());

        var result = controller.GetUserTrips();

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal("Missing or invalid API token.", unauthorized.Value);
    }

    [Fact]
    public void GetUserTrips_ReturnsTrips_ForOwner()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { Id = Guid.NewGuid(), Token = "tok", UserId = user.Id, Name = "test" });
        db.Trips.Add(new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip1", UpdatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var controller = BuildController(db);
        controller.ControllerContext.HttpContext.Request.Headers["Authorization"] = "Bearer tok";

        var result = controller.GetUserTrips();

        var ok = Assert.IsType<OkObjectResult>(result);
        var trips = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value!);
        Assert.Single(trips);
    }

    [Fact]
    public void GetTrip_Unauthorized_ForPrivateDifferentUser()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var other = TestDataFixtures.CreateUser(id: "other");
        db.Users.AddRange(owner, other);
        db.ApiTokens.Add(new ApiToken { Id = Guid.NewGuid(), Token = "tok", UserId = other.Id, Name = "test" });
        db.Trips.Add(new Trip { Id = Guid.NewGuid(), UserId = owner.Id, Name = "Private", IsPublic = false });
        db.SaveChanges();
        var controller = BuildController(db);
        controller.ControllerContext.HttpContext.Request.Headers["Authorization"] = "Bearer tok";

        var result = controller.GetTrip(db.Trips.First().Id);

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public void GetTrip_ReturnsOk_ForPublicTrip()
    {
        var db = CreateDbContext();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "u1", Name = "Public", IsPublic = true, UpdatedAt = DateTime.UtcNow, Regions = new List<Region>(), Segments = new List<Segment>() };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db);

        var result = controller.GetTrip(trip.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(trip.Id, ok.Value?.GetType().GetProperty("Id")?.GetValue(ok.Value));
    }

    [Fact]
    public async Task GetTripBoundary_ReturnsBadRequest_WhenNoGeo()
    {
        var db = CreateDbContext();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "u1", Name = "NoGeo", IsPublic = true, Regions = new List<Region>(), Segments = new List<Segment>() };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db);

        var result = await controller.GetTripBoundary(trip.Id);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    private TripsController BuildController(ApplicationDbContext db)
    {
        var controller = new TripsController(db, NullLogger<BaseApiController>.Instance, Mock.Of<ITripTagService>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }
}
