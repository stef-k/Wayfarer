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
using Wayfarer.Services;
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
    public async Task Create_ValidModel_PersistsLocationAndBroadcastsToGroupChannel()
    {
        // Arrange
        var db = CreateDbContext();
        var currentUser = TestDataFixtures.CreateUser(id: "user-create", username: "alice");
        db.ApplicationUsers.Add(currentUser);

        // Create a group for the user so broadcasts happen
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Test Group",
            GroupType = "Friends",
            OwnerUserId = currentUser.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember
        {
            GroupId = group.Id,
            UserId = currentUser.Id,
            Status = GroupMember.MembershipStatuses.Active,
            JoinedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var reverseGeocoding = new ReverseGeocodingService(
            new HttpClient(new FakeHandler()),
            NullLogger<BaseApiController>.Instance);
        var sse = new TestSseService();
        var controller = new Wayfarer.Areas.User.Controllers.LocationController(
            NullLogger<BaseController>.Instance,
            db,
            reverseGeocoding,
            sse,
            Mock.Of<IPlaceVisitDetectionService>());
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

        // Verify broadcasts go to both per-user (for timeline views) and group channels
        Assert.Equal(2, sse.Messages.Count);
        Assert.Single(sse.Messages, m => m.Channel == $"location-update-{currentUser.UserName}");
        Assert.Single(sse.Messages, m => m.Channel == $"group-{group.Id}");
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
        var result = await controller.Edit(model, saveAction: null!);

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
    public void Index_ReturnsView()
    {
        // Arrange
        var db = CreateDbContext();
        var controller = BuildController(db, TestDataFixtures.CreateUser(id: "user-index", username: "index-user"));

        // Act
        var result = controller.Index();

        // Assert
        Assert.IsType<ViewResult>(result);
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
    public async Task Create_Get_ReturnsView_WithActivityTypes()
    {
        // Arrange
        var db = CreateDbContext();
        db.ActivityTypes.AddRange(
            new ActivityType { Id = 1, Name = "Walking" },
            new ActivityType { Id = 2, Name = "Running" });
        await db.SaveChangesAsync();
        var user = TestDataFixtures.CreateUser(id: "user-create-get", username: "create-user");
        var controller = BuildController(db, user);

        // Act
        var result = await controller.Create();

        // Assert
        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<AddLocationViewModel>(view.Model);
        Assert.Equal(user.Id, model.UserId);
        Assert.Equal(2, model.ActivityTypes!.Count);
        Assert.NotEqual(default, model.LocalTimestamp);
    }

    [Fact]
    public async Task Edit_Get_ReturnsView_WhenLocationExists()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner-edit-get", username: "owner");
        db.Users.Add(owner);
        db.ActivityTypes.Add(new ActivityType { Id = 1, Name = "Walking" });
        var location = CreateLocation(owner.Id, new DateTime(2024, 1, 1), "US", "CA");
        location.Id = 100;
        location.ActivityTypeId = 1;
        db.Locations.Add(location);
        await db.SaveChangesAsync();

        var controller = BuildController(db, owner);

        // Act
        var result = await controller.Edit(location.Id);

        // Assert
        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<AddLocationViewModel>(view.Model);
        Assert.Equal(location.Id, model.Id);
        Assert.Equal(location.Coordinates.Y, model.Latitude);
        Assert.Equal(location.Coordinates.X, model.Longitude);
    }

    [Fact]
    public async Task Edit_Get_RedirectsToIndex_WhenLocationNotFound()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "user-notfound", username: "user");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = BuildController(db, user);

        // Act
        var result = await controller.Edit(999);

        // Assert
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
    }

    [Fact]
    public async Task Edit_Get_RedirectsToIndex_WhenNotOwned()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner-edit", username: "owner");
        var other = TestDataFixtures.CreateUser(id: "other-edit", username: "other");
        db.Users.AddRange(owner, other);
        var location = CreateLocation(owner.Id, new DateTime(2024, 1, 1), "US", "CA");
        location.Id = 200;
        db.Locations.Add(location);
        await db.SaveChangesAsync();

        var controller = BuildController(db, other);

        // Act
        var result = await controller.Edit(location.Id);

        // Assert
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
    }

    [Fact]
    public async Task Edit_Post_RedirectsToReturnUrl_WhenSaveActionReturn()
    {
        // Arrange
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner-return", username: "owner");
        db.Users.Add(owner);
        var location = CreateLocation(owner.Id, new DateTime(2024, 1, 1), "US", "CA");
        location.Id = 300;
        db.Locations.Add(location);
        await db.SaveChangesAsync();

        var controller = BuildController(db, owner);
        var model = new AddLocationViewModel
        {
            Id = location.Id,
            Latitude = 34.0,
            Longitude = -118.0,
            LocalTimestamp = new DateTime(2024, 2, 1, 10, 0, 0, DateTimeKind.Unspecified),
            Notes = "Updated",
            SelectedActivityId = 1,
            ReturnUrl = "/User/Location/AllLocations"
        };

        // Act
        var result = await controller.Edit(model, saveAction: "return");

        // Assert
        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/User/Location/AllLocations", redirect.Url);
    }

    [Fact]
    public async Task BulkEditNotes_Get_ReturnsView_WithDropdownData()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "user-bulk-get", username: "bulk-user");
        db.Users.Add(user);
        db.Locations.AddRange(
            CreateLocation(user.Id, DateTime.UtcNow, "US", "CA", place: "LA"),
            CreateLocation(user.Id, DateTime.UtcNow, "US", "NY", place: "NYC"),
            CreateLocation(user.Id, DateTime.UtcNow, "Canada", "ON", place: "Toronto"));
        await db.SaveChangesAsync();

        var controller = BuildController(db, user);

        // Act
        var result = await controller.BulkEditNotes();

        // Assert
        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<BulkEditNotesViewModel>(view.Model);
        Assert.Equal(2, model.Countries.Count); // US, Canada
        Assert.True(model.Regions.Count >= 2); // CA, NY, ON
        Assert.True(model.Places.Count >= 3); // LA, NYC, Toronto
    }

    [Fact]
    public async Task BulkEditNotes_Post_ClearsNotes_WhenClearNotesTrue()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "user-clear", username: "clear-user");
        db.Users.Add(user);
        var loc1 = CreateLocation(user.Id, new DateTime(2024, 1, 1), "US", "CA", notes: "old note");
        var loc2 = CreateLocation(user.Id, new DateTime(2024, 1, 2), "US", "CA", notes: "another note");
        db.Locations.AddRange(loc1, loc2);
        await db.SaveChangesAsync();

        var controller = BuildController(db, user);
        var model = new BulkEditNotesViewModel
        {
            Country = "US",
            Region = "CA",
            ClearNotes = true
        };

        // Act
        var result = await controller.BulkEditNotes(model);

        // Assert
        var view = Assert.IsType<ViewResult>(result);
        var returned = Assert.IsType<BulkEditNotesViewModel>(view.Model);
        Assert.Equal(2, returned.AffectedCount);
        var cleared = db.Locations.Where(l => l.UserId == user.Id).ToList();
        Assert.All(cleared, l => Assert.Null(l.Notes));
    }

    [Fact]
    public async Task BulkEditNotes_Post_ReplacesNotes_WhenAppendFalse()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "user-replace", username: "replace-user");
        db.Users.Add(user);
        var loc = CreateLocation(user.Id, new DateTime(2024, 1, 1), "US", "CA", notes: "old");
        db.Locations.Add(loc);
        await db.SaveChangesAsync();

        var controller = BuildController(db, user);
        var model = new BulkEditNotesViewModel
        {
            Country = "US",
            Region = "CA",
            Append = false,
            Notes = "new note"
        };

        // Act
        var result = await controller.BulkEditNotes(model);

        // Assert
        var view = Assert.IsType<ViewResult>(result);
        var returned = Assert.IsType<BulkEditNotesViewModel>(view.Model);
        Assert.Equal(1, returned.AffectedCount);
        var updated = db.Locations.Single(l => l.UserId == user.Id);
        Assert.Equal("new note", updated.Notes);
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
        var result = await controller.Edit(model, saveAction: null!);

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
            Mock.Of<SseService>(),
            Mock.Of<IPlaceVisitDetectionService>());
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
