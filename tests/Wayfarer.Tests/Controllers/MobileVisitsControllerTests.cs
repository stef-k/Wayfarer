using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Tests for the MobileVisitsController which provides visit polling for mobile clients.
/// </summary>
public class MobileVisitsControllerTests : TestBase
{
    #region Authentication Tests

    [Fact]
    public async Task GetRecentVisits_ReturnsUnauthorized_WhenNoToken()
    {
        // Arrange
        var (_, controller) = CreateController();

        // Act
        var result = await controller.GetRecentVisitsAsync();

        // Assert
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task GetRecentVisits_ReturnsUnauthorized_WhenInvalidToken()
    {
        // Arrange
        var (db, controller) = CreateController("invalid-token");
        var user = TestDataFixtures.CreateUser(id: "user1");
        db.Users.Add(user);
        db.ApiTokens.Add(TestDataFixtures.CreateApiToken(user, "valid-token"));
        await db.SaveChangesAsync();

        // Act
        var result = await controller.GetRecentVisitsAsync();

        // Assert
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    #endregion

    #region Basic Query Tests

    [Fact]
    public async Task GetRecentVisits_ReturnsEmptyList_WhenNoVisits()
    {
        // Arrange
        var (db, controller) = CreateController("token");
        var user = TestDataFixtures.CreateUser(id: "user1");
        db.Users.Add(user);
        db.ApiTokens.Add(TestDataFixtures.CreateApiToken(user, "token"));
        await db.SaveChangesAsync();

        // Act
        var result = await controller.GetRecentVisitsAsync();

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RecentVisitsResponse>(ok.Value);
        Assert.True(response.Success);
        Assert.Empty(response.Visits);
    }

    [Fact]
    public async Task GetRecentVisits_ReturnsRecentVisits_WithinTimeWindow()
    {
        // Arrange
        var (db, controller) = CreateController("token");
        var user = TestDataFixtures.CreateUser(id: "user1");
        db.Users.Add(user);
        db.ApiTokens.Add(TestDataFixtures.CreateApiToken(user, "token"));

        // Create a visit confirmed 10 seconds ago (within default 30s window)
        var recentVisit = CreateVisit(user.Id, "Recent Place", lastSeenAtUtc: DateTime.UtcNow.AddSeconds(-10));
        db.PlaceVisitEvents.Add(recentVisit);
        await db.SaveChangesAsync();

        // Act
        var result = await controller.GetRecentVisitsAsync();

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RecentVisitsResponse>(ok.Value);
        Assert.True(response.Success);
        Assert.Single(response.Visits);
        Assert.Equal("Recent Place", response.Visits[0].PlaceName);
    }

    [Fact]
    public async Task GetRecentVisits_ExcludesOldVisits_OutsideTimeWindow()
    {
        // Arrange
        var (db, controller) = CreateController("token");
        var user = TestDataFixtures.CreateUser(id: "user1");
        db.Users.Add(user);
        db.ApiTokens.Add(TestDataFixtures.CreateApiToken(user, "token"));

        // Create a visit confirmed 60 seconds ago (outside default 30s window)
        var oldVisit = CreateVisit(user.Id, "Old Place", lastSeenAtUtc: DateTime.UtcNow.AddSeconds(-60));
        db.PlaceVisitEvents.Add(oldVisit);
        await db.SaveChangesAsync();

        // Act
        var result = await controller.GetRecentVisitsAsync(since: 30);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RecentVisitsResponse>(ok.Value);
        Assert.True(response.Success);
        Assert.Empty(response.Visits);
    }

    [Fact]
    public async Task GetRecentVisits_RespectsCustomSinceParameter()
    {
        // Arrange
        var (db, controller) = CreateController("token");
        var user = TestDataFixtures.CreateUser(id: "user1");
        db.Users.Add(user);
        db.ApiTokens.Add(TestDataFixtures.CreateApiToken(user, "token"));

        // Create a visit confirmed 90 seconds ago
        var visit = CreateVisit(user.Id, "Place", lastSeenAtUtc: DateTime.UtcNow.AddSeconds(-90));
        db.PlaceVisitEvents.Add(visit);
        await db.SaveChangesAsync();

        // Act - with 60s window, should NOT include it
        var result60 = await controller.GetRecentVisitsAsync(since: 60);
        var response60 = Assert.IsType<RecentVisitsResponse>(Assert.IsType<OkObjectResult>(result60).Value);
        Assert.Empty(response60.Visits);

        // Act - with 120s window, SHOULD include it
        var result120 = await controller.GetRecentVisitsAsync(since: 120);
        var response120 = Assert.IsType<RecentVisitsResponse>(Assert.IsType<OkObjectResult>(result120).Value);
        Assert.Single(response120.Visits);
    }

    #endregion

    #region User Isolation Tests

    [Fact]
    public async Task GetRecentVisits_OnlyReturnsOwnVisits()
    {
        // Arrange
        var (db, controller) = CreateController("token");
        var user1 = TestDataFixtures.CreateUser(id: "user1");
        var user2 = TestDataFixtures.CreateUser(id: "user2");
        db.Users.AddRange(user1, user2);
        db.ApiTokens.Add(TestDataFixtures.CreateApiToken(user1, "token"));

        // Create visits for both users
        db.PlaceVisitEvents.AddRange(
            CreateVisit(user1.Id, "My Place", lastSeenAtUtc: DateTime.UtcNow.AddSeconds(-5)),
            CreateVisit(user2.Id, "Other Place", lastSeenAtUtc: DateTime.UtcNow.AddSeconds(-5)));
        await db.SaveChangesAsync();

        // Act
        var result = await controller.GetRecentVisitsAsync();

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RecentVisitsResponse>(ok.Value);
        Assert.Single(response.Visits);
        Assert.Equal("My Place", response.Visits[0].PlaceName);
    }

    #endregion

    #region Parameter Clamping Tests

    [Fact]
    public async Task GetRecentVisits_ClampsSinceToMaximum()
    {
        // Arrange
        var (db, controller) = CreateController("token");
        var user = TestDataFixtures.CreateUser(id: "user1");
        db.Users.Add(user);
        db.ApiTokens.Add(TestDataFixtures.CreateApiToken(user, "token"));

        // Create a visit confirmed 400 seconds ago (beyond max 300s)
        var oldVisit = CreateVisit(user.Id, "Very Old Place", lastSeenAtUtc: DateTime.UtcNow.AddSeconds(-400));
        db.PlaceVisitEvents.Add(oldVisit);
        await db.SaveChangesAsync();

        // Act - request 600s but should be clamped to 300s
        var result = await controller.GetRecentVisitsAsync(since: 600);

        // Assert - visit should NOT be included due to clamping
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RecentVisitsResponse>(ok.Value);
        Assert.Empty(response.Visits);
    }

    [Fact]
    public async Task GetRecentVisits_ClampsSinceToMinimum()
    {
        // Arrange
        var (db, controller) = CreateController("token");
        var user = TestDataFixtures.CreateUser(id: "user1");
        db.Users.Add(user);
        db.ApiTokens.Add(TestDataFixtures.CreateApiToken(user, "token"));
        await db.SaveChangesAsync();

        // Act - request negative value, should be clamped to 1
        var result = await controller.GetRecentVisitsAsync(since: -10);

        // Assert - should succeed without error
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RecentVisitsResponse>(ok.Value);
        Assert.True(response.Success);
    }

    #endregion

    #region DTO Mapping Tests

    [Fact]
    public async Task GetRecentVisits_ReturnsCorrectDtoFields()
    {
        // Arrange
        var (db, controller) = CreateController("token");
        var user = TestDataFixtures.CreateUser(id: "user1");
        db.Users.Add(user);
        db.ApiTokens.Add(TestDataFixtures.CreateApiToken(user, "token"));

        var tripId = Guid.NewGuid();
        var placeId = Guid.NewGuid();
        var arrivedAt = DateTime.UtcNow.AddSeconds(-5);
        var visit = new PlaceVisitEvent
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PlaceId = placeId,
            PlaceNameSnapshot = "Coffee Shop",
            TripIdSnapshot = tripId,
            TripNameSnapshot = "NYC Trip",
            RegionNameSnapshot = "Manhattan",
            PlaceLocationSnapshot = new Point(-74.0060, 40.7128) { SRID = 4326 },
            ArrivedAtUtc = arrivedAt,
            LastSeenAtUtc = arrivedAt,
            IconNameSnapshot = "coffee",
            MarkerColorSnapshot = "#8B4513"
        };
        db.PlaceVisitEvents.Add(visit);
        await db.SaveChangesAsync();

        // Act
        var result = await controller.GetRecentVisitsAsync();

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RecentVisitsResponse>(ok.Value);
        Assert.Single(response.Visits);

        var dto = response.Visits[0];
        Assert.Equal("visit_started", dto.Type);
        Assert.Equal(visit.Id, dto.VisitId);
        Assert.Equal(tripId, dto.TripId);
        Assert.Equal("NYC Trip", dto.TripName);
        Assert.Equal(placeId, dto.PlaceId);
        Assert.Equal("Coffee Shop", dto.PlaceName);
        Assert.Equal("Manhattan", dto.RegionName);
        Assert.Equal(arrivedAt, dto.ArrivedAtUtc);
        Assert.Equal(40.7128, dto.Latitude);
        Assert.Equal(-74.0060, dto.Longitude);
        Assert.Equal("coffee", dto.IconName);
        Assert.Equal("#8B4513", dto.MarkerColor);
    }

    [Fact]
    public async Task GetRecentVisits_IncludesVisit_WhenArrivedAtIsOldButLastSeenAtIsRecent()
    {
        // Arrange - This tests the reviewer's scenario:
        // Visit first hit (ArrivedAtUtc) was 2 minutes ago, but confirmation (LastSeenAtUtc) was 5 seconds ago
        var (db, controller) = CreateController("token");
        var user = TestDataFixtures.CreateUser(id: "user1");
        db.Users.Add(user);
        db.ApiTokens.Add(TestDataFixtures.CreateApiToken(user, "token"));

        // ArrivedAtUtc is 2 minutes old (outside 30s window), but LastSeenAtUtc is 5 seconds ago (within window)
        var visit = CreateVisit(
            user.Id,
            "Delayed Confirmation Place",
            lastSeenAtUtc: DateTime.UtcNow.AddSeconds(-5),
            arrivedAtUtc: DateTime.UtcNow.AddMinutes(-2));
        db.PlaceVisitEvents.Add(visit);
        await db.SaveChangesAsync();

        // Act - with default 30s window
        var result = await controller.GetRecentVisitsAsync(since: 30);

        // Assert - should be included because LastSeenAtUtc (confirmation time) is within window
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RecentVisitsResponse>(ok.Value);
        Assert.Single(response.Visits);
        Assert.Equal("Delayed Confirmation Place", response.Visits[0].PlaceName);
    }

    #endregion

    #region Ordering Tests

    [Fact]
    public async Task GetRecentVisits_ReturnsVisitsOrderedByLastSeenAtDescending()
    {
        // Arrange
        var (db, controller) = CreateController("token");
        var user = TestDataFixtures.CreateUser(id: "user1");
        db.Users.Add(user);
        db.ApiTokens.Add(TestDataFixtures.CreateApiToken(user, "token"));

        db.PlaceVisitEvents.AddRange(
            CreateVisit(user.Id, "First", lastSeenAtUtc: DateTime.UtcNow.AddSeconds(-20)),
            CreateVisit(user.Id, "Third", lastSeenAtUtc: DateTime.UtcNow.AddSeconds(-5)),
            CreateVisit(user.Id, "Second", lastSeenAtUtc: DateTime.UtcNow.AddSeconds(-10)));
        await db.SaveChangesAsync();

        // Act
        var result = await controller.GetRecentVisitsAsync();

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RecentVisitsResponse>(ok.Value);
        Assert.Equal(3, response.Visits.Count);
        Assert.Equal("Third", response.Visits[0].PlaceName);
        Assert.Equal("Second", response.Visits[1].PlaceName);
        Assert.Equal("First", response.Visits[2].PlaceName);
    }

    #endregion

    #region Helper Methods

    private (ApplicationDbContext Db, MobileVisitsController Controller) CreateController(string? token = null)
    {
        var db = CreateDbContext();
        var httpContext = new DefaultHttpContext();

        if (!string.IsNullOrWhiteSpace(token))
        {
            httpContext.Request.Headers["Authorization"] = $"Bearer {token}";
        }

        var accessor = new MobileCurrentUserAccessor(
            new HttpContextAccessor { HttpContext = httpContext },
            db,
            NullLogger<MobileCurrentUserAccessor>.Instance);

        var controller = new MobileVisitsController(
            db,
            NullLogger<BaseApiController>.Instance,
            accessor)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        return (db, controller);
    }

    private static PlaceVisitEvent CreateVisit(
        string userId,
        string placeName,
        DateTime lastSeenAtUtc,
        DateTime? arrivedAtUtc = null,
        Guid? tripId = null)
    {
        var tid = tripId ?? Guid.NewGuid();
        // ArrivedAtUtc defaults to LastSeenAtUtc if not specified
        var arrived = arrivedAtUtc ?? lastSeenAtUtc;
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
            ArrivedAtUtc = arrived,
            LastSeenAtUtc = lastSeenAtUtc,
            IconNameSnapshot = "marker",
            MarkerColorSnapshot = "bg-blue"
        };
    }

    #endregion
}
