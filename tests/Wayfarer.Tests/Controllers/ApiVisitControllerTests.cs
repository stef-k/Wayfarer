using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Tests for the API area VisitController.
/// </summary>
public class ApiVisitControllerTests : TestBase
{
    [Fact]
    public async Task Search_ReturnsUnauthorized_WhenNotAuthenticated()
    {
        var db = CreateDbContext();
        var controller = BuildController(db, null);

        var result = await controller.Search();

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Search_ReturnsOk_WhenAuthenticated()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1"));
        db.SaveChanges();
        var controller = BuildController(db, "u1");

        var result = await controller.Search();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        var success = ok.Value.GetType().GetProperty("success")?.GetValue(ok.Value);
        Assert.Equal(true, success);
    }

    [Fact]
    public async Task Search_ReturnsOnlyUserVisits()
    {
        var db = CreateDbContext();
        db.Users.AddRange(
            TestDataFixtures.CreateUser(id: "u1"),
            TestDataFixtures.CreateUser(id: "u2"));
        db.PlaceVisitEvents.AddRange(
            CreateVisit("u1", "User1 Place 1"),
            CreateVisit("u1", "User1 Place 2"),
            CreateVisit("u2", "User2 Place"));
        db.SaveChanges();
        var controller = BuildController(db, "u1");

        var result = await controller.Search();

        var ok = Assert.IsType<OkObjectResult>(result);
        var totalItems = (int?)ok.Value?.GetType().GetProperty("totalItems")?.GetValue(ok.Value);
        Assert.Equal(2, totalItems);
    }

    [Fact]
    public async Task Search_FiltersByStatus_Open()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1"));
        var openVisit = CreateVisit("u1", "Open");
        openVisit.EndedAtUtc = null;
        var closedVisit = CreateVisit("u1", "Closed");
        closedVisit.EndedAtUtc = DateTime.UtcNow;
        db.PlaceVisitEvents.AddRange(openVisit, closedVisit);
        db.SaveChanges();
        var controller = BuildController(db, "u1");

        var result = await controller.Search(status: "open");

