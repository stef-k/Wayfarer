using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
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
        db.ApiTokens.Add(new ApiToken { Id = 1, Token = "tok", UserId = user.Id, Name = "test", User = user });
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
        db.ApiTokens.Add(new ApiToken { Id = 2, Token = "tok", UserId = other.Id, Name = "test", User = other });
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

    [Fact]
    public async Task GetTripBoundary_ReturnsBoundingBox_ForPublicTrip()
    {
        var db = CreateDbContext();
        var regionId = Guid.NewGuid();
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            UserId = "u1",
            Name = "HasGeo",
            IsPublic = true,
            Regions = new List<Region>
            {
                new Region
                {
                    Id = regionId,
                    TripId = trip.Id,
                    Name = "R1",
                    Places = new List<Place>
                    {
                        new Place
                        {
                            Id = Guid.NewGuid(),
                            Name = "P1",
                            UserId = trip.UserId,
                            RegionId = regionId,
                            Location = new Point(20, 10) { SRID = 4326 }
                        }
                    }
                }
            },
            Segments = new List<Segment>()
        };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db);

        var result = await controller.GetTripBoundary(trip.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var boundingBox = payload.GetType().GetProperty("BoundingBox")?.GetValue(payload);
        Assert.NotNull(boundingBox);
        double? north = boundingBox?.GetType().GetProperty("North")?.GetValue(boundingBox) as double?;
        double? south = boundingBox?.GetType().GetProperty("South")?.GetValue(boundingBox) as double?;
        double? east = boundingBox?.GetType().GetProperty("East")?.GetValue(boundingBox) as double?;
        double? west = boundingBox?.GetType().GetProperty("West")?.GetValue(boundingBox) as double?;
        Assert.True(north >= 10);
        Assert.True(south <= 10);
        Assert.True(east >= 20);
        Assert.True(west <= 20);
    }

    [Fact]
    public async Task UpdatePlace_ReturnsUnauthorized_WhenNoToken()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.UpdatePlace(Guid.NewGuid(), new PlaceUpdateRequestDto());

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task UpdatePlace_ReturnsBadRequest_WhenLatOrLonMissing()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "R1" };
        var place = new Place { Id = Guid.NewGuid(), RegionId = region.Id, Region = region, Name = "P1", UserId = user.Id };
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.Places.Add(place);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.UpdatePlace(place.Id, new PlaceUpdateRequestDto { Latitude = 10 });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdatePlace_MovesToAnotherRegion_ForOwner()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        var region1 = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "R1" };
        var region2 = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "R2" };
        var place = new Place { Id = Guid.NewGuid(), RegionId = region1.Id, Region = region1, Name = "P1", UserId = user.Id };
        db.Trips.Add(trip);
        db.Regions.AddRange(region1, region2);
        db.Places.Add(place);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.UpdatePlace(place.Id, new PlaceUpdateRequestDto { RegionId = region2.Id, Name = "Moved" });

        var ok = Assert.IsType<OkObjectResult>(result);
        var updated = db.Places.First(p => p.Id == place.Id);
        Assert.Equal(region2.Id, updated.RegionId);
        Assert.Equal("Moved", updated.Name);
    }

    [Fact]
    public async Task UpdateRegion_ReturnsBadRequest_ForReservedName()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "R1" };
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.UpdateRegion(region.Id, new RegionUpdateRequestDto { Name = "Unassigned Places" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateRegion_UpdatesFields_ForOwner()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "R1" };
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.UpdateRegion(region.Id, new RegionUpdateRequestDto { Name = "New Name", DisplayOrder = 5 });

        var ok = Assert.IsType<OkObjectResult>(result);
        var updated = db.Regions.First(r => r.Id == region.Id);
        Assert.Equal("New Name", updated.Name);
        Assert.Equal(5, updated.DisplayOrder);
    }

    [Fact]
    public async Task CloneTrip_ReturnsUnauthorized_WhenNoToken()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.CloneTrip(Guid.NewGuid());

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task CloneTrip_CreatesNewTrip_ForPublicSource()
    {
        var db = CreateDbContext();
        var sourceOwner = TestDataFixtures.CreateUser(id: "owner");
        var requester = SeedUserWithToken(db, "tok");
        var sourceTrip = new Trip
        {
            Id = Guid.NewGuid(),
            UserId = sourceOwner.Id,
            Name = "Public Trip",
            IsPublic = true,
            Regions = new List<Region>(),
            Segments = new List<Segment>()
        };
        db.Users.Add(sourceOwner);
        db.Trips.Add(sourceTrip);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.CloneTrip(sourceTrip.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var clonedId = (Guid?)payload.GetType().GetProperty("clonedTripId")?.GetValue(payload);
        Assert.NotNull(clonedId);
        Assert.True(db.Trips.Any(t => t.Id == clonedId && t.UserId == requester.Id));
    }

    private TripsController BuildController(ApplicationDbContext db, string? token = null)
    {
        var controller = new TripsController(db, NullLogger<BaseApiController>.Instance, Mock.Of<ITripTagService>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        if (!string.IsNullOrEmpty(token))
        {
            controller.ControllerContext.HttpContext.Request.Headers["Authorization"] = $"Bearer {token}";
        }
        return controller;
    }

    private static ApplicationUser SeedUserWithToken(ApplicationDbContext db, string token)
    {
        var user = TestDataFixtures.CreateUser(id: "seed-user", username: "seed");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { Token = token, UserId = user.Id, Name = "test", User = user });
        db.SaveChanges();
        return user;
    }
}
