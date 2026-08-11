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

/// <summary>Verifies legacy token API lifecycle confirmation and stable success compatibility.</summary>
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

    /// <summary>Returns the shared bounded 409 contract and mutates nothing without Place confirmation.</summary>
    [Fact]
    public async Task DeletePlace_MissingConfirmation_ReturnsStableWarningWithoutMutation()
    {
        var db = CreateDbContext();
        var seeded = Seed(db);
        var controller = BuildController(db, seeded.Token);

        var conflict = Assert.IsType<ConflictObjectResult>(await controller.DeletePlace(seeded.WaypointId));
        var warning = Assert.IsType<EditorLifecycleConflictDto>(conflict.Value);

        Assert.Equal("place-delete-dependencies", warning.Code);
        Assert.True(db.Places.Any(item => item.Id == seeded.WaypointId));
    }

    /// <summary>Returns a fresh stale warning and preserves state when the supplied Place token is invalid.</summary>
    [Fact]
    public async Task DeletePlace_InvalidConfirmation_ReturnsFreshStableWarningWithoutMutation()
    {
        var db = CreateDbContext();
        var seeded = Seed(db);
        var controller = BuildController(db, seeded.Token);
        controller.Request.Headers["X-Wayfarer-Dependency-Confirmation"] = "malformed";

        var conflict = Assert.IsType<ConflictObjectResult>(await controller.DeletePlace(seeded.WaypointId));
        var warning = Assert.IsType<EditorLifecycleConflictDto>(conflict.Value);

        Assert.Equal("lifecycle-confirmation-stale", warning.Code);
        Assert.False(string.IsNullOrWhiteSpace(warning.ConfirmationToken));
        Assert.True(db.Places.Any(item => item.Id == seeded.WaypointId));
    }

    /// <summary>Accepts header-only Place confirmation and preserves the established anonymous success shape.</summary>
    [Fact]
    public async Task DeletePlace_ValidHeaderConfirmation_PreservesLegacySuccessShape()
    {
        var db = CreateDbContext();
        var seeded = Seed(db);
        var controller = BuildController(db, seeded.Token);
        var challenge = Assert.IsType<EditorLifecycleConflictDto>(
            Assert.IsType<ConflictObjectResult>(await controller.DeletePlace(seeded.WaypointId)).Value);
        controller.Request.Headers["X-Wayfarer-Dependency-Confirmation"] = challenge.ConfirmationToken;

        var ok = Assert.IsType<OkObjectResult>(await controller.DeletePlace(seeded.WaypointId));

        Assert.True(Property<bool>(ok.Value!, "success"));
        Assert.Equal(seeded.WaypointId, Property<Guid>(ok.Value!, "placeId"));
        Assert.False(db.Places.Any(item => item.Id == seeded.WaypointId));
    }

    /// <summary>Uses the same 409 protocol for Region deletion and preserves its established success shape.</summary>
    [Fact]
    public async Task DeleteRegion_ConfirmationRoundTrip_PreservesLegacySuccessShape()
    {
        var db = CreateDbContext();
        var seeded = Seed(db);
        var controller = BuildController(db, seeded.Token);
        var challengeResult = Assert.IsType<ConflictObjectResult>(await controller.DeleteRegion(seeded.RegionId));
        var challenge = Assert.IsType<EditorLifecycleConflictDto>(challengeResult.Value);
        Assert.Equal("region-delete-dependencies", challenge.Code);
        controller.Request.Headers["X-Wayfarer-Dependency-Confirmation"] = challenge.ConfirmationToken;

        var ok = Assert.IsType<OkObjectResult>(await controller.DeleteRegion(seeded.RegionId));

        Assert.True(Property<bool>(ok.Value!, "success"));
        Assert.Equal(seeded.RegionId, Property<Guid>(ok.Value!, "regionId"));
        Assert.False(db.Regions.Any(item => item.Id == seeded.RegionId));
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
