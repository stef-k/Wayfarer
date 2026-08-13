using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Exercises public Trip waypoint loading, privacy failure, and owner authorization.</summary>
public sealed class PublicTripWaypointControllerTests : TestBase
{
    [Fact]
    public void GetTrip_LoadsAndReturnsCompleteWaypointFallback()
    {
        var db = CreateDbContext();
        var trip = CreatePublicWaypointTrip("owner");
        db.Add(trip);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        var controller = BuildController(db);

        var result = controller.GetTrip(trip.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<ApiTripDto>(ok.Value);
        var segment = Assert.Single(dto.Segments!);
        Assert.False(segment.HasCustomRoute);
        Assert.Single(segment.Waypoints);
        using var route = JsonDocument.Parse(segment.RouteJson!);
        Assert.Equal(3, route.RootElement.GetProperty("coordinates").GetArrayLength());
    }

    [Fact]
    public void GetTrip_FailsClosedWithoutForeignWaypointDisclosure()
    {
        var db = CreateDbContext();
        var trip = CreatePublicWaypointTrip("owner");
        var foreignTrip = new Trip { Id = Guid.NewGuid(), UserId = "other", Name = "Foreign", IsPublic = false };
        var foreignRegion = new Region
        {
            Id = Guid.NewGuid(),
            Trip = foreignTrip,
            TripId = foreignTrip.Id,
            UserId = foreignTrip.UserId,
            Name = "Private region"
        };
        var foreignPlace = CreatePlace(foreignRegion, foreignTrip.UserId, "Private waypoint", 120, 45);
        foreignRegion.Places.Add(foreignPlace);
        foreignTrip.Regions.Add(foreignRegion);
        var waypoint = Assert.Single(Assert.Single(trip.Segments).Waypoints);
        waypoint.Place = foreignPlace;
        waypoint.PlaceId = foreignPlace.Id;
        db.AddRange(trip, foreignTrip);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        var controller = BuildController(db);

        var result = controller.GetTrip(trip.Id);

        var failure = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, failure.StatusCode);
        var json = JsonSerializer.Serialize(failure.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain(foreignPlace.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(foreignPlace.Name, json, StringComparison.Ordinal);
        Assert.DoesNotContain("120", json, StringComparison.Ordinal);
    }

    [Fact]
    public void GetTrip_ReturnsPrivateTripForOwnerToken()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(owner);
        db.ApiTokens.Add(new ApiToken { Id = 3, Token = "owner-token", UserId = owner.Id, Name = "test", User = owner });
        var trip = new Trip { Id = Guid.NewGuid(), UserId = owner.Id, Name = "Private", IsPublic = false };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db, "owner-token");

        var result = controller.GetTrip(trip.Id);

        Assert.IsType<OkObjectResult>(result);
    }

    private static Trip CreatePublicWaypointTrip(string userId)
    {
        var trip = new Trip { Id = Guid.NewGuid(), UserId = userId, Name = "Public journey", IsPublic = true };
        var region = new Region
        {
            Id = Guid.NewGuid(),
            Trip = trip,
            TripId = trip.Id,
            UserId = userId,
            Name = "Region"
        };
        var from = CreatePlace(region, userId, "A", 23.72, 37.98);
        var via = CreatePlace(region, userId, "B", 23.73, 37.99);
        var to = CreatePlace(region, userId, "C", 23.74, 38.0);
        var segment = new Segment
        {
            Id = Guid.NewGuid(),
            Trip = trip,
            TripId = trip.Id,
            UserId = userId,
            FromPlace = from,
            FromPlaceId = from.Id,
            ToPlace = to,
            ToPlaceId = to.Id,
            Mode = "walk"
        };
        segment.Waypoints.Add(new SegmentWaypoint
        {
            Segment = segment,
            SegmentId = segment.Id,
            Place = via,
            PlaceId = via.Id,
            Position = 0
        });
        region.Places.Add(from);
        region.Places.Add(via);
        region.Places.Add(to);
        trip.Regions.Add(region);
        trip.Segments.Add(segment);
        return trip;
    }

    private static Place CreatePlace(
        Region region,
        string userId,
        string name,
        double longitude,
        double latitude) => new()
    {
        Id = Guid.NewGuid(),
        Region = region,
        RegionId = region.Id,
        UserId = userId,
        Name = name,
        Location = new Point(longitude, latitude) { SRID = 4326 }
    };

    private static TripsController BuildController(ApplicationDbContext db, string? token = null)
    {
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(service => service.GetSettings()).Returns(new ApplicationSettings());
        var controller = new TripsController(
            db,
            NullLogger<BaseApiController>.Instance,
            Mock.Of<ITripTagService>(),
            settings.Object,
            Mock.Of<ICacheWarmupScheduler>());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        if (!string.IsNullOrEmpty(token))
            controller.ControllerContext.HttpContext.Request.Headers.Authorization = $"Bearer {token}";
        return controller;
    }
}
