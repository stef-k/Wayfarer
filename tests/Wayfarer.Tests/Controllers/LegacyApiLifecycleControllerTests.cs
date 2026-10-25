using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Verifies token authority and rejection of unconditional deletion bypasses.</summary>
public sealed class LegacyApiLifecycleControllerTests : TestBase
{
    /// <summary>Requires token authentication before Place or Region lifecycle discovery.</summary>
    [Fact]
    public async Task LifecycleDeletes_MissingAuthentication_ReturnUnauthorized()
    {
        var db = CreateDbContext();
        var seeded = Seed(db);
        var controller = BuildController(db);

        Assert.IsType<UnauthorizedObjectResult>(await controller.DeletePlace(seeded.WaypointId));
        Assert.IsType<UnauthorizedObjectResult>(await controller.DeleteRegion(seeded.RegionId));
    }

    /// <summary>Neither an absent nor legacy identity-only header can authorize the retired unconditional delete adapters.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnconditionalDeletion_RequiresContentPreview(bool region, bool header)
    {
        var db = CreateDbContext();
        var seeded = Seed(db);
        var controller = BuildController(db, seeded.Token);
        var tripId = db.Regions.Single(r => r.Id == seeded.RegionId).TripId;
        if (header)
        {
            var confirmation = new LifecycleDependencyConfirmation(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
            var lifecycle = new PlaceRegionLifecycleService(db, confirmation);
            var dependencies = region ? await lifecycle.DiscoverRegionDependenciesAsync(tripId, seeded.RegionId, "legacy-lifecycle-owner", default)
                : await lifecycle.DiscoverPlaceDependenciesAsync(tripId, seeded.WaypointId, "legacy-lifecycle-owner", default);
            // Even an authentic legacy confirmation is not a content-bound operation approval.
            controller.Request.Headers["X-Wayfarer-Dependency-Confirmation"] = confirmation.Create("legacy", region ? "region-delete" : "place-delete",
                "legacy-lifecycle-owner", tripId, region ? seeded.RegionId : seeded.WaypointId, dependencies!).ConfirmationToken;
        }
        var result = Assert.IsType<ObjectResult>(region ? await controller.DeleteRegion(seeded.RegionId) : await controller.DeletePlace(seeded.WaypointId));
        Assert.Equal(409, result.StatusCode);
        Assert.Equal("mutation-confirmation-required", Assert.IsAssignableFrom<ProblemDetails>(result.Value).Extensions["code"]);
        Assert.True(db.Places.Any(p => p.Id == seeded.WaypointId));
        Assert.True(db.Regions.Any(r => r.Id == seeded.RegionId));
    }

    private static TripsController BuildController(ApplicationDbContext db, string? token = null)
    {
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(item => item.GetSettings()).Returns(new ApplicationSettings());
        var controller = new TripsController(
            db,
            NullLogger<BaseApiController>.Instance,
            Mock.Of<ITripTagService>(),
            settings.Object,
            Mock.Of<ICacheWarmupScheduler>());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        if (token != null) controller.Request.Headers.Authorization = $"Bearer {token}";
        return controller;
    }

    private static LegacySeed Seed(ApplicationDbContext db)
    {
        const string token = "lifecycle-token";
        var user = TestDataFixtures.CreateUser(id: "legacy-lifecycle-owner");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Legacy lifecycle" };
        var region = new Region
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id,
            Name = "Deleted", DisplayOrder = 1
        };
        var outside = new Region
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id,
            Name = "Outside", DisplayOrder = 2
        };
        trip.Regions.Add(region);
        trip.Regions.Add(outside);
        var from = Place(outside, user.Id, "From", 1, 1);
        var waypoint = Place(region, user.Id, "Waypoint", 2, 2);
        var to = Place(outside, user.Id, "To", 3, 3);
        var segment = new Segment
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id,
            FromPlaceId = from.Id, ToPlaceId = to.Id, DisplayOrder = 1,
            RouteGeometry = new LineString([new(1, 1), new(2, 2), new(3, 3)]) { SRID = 4326 }
        };
        segment.Waypoints.Add(new SegmentWaypoint
        {
            Segment = segment, SegmentId = segment.Id, Place = waypoint, PlaceId = waypoint.Id,
            Position = 0, RouteVertexIndex = 1
        });
        trip.Segments.Add(segment);
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { Token = token, UserId = user.Id, User = user, Name = "Lifecycle" });
        db.Trips.Add(trip);
        db.SaveChanges();
        return new(token, region.Id, waypoint.Id);
    }

    private static Place Place(Region region, string userId, string name, double x, double y)
    {
        var place = new Place
        {
            Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = userId,
            Name = name, DisplayOrder = region.Places.Count + 1, Location = new Point(x, y) { SRID = 4326 }
        };
        region.Places.Add(place);
        return place;
    }

    private static T Property<T>(object value, string name) =>
        (T)value.GetType().GetProperty(name)!.GetValue(value)!;

    private sealed record LegacySeed(string Token, Guid RegionId, Guid WaypointId);
}
