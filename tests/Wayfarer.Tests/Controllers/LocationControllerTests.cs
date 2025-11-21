using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
    /// Tests for <see cref="LocationController"/> covering CRUD utilities.
    /// </summary>
    public class LocationControllerTests : TestBase
    {
        [Fact]
        public async Task BulkEditNotes_AppendsNotes_ForFilteredLocations()
        {
            // Arrange
            var db = CreateDbContext();
            var user = TestDataFixtures.CreateUser(id: "user-bulk", username: "bulk-user");
            var other = TestDataFixtures.CreateUser(id: "other", username: "other-user");
            db.Users.AddRange(user, other);

            db.Locations.AddRange(
                CreateLocation(user.Id, new DateTime(2024, 6, 1), "US", "CA", "first"),
                CreateLocation(user.Id, new DateTime(2024, 6, 2), "US", "CA", "second"),
                CreateLocation(user.Id, new DateTime(2024, 6, 3), "US", "NV", place: "skip", notes: "skip"),
                CreateLocation(other.Id, new DateTime(2024, 6, 1), "US", "CA", place: "other", notes: "other"));
            await db.SaveChangesAsync();

            var controller = BuildController(db, user);
            var model = new BulkEditNotesViewModel
            {
                Country = "US",
                Region = "CA",
                Append = true,
                Notes = " appended",
                FromDate = new DateTime(2024, 6, 1),
                ToDate = new DateTime(2024, 6, 30)
            };

            // Act
            var result = await controller.BulkEditNotes(model);

            // Assert
            var view = Assert.IsType<ViewResult>(result);
            var returned = Assert.IsType<BulkEditNotesViewModel>(view.Model);
            Assert.Equal(2, returned.AffectedCount);

            var updated = db.Locations.Where(l => l.UserId == user.Id && l.Region == "CA").ToList();
            Assert.All(updated, l => Assert.EndsWith(" appended", l.Notes));

            var untouched = db.Locations.Single(l => l.Place == "skip");
            Assert.Equal("skip", untouched.Notes);
            var otherUserLoc = db.Locations.Single(l => l.UserId == other.Id);
            Assert.Equal("other", otherUserLoc.Notes);
        }

        [Fact]
    public async Task Create_ValidModel_PersistsLocationAndBroadcasts()
    {
        // Arrange
        var db = CreateDbContext();
        var currentUser = TestDataFixtures.CreateUser(id: "user-create", username: "alice");
        db.ApplicationUsers.Add(currentUser);
        await db.SaveChangesAsync();

        var reverseGeocoding = new ReverseGeocodingService(
            new HttpClient(new FakeHandler()),
            NullLogger<BaseApiController>.Instance);
        var sse = new TestSseService();
        var controller = new Wayfarer.Areas.User.Controllers.LocationController(
            NullLogger<BaseController>.Instance,
            db,
            reverseGeocoding,
            sse);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, currentUser.Id),
                    new Claim(ClaimTypes.Name, currentUser.UserName!)
                }, "TestAuth"))
            }
        };

        var httpContext = controller.ControllerContext.HttpContext;
        controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());

        var model = new AddLocationViewModel
        {
            Latitude = 10,
            Longitude = 20,
            LocalTimestamp = new DateTime(2024, 6, 1, 8, 0, 0, DateTimeKind.Unspecified),
            Notes = "Test note"
        };

        // Act
        var result = await controller.Create(model);

        // Assert
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Edit", redirect.ActionName);

        var saved = await db.Locations.SingleAsync(l => l.UserId == currentUser.Id);
        Assert.Equal(model.Latitude, saved.Coordinates.Y);
        Assert.Equal(model.Longitude, saved.Coordinates.X);
        Assert.Equal("Test note", saved.Notes);
        var message = Assert.Single(sse.Messages);
        Assert.Equal($"location-update-{currentUser.UserName}", message.Channel);
    }

    [Fact]
    public async Task Edit_RejectsEditsFromDifferentUser()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner", username: "owner");
        var intruder = TestDataFixtures.CreateUser(id: "intruder", username: "intruder");
        db.Users.AddRange(owner, intruder);
        var location = CreateLocation(owner.Id, new DateTime(2024, 1, 1), "US", "CA");
        location.Id = 77;
        db.Locations.Add(location);
        await db.SaveChangesAsync();

        var controller = BuildController(db, intruder);
        var model = new AddLocationViewModel
        {
            Id = location.Id,
            Latitude = 1,
            Longitude = 1,
            LocalTimestamp = new DateTime(2024, 3, 1),
            Notes = "should not update"
        };

        // Act
        var result = await controller.Edit(model, saveAction: null);

        // Assert
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        var untouched = await db.Locations.SingleAsync(l => l.Id == location.Id);
        Assert.Equal(location.Notes, untouched.Notes);
        Assert.Equal(location.Coordinates.X, untouched.Coordinates.X);
        Assert.Equal(location.Coordinates.Y, untouched.Coordinates.Y);
    }

    [Fact]
    public async Task GetRegions_ReturnsDistinctRegionsForUser()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "user-region", username: "region-user");
        var other = TestDataFixtures.CreateUser(id: "other-region", username: "other-user");
        db.Users.AddRange(user, other);
        db.Locations.AddRange(
            CreateLocation(user.Id, DateTime.UtcNow, "US", "CA"),
            CreateLocation(user.Id, DateTime.UtcNow, "US", "OR"),
            CreateLocation(other.Id, DateTime.UtcNow, "US", "TX"));
        await db.SaveChangesAsync();

        var controller = BuildController(db, user);

        // Act
        var result = await controller.GetRegions("US");

        // Assert
        var json = Assert.IsType<JsonResult>(result);
        var regions = Assert.IsAssignableFrom<IEnumerable<string>>(json.Value!);
        Assert.Equal(new[] { "CA", "OR" }, regions.OrderBy(r => r));
        Assert.DoesNotContain("TX", regions);
    }

    [Fact]
    public async Task GetPlaces_ReturnsDistinctPlacesForUserCountryAndRegion()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "user-place", username: "place-user");
        var other = TestDataFixtures.CreateUser(id: "other-place", username: "other-user");
        db.Users.AddRange(user, other);
        db.Locations.AddRange(
            CreateLocation(user.Id, DateTime.UtcNow, "US", "CA", place: "San Francisco"),
            CreateLocation(user.Id, DateTime.UtcNow, "US", "CA", place: "Los Angeles"),
            CreateLocation(user.Id, DateTime.UtcNow, "US", "OR", place: "Portland"),
            CreateLocation(other.Id, DateTime.UtcNow, "US", "CA", place: "Other City"));
        await db.SaveChangesAsync();

        var controller = BuildController(db, user);

        // Act
        var result = await controller.GetPlaces("US", "CA");

        // Assert
        var json = Assert.IsType<JsonResult>(result);
        var places = Assert.IsAssignableFrom<IEnumerable<string>>(json.Value!);
        Assert.Equal(new[] { "Los Angeles", "San Francisco" }, places.OrderBy(p => p));
        Assert.DoesNotContain("Other City", places);
        Assert.DoesNotContain("Portland", places);
    }

    [Fact]
    public void AllLocations_ReturnsView()
    {
        // Arrange
        var db = CreateDbContext();
        var controller = BuildController(db, TestDataFixtures.CreateUser(id: "user-view", username: "view-user"));

        // Act
        var result = controller.AllLocations();

        // Assert
        Assert.IsType<ViewResult>(result);
    }

    [Fact]
    public async Task Edit_UpdatesExistingLocationForOwner()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner", username: "owner");
        db.Users.Add(owner);
        var location = CreateLocation(owner.Id, new DateTime(2024, 1, 1), "US", "CA");
        location.Id = 42;
        db.Locations.Add(location);
        await db.SaveChangesAsync();

        var controller = BuildController(db, owner);

        var model = new AddLocationViewModel
        {
            Id = location.Id,
            Latitude = 33.1,
            Longitude = -117.2,
            LocalTimestamp = new DateTime(2024, 2, 1, 12, 0, 0, DateTimeKind.Unspecified),
            Notes = "Updated",
            SelectedActivityId = 5,
            ReturnUrl = "/User/Location"
        };

        // Act
        var result = await controller.Edit(model, saveAction: null);

        // Assert
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Edit", redirect.ActionName);
        var updated = await db.Locations.SingleAsync(l => l.Id == location.Id);
        Assert.Equal(model.Latitude, updated.Coordinates.Y);
        Assert.Equal(model.Longitude, updated.Coordinates.X);
        Assert.Equal("Updated", updated.Notes);
        Assert.Equal(5, updated.ActivityTypeId);
    }

    [Fact]
    public async Task PreviewCount_FiltersByUserAndDateRange()
    {
        // Arrange
        var db = CreateDbContext();
        var currentUser = TestDataFixtures.CreateUser(id: "user-current");
        var otherUser = TestDataFixtures.CreateUser(id: "user-other");
        db.Users.AddRange(currentUser, otherUser);

        db.Locations.AddRange(
            CreateLocation(currentUser.Id, new DateTime(2024, 6, 1), "US", "CA"),
            CreateLocation(currentUser.Id, new DateTime(2024, 6, 15), "US", "CA"),
            CreateLocation(currentUser.Id, new DateTime(2024, 7, 1), "US", "NV"),
            CreateLocation(otherUser.Id, new DateTime(2024, 6, 10), "US", "CA"));
        await db.SaveChangesAsync();

        var reverseGeocoding = new ReverseGeocodingService(
            new HttpClient(new FakeHandler()),
            NullLogger<BaseApiController>.Instance);
        var controller = BuildController(db, currentUser);

        // Act
        var result = await controller.PreviewCount(
            country: "US",
            region: "CA",
            place: null,
            fromDate: new DateTime(2024, 6, 1),
            toDate: new DateTime(2024, 6, 30));

        // Assert
        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.Equal(2, jsonResult.Value);
    }

    private static Wayfarer.Areas.User.Controllers.LocationController BuildController(ApplicationDbContext db, ApplicationUser user)
    {
        var reverseGeocoding = new ReverseGeocodingService(
            new HttpClient(new FakeHandler()),
            NullLogger<BaseApiController>.Instance);
        var controller = new Wayfarer.Areas.User.Controllers.LocationController(
            NullLogger<BaseController>.Instance,
            db,
            reverseGeocoding,
            Mock.Of<SseService>());
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id),
                new Claim(ClaimTypes.Name, user.UserName!)
            }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());
        var url = new Mock<IUrlHelper>();
        url.Setup(u => u.IsLocalUrl(It.IsAny<string>())).Returns(true);
        url.Setup(u => u.Action(It.IsAny<UrlActionContext>())).Returns("/User/Location");
        controller.Url = url.Object;
        return controller;
    }

    private static Wayfarer.Models.Location CreateLocation(string userId, DateTime timestampUtc, string country, string region, string? place = null, string? notes = null)
    {
        return new Wayfarer.Models.Location
        {
            UserId = userId,
            Coordinates = new Point(0, 0) { SRID = 4326 },
            Timestamp = timestampUtc,
            LocalTimestamp = timestampUtc,
            TimeZoneId = "UTC",
            Country = country,
            Region = region,
            Place = place ?? "Test Place",
            Notes = notes
        };
    }
    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"features\":[]}")
            });
        }
    }

    private sealed class TestSseService : SseService
    {
        public List<(string Channel, string Data)> Messages { get; } = new();

        public override Task BroadcastAsync(string channel, string data)
        {
            Messages.Add((channel, data));
            return Task.CompletedTask;
        }
    }
}
