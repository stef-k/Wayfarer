using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// API Trips controller high-ROI paths.
/// </summary>
public class ApiTripsControllerTests : TestBase
{
    [Fact]
    public void GetUserTrips_ReturnsUnauthorized_WhenNoToken()
    {
        var controller = BuildController(CreateDbContext());

        var result = controller.GetUserTrips();

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal("Missing or invalid API token.", unauthorized.Value);
    }

    [Fact]
    public void GetUserTrips_ReturnsTrips_ForOwner()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { Id = 1, Token = "tok", UserId = user.Id, Name = "test", User = user });
        db.Trips.Add(new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip1", UpdatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var controller = BuildController(db);
        controller.ControllerContext.HttpContext.Request.Headers["Authorization"] = "Bearer tok";

        var result = controller.GetUserTrips();

        var ok = Assert.IsType<OkObjectResult>(result);
        var trips = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value!);
        Assert.Single(trips);
    }

    [Fact]
    public void GetTrip_Unauthorized_ForPrivateDifferentUser()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var other = TestDataFixtures.CreateUser(id: "other");
        db.Users.AddRange(owner, other);
        db.ApiTokens.Add(new ApiToken { Id = 2, Token = "tok", UserId = other.Id, Name = "test", User = other });
        db.Trips.Add(new Trip { Id = Guid.NewGuid(), UserId = owner.Id, Name = "Private", IsPublic = false });
        db.SaveChanges();
        var controller = BuildController(db);
        controller.ControllerContext.HttpContext.Request.Headers["Authorization"] = "Bearer tok";

        var result = controller.GetTrip(db.Trips.First().Id);

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public void GetTrip_ReturnsOk_ForPublicTrip()
    {
        var db = CreateDbContext();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "u1", Name = "Public", IsPublic = true, UpdatedAt = DateTime.UtcNow, Regions = new List<Region>(), Segments = new List<Segment>() };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db);

        var result = controller.GetTrip(trip.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(trip.Id, ok.Value?.GetType().GetProperty("Id")?.GetValue(ok.Value));
    }

    [Fact]
    public async Task GetTripBoundary_ReturnsBadRequest_WhenNoGeo()
    {
        var db = CreateDbContext();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "u1", Name = "NoGeo", IsPublic = true, Regions = new List<Region>(), Segments = new List<Segment>() };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db);

        var result = await controller.GetTripBoundary(trip.Id);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetTripBoundary_ReturnsBoundingBox_ForPublicTrip()
    {
        var db = CreateDbContext();
        var regionId = Guid.NewGuid();
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            UserId = "u1",
            Name = "HasGeo",
            IsPublic = true,
            Regions = new List<Region>
            {
                new Region
                {
                    Id = regionId,
                    TripId = trip.Id,
                    Name = "R1",
                    Places = new List<Place>
                    {
                        new Place
                        {
                            Id = Guid.NewGuid(),
                            Name = "P1",
                            UserId = trip.UserId,
                            RegionId = regionId,
                            Location = new Point(20, 10) { SRID = 4326 }
                        }
                    }
                }
            },
            Segments = new List<Segment>()
        };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db);

        var result = await controller.GetTripBoundary(trip.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var boundingBox = payload.GetType().GetProperty("BoundingBox")?.GetValue(payload);
        Assert.NotNull(boundingBox);
        double? north = boundingBox?.GetType().GetProperty("North")?.GetValue(boundingBox) as double?;
        double? south = boundingBox?.GetType().GetProperty("South")?.GetValue(boundingBox) as double?;
        double? east = boundingBox?.GetType().GetProperty("East")?.GetValue(boundingBox) as double?;
        double? west = boundingBox?.GetType().GetProperty("West")?.GetValue(boundingBox) as double?;
        Assert.True(north >= 10);
        Assert.True(south <= 10);
        Assert.True(east >= 20);
        Assert.True(west <= 20);
    }

    [Fact]
    public async Task GetTripBoundary_ReturnsNotFound_WhenTripMissing()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.GetTripBoundary(Guid.NewGuid());

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetTripBoundary_ReturnsBadRequest_WhenNoValidGeometry()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Name = "EmptyGeo",
            IsPublic = true,
            Regions = new List<Region>
            {
                new Region
                {
                    Id = Guid.NewGuid(),
                    TripId = Guid.NewGuid(),
                    UserId = user.Id,
                    Name = "R1",
                    Places = new List<Place>() // no coordinates
                }
            },
            Segments = new List<Segment>()
        };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.GetTripBoundary(trip.Id);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetPublicTrips_ReturnsOnlyPublic()
    {
        var db = CreateDbContext();
        var tagService = new Mock<ITripTagService>();
        tagService.Setup(s => s.ApplyTagFilter(It.IsAny<IQueryable<Trip>>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>()))
            .Returns<IQueryable<Trip>>(q => q);
        var controller = BuildController(db, token: null, tagService: tagService.Object);
        var publicTrip = new Trip { Id = Guid.NewGuid(), UserId = "u1", Name = "Public", IsPublic = true, UpdatedAt = DateTime.UtcNow };
        var privateTrip = new Trip { Id = Guid.NewGuid(), UserId = "u2", Name = "Private", IsPublic = false, UpdatedAt = DateTime.UtcNow };
        db.Trips.AddRange(publicTrip, privateTrip);
        db.SaveChanges();

        var result = await controller.GetPublicTrips(page: 1, pageSize: 10, sort: "updated_desc", tags: null, tagMode: null);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var items = payload.GetType().GetProperty("items")?.GetValue(payload) as IEnumerable<object>;
        Assert.NotNull(items);
        Assert.Single(items!);
    }

    [Theory]
    [InlineData("name_asc")]
    [InlineData("name_desc")]
    [InlineData("updated_desc")]
    public async Task GetPublicTrips_SupportsSorting(string sort)
    {
        var db = CreateDbContext();
        var tagService = new Mock<ITripTagService>();
        tagService.Setup(s => s.ApplyTagFilter(It.IsAny<IQueryable<Trip>>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>()))
            .Returns<IQueryable<Trip>>(q => q);
        db.Trips.AddRange(
            new Trip { Id = Guid.NewGuid(), UserId = "u1", Name = "B", IsPublic = true, UpdatedAt = DateTime.UtcNow.AddDays(-1) },
            new Trip { Id = Guid.NewGuid(), UserId = "u1", Name = "A", IsPublic = true, UpdatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var controller = BuildController(db, tagService: tagService.Object);

        var result = await controller.GetPublicTrips(1, 10, sort, tags: null, tagMode: null);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var items = payload.GetType().GetProperty("items")?.GetValue(payload) as IEnumerable<object>;
        Assert.NotNull(items);
        Assert.Equal(2, items!.Count());
    }

    [Fact]
    public async Task GetPublicTrips_AppliesTagFilter()
    {
        var db = CreateDbContext();
        var tagService = new Mock<ITripTagService>();
        tagService.Setup(s => s.ApplyTagFilter(It.IsAny<IQueryable<Trip>>(), It.IsAny<IReadOnlyCollection<string>>(), "any"))
            .Returns<IQueryable<Trip>>(q => q.Where(t => t.Name == "Tagged"));
        db.Trips.AddRange(
            new Trip { Id = Guid.NewGuid(), UserId = "u1", Name = "Tagged", IsPublic = true, UpdatedAt = DateTime.UtcNow },
            new Trip { Id = Guid.NewGuid(), UserId = "u1", Name = "Other", IsPublic = true, UpdatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var controller = BuildController(db, tagService: tagService.Object);

        var result = await controller.GetPublicTrips(1, 10, "updated_desc", tags: "tag1", tagMode: "any");

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var items = payload.GetType().GetProperty("items")?.GetValue(payload) as IEnumerable<object>;
        Assert.Single(items!);
    }

    [Fact]
    public async Task CloneTrip_ReturnsBadRequest_WhenCloningOwnTrip()
    {
        var db = CreateDbContext();
        var requester = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = requester.Id, Name = "Mine", IsPublic = true };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.CloneTrip(trip.Id);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CreatePlace_ReturnsUnauthorized_ForDifferentOwner()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var other = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = owner.Id, Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = owner.Id, Name = "R1" };
        db.Users.Add(owner);
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.CreatePlace(trip.Id, new PlaceCreateRequestDto { Name = "P1", RegionId = region.Id, Latitude = 1, Longitude = 2 });

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task UpdateRegion_ReturnsUnauthorized_ForDifferentOwner()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var other = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = owner.Id, Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = owner.Id, Name = "R1" };
        db.Users.Add(owner);
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.UpdateRegion(region.Id, new RegionUpdateRequestDto { Name = "New" });

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task CloneTrip_ReturnsBadRequest_WhenPrivate()
    {
        var db = CreateDbContext();
        var requester = SeedUserWithToken(db, "tok");
        var owner = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(owner);
        db.Trips.Add(new Trip { Id = Guid.NewGuid(), UserId = owner.Id, Name = "Private", IsPublic = false });
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.CloneTrip(db.Trips.First().Id);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdatePlace_ReturnsUnauthorized_WhenNoToken()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.UpdatePlace(Guid.NewGuid(), new PlaceUpdateRequestDto());

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task UpdatePlace_ReturnsBadRequest_WhenLatOrLonMissing()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "R1" };
        var place = new Place { Id = Guid.NewGuid(), RegionId = region.Id, Region = region, Name = "P1", UserId = user.Id };
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.Places.Add(place);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.UpdatePlace(place.Id, new PlaceUpdateRequestDto { Latitude = 10 });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdatePlace_MovesToAnotherRegion_ForOwner()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        var region1 = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "R1" };
        var region2 = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "R2" };
        var place = new Place { Id = Guid.NewGuid(), RegionId = region1.Id, Region = region1, Name = "P1", UserId = user.Id };
        db.Trips.Add(trip);
        db.Regions.AddRange(region1, region2);
        db.Places.Add(place);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.UpdatePlace(place.Id, new PlaceUpdateRequestDto { RegionId = region2.Id, Name = "Moved" });

        var ok = Assert.IsType<OkObjectResult>(result);
        var updated = db.Places.First(p => p.Id == place.Id);
        Assert.Equal(region2.Id, updated.RegionId);
        Assert.Equal("Moved", updated.Name);
    }

    [Fact]
    public async Task UpdateRegion_ReturnsBadRequest_ForReservedName()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "R1" };
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.UpdateRegion(region.Id, new RegionUpdateRequestDto { Name = "Unassigned Places" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateRegion_UpdatesFields_ForOwner()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "R1" };
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.UpdateRegion(region.Id, new RegionUpdateRequestDto { Name = "New Name", DisplayOrder = 5 });

        var ok = Assert.IsType<OkObjectResult>(result);
        var updated = db.Regions.First(r => r.Id == region.Id);
        Assert.Equal("New Name", updated.Name);
        Assert.Equal(5, updated.DisplayOrder);
    }

    [Fact]
    public async Task CloneTrip_ReturnsUnauthorized_WhenNoToken()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.CloneTrip(Guid.NewGuid());

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task CloneTrip_CreatesNewTrip_ForPublicSource()
    {
        var db = CreateDbContext();
        var sourceOwner = TestDataFixtures.CreateUser(id: "owner");
        var requester = SeedUserWithToken(db, "tok");
        var sourceTrip = new Trip
        {
            Id = Guid.NewGuid(),
            UserId = sourceOwner.Id,
            Name = "Public Trip",
            IsPublic = true,
            Regions = new List<Region>(),
            Segments = new List<Segment>()
        };
        db.Users.Add(sourceOwner);
        db.Trips.Add(sourceTrip);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.CloneTrip(sourceTrip.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var clonedId = (Guid?)payload.GetType().GetProperty("clonedTripId")?.GetValue(payload);
        Assert.NotNull(clonedId);
        Assert.True(db.Trips.Any(t => t.Id == clonedId && t.UserId == requester.Id));
    }

    [Fact]
    public async Task CreateRegion_ReturnsBadRequest_WhenMissingName()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.CreateRegion(trip.Id, new RegionCreateRequestDto { Name = "" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CreateRegion_ReturnsNotFound_WhenTripMissing()
    {
        var db = CreateDbContext();
        SeedUserWithToken(db, "tok");
        var controller = BuildController(db, token: "tok");

        var result = await controller.CreateRegion(Guid.NewGuid(), new RegionCreateRequestDto { Name = "R1" });

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task CreateRegion_Succeeds_ForOwner()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.CreateRegion(trip.Id, new RegionCreateRequestDto { Name = "R1" });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(db.Regions.Any(r => r.TripId == trip.Id && r.Name == "R1"));
    }

    [Fact]
    public async Task CreatePlace_ReturnsBadRequest_WhenCoordsMissing()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "R1" };
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.CreatePlace(trip.Id, region.Id, new PlaceCreateRequestDto { Name = "P1" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CreatePlace_Succeeds_WithValidData()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "R1" };
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.CreatePlace(trip.Id, new PlaceCreateRequestDto
        {
            Name = "P1",
            RegionId = region.Id,
            Latitude = 10,
            Longitude = 20
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(db.Places.Any(p => p.RegionId == region.Id && p.Name == "P1"));
    }

    private TripsController BuildController(ApplicationDbContext db, string? token = null, ITripTagService? tagService = null)
    {
        tagService ??= Mock.Of<ITripTagService>();
        var controller = new TripsController(db, NullLogger<BaseApiController>.Instance, tagService);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        if (!string.IsNullOrEmpty(token))
        {
            controller.ControllerContext.HttpContext.Request.Headers["Authorization"] = $"Bearer {token}";
        }
        return controller;
    }

    private static ApplicationUser SeedUserWithToken(ApplicationDbContext db, string token)
    {
        var user = TestDataFixtures.CreateUser(id: "seed-user", username: "seed");
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { Token = token, UserId = user.Id, Name = "test", User = user });
        db.SaveChanges();
        return user;
    }
}
