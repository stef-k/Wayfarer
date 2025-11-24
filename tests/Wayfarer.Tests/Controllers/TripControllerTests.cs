using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
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

    [Fact]
    public async Task Clone_ReturnsNotFound_WhenTripDoesNotExist()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "user1");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        ConfigureControllerWithUser(controller, user.Id);

        var result = await controller.Clone(Guid.NewGuid());

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("TripViewer", redirect.ControllerName);
        Assert.Equal("Public", redirect.RouteValues["area"]);
    }

    [Fact]
    public async Task Clone_ReturnsRedirect_WhenTripIsNotPublic()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var cloner = TestDataFixtures.CreateUser(id: "cloner");
        db.Users.AddRange(owner, cloner);
        var trip = TestDataFixtures.CreateTrip(owner, "Private Trip");
        trip.IsPublic = false;
        db.Trips.Add(trip);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        ConfigureControllerWithUser(controller, cloner.Id);

        var result = await controller.Clone(trip.Id);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("TripViewer", redirect.ControllerName);
    }

    [Fact]
    public async Task Clone_RedirectsToEdit_WhenUserClonesOwnTrip()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "user1");
        db.Users.Add(user);
        var trip = TestDataFixtures.CreateTrip(user, "My Trip");
        trip.IsPublic = true;
        db.Trips.Add(trip);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        ConfigureControllerWithUser(controller, user.Id);

        var result = await controller.Clone(trip.Id);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Edit", redirect.ActionName);
        Assert.Equal(trip.Id, redirect.RouteValues["id"]);
    }

    [Fact]
    public async Task Clone_CreatesNewTrip_WithBasicProperties()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var cloner = TestDataFixtures.CreateUser(id: "cloner");
        db.Users.AddRange(owner, cloner);
        var sourceTrip = TestDataFixtures.CreateTrip(owner, "Source Trip");
        sourceTrip.IsPublic = true;
        sourceTrip.Notes = "Original notes";
        sourceTrip.CenterLat = 40.7128;
        sourceTrip.CenterLon = -74.0060;
        sourceTrip.Zoom = 12;
        sourceTrip.CoverImageUrl = "http://example.com/image.jpg";
        db.Trips.Add(sourceTrip);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        ConfigureControllerWithUser(controller, cloner.Id);

        var result = await controller.Clone(sourceTrip.Id);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Edit", redirect.ActionName);

        var clonedTrip = db.Trips.First(t => t.UserId == cloner.Id);
        Assert.NotEqual(sourceTrip.Id, clonedTrip.Id);
        Assert.Equal(cloner.Id, clonedTrip.UserId);
        Assert.Equal("Source Trip (Copy)", clonedTrip.Name);
        Assert.Equal(sourceTrip.Notes, clonedTrip.Notes);
        Assert.False(clonedTrip.IsPublic);
        Assert.Equal(sourceTrip.CenterLat, clonedTrip.CenterLat);
        Assert.Equal(sourceTrip.CenterLon, clonedTrip.CenterLon);
        Assert.Equal(sourceTrip.Zoom, clonedTrip.Zoom);
        Assert.Equal(sourceTrip.CoverImageUrl, clonedTrip.CoverImageUrl);
    }

    [Fact]
    public async Task Clone_ClonesRegionsWithPlaces()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var cloner = TestDataFixtures.CreateUser(id: "cloner");
        db.Users.AddRange(owner, cloner);

        var sourceTrip = TestDataFixtures.CreateTrip(owner, "Source Trip");
        sourceTrip.IsPublic = true;
        var region = new Region
        {
            Id = Guid.NewGuid(),
            TripId = sourceTrip.Id,
            UserId = owner.Id,
            Name = "Region 1",
            Notes = "Region notes",
            DisplayOrder = 1,
            CoverImageUrl = "http://example.com/region.jpg",
            Center = new Point(10, 20) { SRID = 4326 }
        };
        var place = new Place
        {
            Id = Guid.NewGuid(),
            RegionId = region.Id,
            UserId = owner.Id,
            Name = "Place 1",
            Location = new Point(10.5, 20.5) { SRID = 4326 },
            Notes = "Place notes",
            DisplayOrder = 1,
            IconName = "restaurant",
            MarkerColor = "#FF0000",
            Address = "123 Main St"
        };
        region.Places = new List<Place> { place };
        sourceTrip.Regions = new List<Region> { region };
        db.Trips.Add(sourceTrip);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        ConfigureControllerWithUser(controller, cloner.Id);

        var result = await controller.Clone(sourceTrip.Id);

        var clonedTrip = db.Trips
            .Include(t => t.Regions!)
            .ThenInclude(r => r.Places)
            .First(t => t.UserId == cloner.Id);

        Assert.Single(clonedTrip.Regions);
        var clonedRegion = clonedTrip.Regions.First();
        Assert.NotEqual(region.Id, clonedRegion.Id);
        Assert.Equal(cloner.Id, clonedRegion.UserId);
        Assert.Equal(region.Name, clonedRegion.Name);
        Assert.Equal(region.Notes, clonedRegion.Notes);
        Assert.Equal(region.DisplayOrder, clonedRegion.DisplayOrder);

        Assert.Single(clonedRegion.Places);
        var clonedPlace = clonedRegion.Places.First();
        Assert.NotEqual(place.Id, clonedPlace.Id);
        Assert.Equal(cloner.Id, clonedPlace.UserId);
        Assert.Equal(place.Name, clonedPlace.Name);
        Assert.Equal(place.Notes, clonedPlace.Notes);
        Assert.Equal(place.IconName, clonedPlace.IconName);
        Assert.Equal(place.MarkerColor, clonedPlace.MarkerColor);
        Assert.Equal(place.Address, clonedPlace.Address);
    }

    [Fact]
    public async Task Clone_ClonesRegionsWithAreas()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var cloner = TestDataFixtures.CreateUser(id: "cloner");
        db.Users.AddRange(owner, cloner);

        var sourceTrip = TestDataFixtures.CreateTrip(owner, "Source Trip");
        sourceTrip.IsPublic = true;
        var region = new Region
        {
            Id = Guid.NewGuid(),
            TripId = sourceTrip.Id,
            UserId = owner.Id,
            Name = "Region 1",
            DisplayOrder = 1
        };
        var area = new Area
        {
            Id = Guid.NewGuid(),
            RegionId = region.Id,
            Name = "Area 1",
            Notes = "Area notes",
            DisplayOrder = 1,
            FillHex = "#00FF00",
            Geometry = new Polygon(new LinearRing(new[]
            {
                new Coordinate(0, 0),
                new Coordinate(1, 0),
                new Coordinate(1, 1),
                new Coordinate(0, 1),
                new Coordinate(0, 0)
            })) { SRID = 4326 }
        };
        region.Areas = new List<Area> { area };
        sourceTrip.Regions = new List<Region> { region };
        db.Trips.Add(sourceTrip);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        ConfigureControllerWithUser(controller, cloner.Id);

        var result = await controller.Clone(sourceTrip.Id);

        var clonedTrip = db.Trips
            .Include(t => t.Regions!)
            .ThenInclude(r => r.Areas)
            .First(t => t.UserId == cloner.Id);

        var clonedRegion = clonedTrip.Regions.First();
        Assert.Single(clonedRegion.Areas);
        var clonedArea = clonedRegion.Areas.First();
        Assert.NotEqual(area.Id, clonedArea.Id);
        Assert.Equal(area.Name, clonedArea.Name);
        Assert.Equal(area.Notes, clonedArea.Notes);
        Assert.Equal(area.FillHex, clonedArea.FillHex);
        Assert.NotNull(clonedArea.Geometry);
    }

    [Fact]
    public async Task Clone_ClonesSegmentsWithPlaceMapping()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var cloner = TestDataFixtures.CreateUser(id: "cloner");
        db.Users.AddRange(owner, cloner);

        var sourceTrip = TestDataFixtures.CreateTrip(owner, "Source Trip");
        sourceTrip.IsPublic = true;
        var region = new Region
        {
            Id = Guid.NewGuid(),
            TripId = sourceTrip.Id,
            UserId = owner.Id,
            Name = "Region 1"
        };
        var place1 = new Place
        {
            Id = Guid.NewGuid(),
            RegionId = region.Id,
            UserId = owner.Id,
            Name = "Place 1",
            Location = new Point(10, 20) { SRID = 4326 }
        };
        var place2 = new Place
        {
            Id = Guid.NewGuid(),
            RegionId = region.Id,
            UserId = owner.Id,
            Name = "Place 2",
            Location = new Point(11, 21) { SRID = 4326 }
        };
        region.Places = new List<Place> { place1, place2 };

        var segment = new Segment
        {
            Id = Guid.NewGuid(),
            TripId = sourceTrip.Id,
            UserId = owner.Id,
            Mode = "drive",
            FromPlaceId = place1.Id,
            ToPlaceId = place2.Id,
            RouteGeometry = new LineString(new[]
            {
                new Coordinate(10, 20),
                new Coordinate(11, 21)
            }) { SRID = 4326 },
            EstimatedDuration = TimeSpan.FromMinutes(30),
            EstimatedDistanceKm = 25.5,
            DisplayOrder = 1,
            Notes = "Segment notes"
        };

        sourceTrip.Regions = new List<Region> { region };
        sourceTrip.Segments = new List<Segment> { segment };
        db.Trips.Add(sourceTrip);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        ConfigureControllerWithUser(controller, cloner.Id);

        var result = await controller.Clone(sourceTrip.Id);

        var clonedTrip = db.Trips
            .Include(t => t.Regions!)
            .ThenInclude(r => r.Places)
            .Include(t => t.Segments)
            .First(t => t.UserId == cloner.Id);

        Assert.Single(clonedTrip.Segments);
        var clonedSegment = clonedTrip.Segments.First();
        Assert.NotEqual(segment.Id, clonedSegment.Id);
        Assert.Equal(cloner.Id, clonedSegment.UserId);
        Assert.Equal(segment.Mode, clonedSegment.Mode);
        Assert.Equal(segment.Notes, clonedSegment.Notes);
        Assert.Equal(segment.EstimatedDuration, clonedSegment.EstimatedDuration);
        Assert.Equal(segment.EstimatedDistanceKm, clonedSegment.EstimatedDistanceKm);

        // Verify place ID mapping worked
        Assert.NotEqual(place1.Id, clonedSegment.FromPlaceId);
        Assert.NotEqual(place2.Id, clonedSegment.ToPlaceId);
        var clonedPlaceIds = clonedTrip.Regions.First().Places.Select(p => p.Id).ToList();
        Assert.Contains(clonedSegment.FromPlaceId.Value, clonedPlaceIds);
        Assert.Contains(clonedSegment.ToPlaceId.Value, clonedPlaceIds);
    }

    [Fact]
    public async Task Clone_ClonesTagsFromSourceTrip()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var cloner = TestDataFixtures.CreateUser(id: "cloner");
        db.Users.AddRange(owner, cloner);

        var tag1 = new Tag { Id = Guid.NewGuid(), Name = "adventure", Slug = "adventure" };
        var tag2 = new Tag { Id = Guid.NewGuid(), Name = "hiking", Slug = "hiking" };
        db.Tags.AddRange(tag1, tag2);

        var sourceTrip = TestDataFixtures.CreateTrip(owner, "Source Trip");
        sourceTrip.IsPublic = true;
        sourceTrip.Tags = new List<Tag> { tag1, tag2 };
        db.Trips.Add(sourceTrip);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        ConfigureControllerWithUser(controller, cloner.Id);

        var result = await controller.Clone(sourceTrip.Id);

        var clonedTrip = db.Trips
            .Include(t => t.Tags)
            .First(t => t.UserId == cloner.Id);

        Assert.Equal(2, clonedTrip.Tags.Count);
        Assert.Contains(clonedTrip.Tags, t => t.Id == tag1.Id);
        Assert.Contains(clonedTrip.Tags, t => t.Id == tag2.Id);
    }

    [Fact]
    public async Task Edit_Get_RedirectsWithAlert_WhenTripMissing()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "user-1");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = BuildControllerWithUser(db, user.Id);

        var result = await controller.Edit(Guid.NewGuid());

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(TripController.Index), redirect.ActionName);
        Assert.Equal("Trip not found.", controller.TempData["AlertMessage"]);
    }

    [Fact]
    public async Task Edit_Get_ReturnsView_WithLoadedCollections()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(user);
        var trip = TestDataFixtures.CreateTrip(user, "Owned Trip");
        var region = new Region
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = user.Id,
            Name = "Region",
            Places = new List<Place> { new() { Id = Guid.NewGuid(), UserId = user.Id, RegionId = Guid.NewGuid(), Name = "Place" } }
        };
        var segment = new Segment
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = user.Id,
            Mode = "walk"
        };
        trip.Regions = new List<Region> { region };
        trip.Segments = new List<Segment> { segment };
        db.Trips.Add(trip);
        await db.SaveChangesAsync();

        var controller = BuildControllerWithUser(db, user.Id);

        var result = await controller.Edit(trip.Id);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<Trip>(view.Model);
        Assert.NotNull(model.Regions);
        Assert.Single(model.Regions);
        Assert.NotNull(model.Regions.First().Places);
        Assert.Single(model.Regions.First().Places);
        Assert.NotNull(model.Segments);
        Assert.Single(model.Segments);
    }

    [Fact]
    public async Task Edit_Post_Redirects_WhenIdMismatch()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(user);
        var trip = TestDataFixtures.CreateTrip(user, "Trip");
        db.Trips.Add(trip);
        await db.SaveChangesAsync();

        var controller = BuildControllerWithUser(db, user.Id);
        var model = new Trip { Id = Guid.NewGuid(), Name = "Changed" };

        var result = await controller.Edit(trip.Id, model, submitAction: null);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(TripController.Index), redirect.ActionName);
        Assert.Equal("ID mismatch.", controller.TempData["AlertMessage"]);
        Assert.Equal("Trip", db.Trips.Find(trip.Id)!.Name);
    }

    [Fact]
    public async Task Edit_Post_Redirects_WhenUnauthorized()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var other = TestDataFixtures.CreateUser(id: "other");
        db.Users.AddRange(owner, other);
        var trip = TestDataFixtures.CreateTrip(owner, "Secret Trip");
        db.Trips.Add(trip);
        await db.SaveChangesAsync();

        var controller = BuildControllerWithUser(db, other.Id);
        var model = new Trip { Id = trip.Id, Name = "Attempted change" };

        var result = await controller.Edit(trip.Id, model, submitAction: null);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(TripController.Index), redirect.ActionName);
        Assert.Equal("Unauthorized or trip not found.", controller.TempData["AlertMessage"]);
        Assert.Equal("Secret Trip", db.Trips.Find(trip.Id)!.Name);
    }

    [Fact]
    public async Task Edit_Post_UpdatesTrip_AndRedirectsToIndex()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(user);
        var trip = TestDataFixtures.CreateTrip(user, "Original");
        trip.IsPublic = false;
        trip.Notes = "Old notes";
        trip.CenterLat = 1;
        trip.CenterLon = 2;
        trip.Zoom = 3;
        trip.CoverImageUrl = "http://old";
        trip.UpdatedAt = new DateTime(2024, 1, 1);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();

        var thumbnailMock = new Mock<ITripMapThumbnailGenerator>();
        var controller = BuildControllerWithUser(db, user.Id, thumbnailMock);
        var model = new Trip
        {
            Id = trip.Id,
            Name = "Updated",
            IsPublic = true,
            Notes = "New notes",
            CenterLat = 5,
            CenterLon = 6,
            Zoom = 7,
            CoverImageUrl = "http://new"
        };

        var result = await controller.Edit(trip.Id, model, submitAction: null);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(TripController.Index), redirect.ActionName);

        var updated = db.Trips.Find(trip.Id)!;
        Assert.Equal("Updated", updated.Name);
        Assert.True(updated.IsPublic);
        Assert.Equal("New notes", updated.Notes);
        Assert.Equal(5, updated.CenterLat);
        Assert.Equal(6, updated.CenterLon);
        Assert.Equal(7, updated.Zoom);
        Assert.Equal("http://new", updated.CoverImageUrl);
        Assert.NotEqual(new DateTime(2024, 1, 1), updated.UpdatedAt);
        thumbnailMock.Verify(t => t.InvalidateThumbnails(trip.Id, It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task Edit_Post_RedirectsToEdit_WhenSaveEditRequested()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(user);
        var trip = TestDataFixtures.CreateTrip(user, "Original");
        db.Trips.Add(trip);
        await db.SaveChangesAsync();

        var controller = BuildControllerWithUser(db, user.Id);
        var model = new Trip { Id = trip.Id, Name = "Updated" };

        var result = await controller.Edit(trip.Id, model, submitAction: "save-edit");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(TripController.Edit), redirect.ActionName);
        Assert.Equal(trip.Id, redirect.RouteValues!["id"]);
    }

    [Fact]
    public async Task Edit_Post_ReturnsView_WhenModelInvalid()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(user);
        var trip = TestDataFixtures.CreateTrip(user, "Original");
        db.Trips.Add(trip);
        await db.SaveChangesAsync();

        var controller = BuildControllerWithUser(db, user.Id);
        controller.ModelState.AddModelError("Name", "Required");
        var model = new Trip { Id = trip.Id, Name = string.Empty };

        var result = await controller.Edit(trip.Id, model, submitAction: null);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Same(model, view.Model);
        Assert.Equal("Original", db.Trips.Find(trip.Id)!.Name);
    }

    private TripController BuildController(ApplicationDbContext db)
    {
        var controller = new TripController(
            NullLogger<TripController>.Instance,
            db,
            Mock.Of<ITripMapThumbnailGenerator>(),
            Mock.Of<ITripTagService>());

        var httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());

        return controller;
    }

    private TripController BuildControllerWithUser(ApplicationDbContext db, string userId, Mock<ITripMapThumbnailGenerator>? thumbnailMock = null)
    {
        var httpContext = BuildHttpContextWithUser(userId);
        var controller = new TripController(
            NullLogger<TripController>.Instance,
            db,
            thumbnailMock?.Object ?? Mock.Of<ITripMapThumbnailGenerator>(),
            Mock.Of<ITripTagService>());

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());
        return controller;
    }
}
