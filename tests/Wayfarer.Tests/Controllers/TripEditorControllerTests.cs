using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Focused tests for the read-only Trip Editor API spike.
/// </summary>
public sealed class TripEditorControllerTests : TestBase
{
    [Fact]
    public async Task GetEditorStateWithoutUserReturnsUnauthorized()
    {
        using var db = CreateDbContext();
        var controller = new TripEditorController(db, BuildEnvironment());

        var result = await controller.GetEditorState(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task GetEditorStateForNonOwnerReturnsForbidden()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = new TripEditorController(db, BuildEnvironment());
        ConfigureControllerWithUserRole(controller, "other-user");

        var result = await controller.GetEditorState(trip.Id, CancellationToken.None);

        AssertForbiddenStatus(result);
    }

    [Fact]
    public async Task GetEditorStateWithoutUserRoleReturnsForbidden()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = new TripEditorController(db, BuildEnvironment());
        ConfigureControllerWithUserRole(controller, "owner-user", "Manager");

        var result = await controller.GetEditorState(trip.Id, CancellationToken.None);

        AssertForbiddenStatus(result);
    }

    [Fact]
    public async Task GetEditorStateForMissingOwnedScopeTripReturnsNotFound()
    {
        using var db = CreateDbContext();
        var controller = new TripEditorController(db, BuildEnvironment());
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await controller.GetEditorState(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetEditorStateForOwnerReturnsNormalizedMinimumShape()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = new TripEditorController(db, BuildEnvironment());
        ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await controller.GetEditorState(trip.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var state = Assert.IsType<EditorTripStateDto>(ok.Value);
        Assert.Equal(trip.Id, state.TripId);
        Assert.Equal(trip.Name, state.Metadata.Name);
        Assert.Single(state.RegionOrder);
        Assert.Single(state.RegionsById);
        Assert.Single(state.PlacesById);
        Assert.Single(state.AreasById);
        Assert.Single(state.SegmentsById);
        Assert.Single(state.TagsBySlug);
        Assert.Equal(1, state.VisitProgress.TotalPlaces);
        Assert.Equal(1, state.VisitProgress.VisitedPlaces);
        Assert.NotNull(state.AreasById.Values.Single().Geometry);
        Assert.NotNull(state.SegmentsById.Values.Single().Route);
        Assert.NotNull(state.Options);
        Assert.True(state.Permissions.CanEditTrip);
    }

    private static void ConfigureControllerWithUserRole(ControllerBase controller, string userId, string role = "User")
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = BuildHttpContextWithUser(userId, role)
        };
    }

    private static void AssertForbiddenStatus(IActionResult result)
    {
        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }

    private static IWebHostEnvironment BuildEnvironment()
    {
        var mock = new Mock<IWebHostEnvironment>();
        mock.SetupGet(e => e.WebRootPath).Returns(Path.GetTempPath());
        return mock.Object;
    }

    private static Trip SeedTrip(ApplicationDbContext db, string userId)
    {
        var factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Test Trip",
            UpdatedAt = DateTime.UtcNow,
            IsPublic = false,
            ShareProgressEnabled = false,
            Tags = new List<Tag> { new() { Id = Guid.NewGuid(), Name = "Museum", Slug = "museum" } }
        };
        var region = new Region
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = userId,
            Name = "Athens",
            DisplayOrder = 1,
            Center = factory.CreatePoint(new Coordinate(23.7275, 37.9838))
        };
        var place = new Place
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RegionId = region.Id,
            Name = "Acropolis",
            Location = factory.CreatePoint(new Coordinate(23.7261, 37.9715)),
            DisplayOrder = 1,
            IconName = "marker",
            MarkerColor = "bg-blue"
        };
        var area = new Area
        {
            Id = Guid.NewGuid(),
            RegionId = region.Id,
            Name = "Center",
            DisplayOrder = 1,
            FillHex = "#ff6600",
            Geometry = factory.CreatePolygon(new[]
            {
                new Coordinate(23.72, 37.97),
                new Coordinate(23.73, 37.97),
                new Coordinate(23.73, 37.98),
                new Coordinate(23.72, 37.97)
            })
        };
        var segment = new Segment
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            UserId = userId,
            FromPlaceId = place.Id,
            ToPlaceId = place.Id,
            Mode = "walk",
            DisplayOrder = 1,
            RouteGeometry = factory.CreateLineString(new[]
            {
                new Coordinate(23.7261, 37.9715),
                new Coordinate(23.7275, 37.9838)
            })
        };
        var visit = new PlaceVisitEvent
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PlaceId = place.Id,
            ArrivedAtUtc = DateTime.UtcNow.AddHours(-1),
            EndedAtUtc = DateTime.UtcNow
        };

        trip.Regions.Add(region);
        trip.Segments.Add(segment);
        region.Places.Add(place);
        region.Areas.Add(area);

        db.Trips.Add(trip);
        db.PlaceVisitEvents.Add(visit);
        db.SaveChanges();
        return trip;
    }
}