        var ok = Assert.IsType<OkObjectResult>(result);
        var totalItems = (int?)ok.Value?.GetType().GetProperty("totalItems")?.GetValue(ok.Value);
        Assert.Equal(1, totalItems);
    }

    [Fact]
    public async Task Search_FiltersByStatus_Closed()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1"));
        var openVisit = CreateVisit("u1", "Open");
        openVisit.EndedAtUtc = null;
        var closedVisit = CreateVisit("u1", "Closed");
        closedVisit.EndedAtUtc = DateTime.UtcNow;
        db.PlaceVisitEvents.AddRange(openVisit, closedVisit);
        db.SaveChanges();
        var controller = BuildController(db, "u1");

        var result = await controller.Search(status: "closed");

        var ok = Assert.IsType<OkObjectResult>(result);
        var totalItems = (int?)ok.Value?.GetType().GetProperty("totalItems")?.GetValue(ok.Value);
        Assert.Equal(1, totalItems);
    }

    [Fact]
    public async Task Search_FiltersByTripId()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1"));
        var tripId = Guid.NewGuid();
        db.PlaceVisitEvents.AddRange(
            CreateVisit("u1", "Place 1", tripId),
            CreateVisit("u1", "Place 2", tripId),
            CreateVisit("u1", "Other Trip Place", Guid.NewGuid()));
        db.SaveChanges();
        var controller = BuildController(db, "u1");

        var result = await controller.Search(tripId: tripId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var totalItems = (int?)ok.Value?.GetType().GetProperty("totalItems")?.GetValue(ok.Value);
        Assert.Equal(2, totalItems);
    }

    [Fact]
    public async Task GetTripsWithVisits_ReturnsOk()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1"));
        db.PlaceVisitEvents.Add(CreateVisit("u1", "Place 1"));
        db.SaveChanges();
        var controller = BuildController(db, "u1");

        var result = await controller.GetTripsWithVisits();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task GetTripsWithVisits_ReturnsUnauthorized_WhenNotAuthenticated()
    {
        var db = CreateDbContext();
        var controller = BuildController(db, null);

        var result = await controller.GetTripsWithVisits();

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task BulkDelete_RemovesVisits()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1"));
        var visit1 = CreateVisit("u1", "Visit 1");
        var visit2 = CreateVisit("u1", "Visit 2");
        db.PlaceVisitEvents.AddRange(visit1, visit2);
        db.SaveChanges();
        var controller = BuildController(db, "u1");

        var request = new VisitController.BulkDeleteRequest
        {
            VisitIds = new[] { visit1.Id, visit2.Id }
        };
        var result = await controller.BulkDelete(request);

        var ok = Assert.IsType<OkObjectResult>(result);
        var success = ok.Value?.GetType().GetProperty("success")?.GetValue(ok.Value);
        Assert.Equal(true, success);
        Assert.Equal(0, db.PlaceVisitEvents.Count());
    }

    [Fact]
    public async Task BulkDelete_OnlyDeletesOwnedVisits()
    {
        var db = CreateDbContext();
        db.Users.AddRange(
            TestDataFixtures.CreateUser(id: "u1"),
            TestDataFixtures.CreateUser(id: "u2"));
        var myVisit = CreateVisit("u1", "My Visit");
        var otherVisit = CreateVisit("u2", "Other Visit");
        db.PlaceVisitEvents.AddRange(myVisit, otherVisit);
        db.SaveChanges();
        var controller = BuildController(db, "u1");

        var request = new VisitController.BulkDeleteRequest
        {
            VisitIds = new[] { myVisit.Id, otherVisit.Id }
        };
        var result = await controller.BulkDelete(request);

        var ok = Assert.IsType<OkObjectResult>(result);
        var deletedCount = (int?)ok.Value?.GetType().GetProperty("deletedCount")?.GetValue(ok.Value);
        Assert.Equal(1, deletedCount);
        Assert.NotNull(db.PlaceVisitEvents.Find(otherVisit.Id));
    }

    [Fact]
    public async Task BulkDelete_ReturnsBadRequest_WhenNoIds()
    {
        var db = CreateDbContext();
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1"));
        db.SaveChanges();
        var controller = BuildController(db, "u1");

        var request = new VisitController.BulkDeleteRequest { VisitIds = Array.Empty<Guid>() };
        var result = await controller.BulkDelete(request);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task BulkDelete_ReturnsUnauthorized_WhenNotAuthenticated()
    {
        var db = CreateDbContext();
        var controller = BuildController(db, null);

        var request = new VisitController.BulkDeleteRequest { VisitIds = new[] { Guid.NewGuid() } };
        var result = await controller.BulkDelete(request);

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    private VisitController BuildController(ApplicationDbContext db, string? userId)
    {
        var controller = new VisitController(db, NullLogger<BaseApiController>.Instance);

        var httpContext = new DefaultHttpContext();
        if (userId != null)
        {
            httpContext.User = CreateUserPrincipal(userId);
        }

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        return controller;
    }

    private static PlaceVisitEvent CreateVisit(
        string userId,
        string placeName,
        Guid? tripId = null)
    {
        var tid = tripId ?? Guid.NewGuid();
        return new PlaceVisitEvent
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PlaceId = Guid.NewGuid(),
            PlaceNameSnapshot = placeName,
            TripIdSnapshot = tid,
            TripNameSnapshot = "Test Trip",
            RegionNameSnapshot = "Test Region",
            PlaceLocationSnapshot = new Point(-74.0, 40.0) { SRID = 4326 },
            ArrivedAtUtc = DateTime.UtcNow.AddHours(-1),
            LastSeenAtUtc = DateTime.UtcNow,
            IconNameSnapshot = "marker",
            MarkerColorSnapshot = "bg-blue"
        };
    }
}
