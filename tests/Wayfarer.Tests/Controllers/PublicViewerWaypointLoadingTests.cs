using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Verifies public viewer loading of authorized ordered waypoint Places.</summary>
public sealed class PublicViewerWaypointLoadingTests : TestBase
{
    [Fact]
    public async Task View_ExplicitlyLoadsOrderedWaypointPlaces_ForPublicTrip()
    {
        var db = CreateDbContext();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "owner", Name = "Public waypoint trip", IsPublic = true, UpdatedAt = DateTime.UtcNow };
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = trip.UserId, Name = "Region" };
        var start = Place("A", 1, region);
        var via = Place("B", 2, region);
        var end = Place("C", 3, region);
        var segment = new Segment { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = trip.UserId, FromPlace = start, FromPlaceId = start.Id, ToPlace = end, ToPlaceId = end.Id };
        segment.Waypoints.Add(new SegmentWaypoint { Segment = segment, SegmentId = segment.Id, Place = via, PlaceId = via.Id, Position = 0 });
        region.Places.Add(start);
        region.Places.Add(via);
        region.Places.Add(end);
        trip.Regions.Add(region);
        trip.Segments.Add(segment);
        db.Users.Add(TestDataFixtures.CreateUser(id: trip.UserId, username: trip.UserId));
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await BuildController(db).View(trip.Id);

        var model = Assert.IsType<Trip>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal("B", Assert.Single(Assert.Single(model.Segments).Waypoints).Place.Name);
    }

    /// <summary>Builds one authorized saved Place for the loading boundary fixture.</summary>
    private static Place Place(string name, double coordinate, Region region) => new()
    {
        Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = region.UserId,
        Name = name, Location = new Point(coordinate, coordinate) { SRID = 4326 }
    };

    /// <summary>Constructs the production public controller with inert unrelated dependencies.</summary>
    private static TripViewerController BuildController(ApplicationDbContext db)
    {
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(service => service.GetSettings()).Returns(new ApplicationSettings());
        var imageProxy = new ImageProxyService(new HttpClient(), Mock.Of<IProxiedImageCacheService>(), settings.Object,
            Mock.Of<IServiceScopeFactory>(), NullLogger<ImageProxyService>.Instance);
        var controller = new TripViewerController(NullLogger<TripViewerController>.Instance, db, new HttpClient(),
            Mock.Of<ITripThumbnailService>(), Mock.Of<ITripTagService>(), imageProxy, settings.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }
}
