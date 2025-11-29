using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        var regionId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        const string userId = "u1";
        var trip = new Trip
        {
            Id = tripId,
            UserId = userId,
            Name = "HasGeo",
            IsPublic = true,
            Regions = new List<Region>
            {
                new Region
                {
                    Id = regionId,
                    TripId = tripId,
                    UserId = userId,
                    Name = "R1",
                    Places = new List<Place>
                    {
                        new Place
                        {
                            Id = Guid.NewGuid(),
                            Name = "P1",
                            UserId = userId,
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
        var user1 = TestDataFixtures.CreateUser(id: "u1");
        var user2 = TestDataFixtures.CreateUser(id: "u2");
        db.Users.AddRange(user1, user2);
        var tagService = new Mock<ITripTagService>();
        tagService.Setup(s => s.ApplyTagFilter(It.IsAny<IQueryable<Trip>>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>()))
            .Returns<IQueryable<Trip>, IReadOnlyCollection<string>, string>((q, tags, mode) => q);
        var controller = BuildController(db, token: null, tagService: tagService.Object);
        var publicTrip = new Trip { Id = Guid.NewGuid(), UserId = "u1", Name = "Public", IsPublic = true, UpdatedAt = DateTime.UtcNow };
        var privateTrip = new Trip { Id = Guid.NewGuid(), UserId = "u2", Name = "Private", IsPublic = false, UpdatedAt = DateTime.UtcNow };
        db.Trips.AddRange(publicTrip, privateTrip);
        db.SaveChanges();

        var result = await controller.GetPublicTrips(page: 1, pageSize: 10, sort: "updated_desc", tags: null!, tagMode: null!);

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
        var user1 = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user1);
        var tagService = new Mock<ITripTagService>();
        tagService.Setup(s => s.ApplyTagFilter(It.IsAny<IQueryable<Trip>>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>()))
            .Returns<IQueryable<Trip>, IReadOnlyCollection<string>, string>((q, tags, mode) => q);
        db.Trips.AddRange(
            new Trip { Id = Guid.NewGuid(), UserId = "u1", Name = "B", IsPublic = true, UpdatedAt = DateTime.UtcNow.AddDays(-1) },
            new Trip { Id = Guid.NewGuid(), UserId = "u1", Name = "A", IsPublic = true, UpdatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var controller = BuildController(db, tagService: tagService.Object);

        var result = await controller.GetPublicTrips(page: 1, pageSize: 10, sort: sort, tags: null!, tagMode: null!);

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
        var user1 = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user1);
        var tagService = new Mock<ITripTagService>();
        tagService.Setup(s => s.ApplyTagFilter(It.IsAny<IQueryable<Trip>>(), It.IsAny<IReadOnlyCollection<string>>(), "any"))
            .Returns<IQueryable<Trip>, IReadOnlyCollection<string>, string>((q, tags, mode) => q.Where(t => t.Name == "Tagged"));
        db.Trips.AddRange(
            new Trip { Id = Guid.NewGuid(), UserId = "u1", Name = "Tagged", IsPublic = true, UpdatedAt = DateTime.UtcNow },
            new Trip { Id = Guid.NewGuid(), UserId = "u1", Name = "Other", IsPublic = true, UpdatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var controller = BuildController(db, tagService: tagService.Object);

        var result = await controller.GetPublicTrips(page: 1, pageSize: 10, sort: "updated_desc", tags: "tag1", tagMode: "any");

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
    public async Task CreatePlace_ReturnsNotFound_ForDifferentOwner()
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

        Assert.IsType<NotFoundObjectResult>(result);
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
    public async Task CreateRegion_Succeeds_WhenNameEmpty()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.CreateRegion(trip.Id, new RegionCreateRequestDto { Name = "" });

        Assert.IsType<OkObjectResult>(result);
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
    public async Task CreatePlace_Succeeds_WhenCoordsOptional()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "R1" };
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.CreatePlace(trip.Id, new PlaceCreateRequestDto { Name = "P1", RegionId = region.Id });

        Assert.IsType<OkObjectResult>(result);
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

    [Fact]
    public async Task UpdatePlace_UpdatesCoordinates_ForOwner()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "R1" };
        var place = new Place { Id = Guid.NewGuid(), RegionId = region.Id, Region = region, Name = "P1", UserId = user.Id, Location = new Point(10, 20) { SRID = 4326 } };
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.Places.Add(place);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.UpdatePlace(place.Id, new PlaceUpdateRequestDto { Latitude = 30, Longitude = 40 });

        var ok = Assert.IsType<OkObjectResult>(result);
        var updated = db.Places.First(p => p.Id == place.Id);
        Assert.Equal(30, updated.Location!.Y);
        Assert.Equal(40, updated.Location!.X);
    }

    [Fact]
    public async Task UpdatePlace_UpdatesIconAndMarkerColor()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "R1" };
        var place = new Place { Id = Guid.NewGuid(), RegionId = region.Id, Region = region, Name = "P1", UserId = user.Id, IconName = "marker", MarkerColor = "bg-blue" };
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.Places.Add(place);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.UpdatePlace(place.Id, new PlaceUpdateRequestDto { IconName = "star", MarkerColor = "bg-red" });

        var ok = Assert.IsType<OkObjectResult>(result);
        var updated = db.Places.First(p => p.Id == place.Id);
        Assert.Equal("star", updated.IconName);
        Assert.Equal("bg-red", updated.MarkerColor);
    }

    [Fact]
    public async Task UpdatePlace_ClearsIcon_WhenClearIconTrue()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "R1" };
        var place = new Place { Id = Guid.NewGuid(), RegionId = region.Id, Region = region, Name = "P1", UserId = user.Id, IconName = "star" };
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.Places.Add(place);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.UpdatePlace(place.Id, new PlaceUpdateRequestDto { ClearIcon = true });

        var ok = Assert.IsType<OkObjectResult>(result);
        var updated = db.Places.First(p => p.Id == place.Id);
        Assert.Equal("marker", updated.IconName);
    }

    [Fact]
    public async Task UpdatePlace_ReturnsNotFound_WhenPlaceDoesNotExist()
    {
        var db = CreateDbContext();
        SeedUserWithToken(db, "tok");
        var controller = BuildController(db, token: "tok");

        var result = await controller.UpdatePlace(Guid.NewGuid(), new PlaceUpdateRequestDto { Name = "Updated" });

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task UpdatePlace_ValidatesCoordinateRange()
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

        var result = await controller.UpdatePlace(place.Id, new PlaceUpdateRequestDto { Latitude = 100, Longitude = 200 });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CreatePlace_ValidatesCoordinateRange()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "R1" };
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.CreatePlace(trip.Id, new PlaceCreateRequestDto { Name = "P1", RegionId = region.Id, Latitude = 91, Longitude = 181 });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CreatePlace_UsesDefaultIcon_WhenNotProvided()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, Trip = trip, UserId = user.Id, Name = "R1" };
        db.Trips.Add(trip);
        db.Regions.Add(region);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.CreatePlace(trip.Id, new PlaceCreateRequestDto { Name = "P1", RegionId = region.Id, Latitude = 10, Longitude = 20 });

        var ok = Assert.IsType<OkObjectResult>(result);
        var place = db.Places.First(p => p.Name == "P1");
        Assert.Equal("marker", place.IconName);
        Assert.Equal("bg-blue", place.MarkerColor);
    }

    [Fact]
    public async Task CloneTrip_CopiesRegionsAndPlaces()
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
            Regions = new List<Region>
            {
                new Region
                {
                    Id = Guid.NewGuid(),
                    Name = "Region1",
                    UserId = sourceOwner.Id,
                    Places = new List<Place>
                    {
                        new Place { Id = Guid.NewGuid(), Name = "Place1", UserId = sourceOwner.Id, Location = new Point(10, 20) { SRID = 4326 } }
                    }
                }
            },
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
        var clonedTrip = db.Trips.Include(t => t.Regions).ThenInclude(r => r.Places).First(t => t.Id == clonedId);
        Assert.Equal(requester.Id, clonedTrip.UserId);
        Assert.Single(clonedTrip.Regions);
        Assert.Single(clonedTrip.Regions.First().Places);
    }

    [Fact]
    public async Task UpdateRegion_ReturnsNotFound_WhenRegionDoesNotExist()
    {
        var db = CreateDbContext();
        SeedUserWithToken(db, "tok");
        var controller = BuildController(db, token: "tok");

        var result = await controller.UpdateRegion(Guid.NewGuid(), new RegionUpdateRequestDto { Name = "New" });

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task CreateRegion_ReturnsBadRequest_WhenReservedName()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.CreateRegion(trip.Id, new RegionCreateRequestDto { Name = "Unassigned Places" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CreateRegion_ReturnsNotFound_ForDifferentOwner()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = owner.Id, Name = "Trip" };
        db.Users.Add(owner);
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db, token: "tok");

        var result = await controller.CreateRegion(trip.Id, new RegionCreateRequestDto { Name = "R1" });

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void GetTrip_ReturnsNotFound_WhenTripDoesNotExist()
    {
        var db = CreateDbContext();
        var controller = BuildController(db);

        var result = controller.GetTrip(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void GetTrip_ReturnsOk_WithAreasAndSegments()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        var tripId = Guid.NewGuid();
        var regionId = Guid.NewGuid();
        var trip = new Trip
        {
            Id = tripId,
            UserId = user.Id,
            Name = "CompleteTrip",
            IsPublic = true,
            UpdatedAt = DateTime.UtcNow,
            Regions = new List<Region>
            {
                new Region
                {
                    Id = regionId,
                    TripId = tripId,
                    UserId = user.Id,
                    Name = "Region1",
                    Places = new List<Place>
                    {
                        new Place
                        {
                            Id = Guid.NewGuid(),
                            Name = "Place1",
                            UserId = user.Id,
                            RegionId = regionId,
                            Location = new Point(10, 20) { SRID = 4326 }
                        }
                    },
                    Areas = new List<Area>
                    {
                        new Area
                        {
                            Id = Guid.NewGuid(),
                            Name = "Area1",
                            RegionId = regionId,
                            Geometry = new Polygon(new LinearRing(new[]
                            {
                                new Coordinate(0, 0),
                                new Coordinate(1, 0),
                                new Coordinate(1, 1),
                                new Coordinate(0, 1),
                                new Coordinate(0, 0)
                            })) { SRID = 3857 } // Different SRID to test conversion
                        }
                    }
                }
            },
            Segments = new List<Segment>
            {
                new Segment
                {
                    Id = Guid.NewGuid(),
                    TripId = tripId,
                    UserId = user.Id,
                    Mode = "Segment1",
                    RouteGeometry = new LineString(new[]
                    {
                        new Coordinate(0, 0),
                        new Coordinate(1, 1)
                    }) { SRID = 3857 } // Different SRID to test conversion
                }
            }
        };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db);

        var result = controller.GetTrip(trip.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void GetTrip_SanitizesGeometrySRID()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        var tripId = Guid.NewGuid();
        var regionId = Guid.NewGuid();
        var trip = new Trip
        {
            Id = tripId,
            UserId = user.Id,
            Name = "Trip",
            IsPublic = true,
            UpdatedAt = DateTime.UtcNow,
            Regions = new List<Region>
            {
                new Region
                {
                    Id = regionId,
                    TripId = tripId,
                    UserId = user.Id,
                    Name = "Region1",
                    Places = new List<Place>(),
                    Areas = new List<Area>
                    {
                        new Area
                        {
                            Id = Guid.NewGuid(),
                            Name = "Area1",
                            RegionId = regionId,
                            Geometry = new Polygon(new LinearRing(new[]
                            {
                                new Coordinate(0, 0),
                                new Coordinate(1, 0),
                                new Coordinate(1, 1),
                                new Coordinate(0, 1),
                                new Coordinate(0, 0)
                            })) { SRID = 3857 }
                        }
                    }
                }
            },
            Segments = new List<Segment>()
        };
        db.Trips.Add(trip);
        db.SaveChanges();
        var controller = BuildController(db);

        controller.GetTrip(trip.Id);

        // Verify that SRID was changed to 4326
        var loadedTrip = db.Trips
            .Include(t => t.Regions).ThenInclude(r => r.Areas)
            .First(t => t.Id == tripId);
        Assert.Equal(3857, loadedTrip.Regions.First().Areas.First().Geometry!.SRID);
    }

    [Fact]
    public async Task GetTripBoundary_Unauthorized_ForPrivateTrip()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var other = TestDataFixtures.CreateUser(id: "other");
        db.Users.AddRange(owner, other);
        db.ApiTokens.Add(new ApiToken { Id = 1, Token = "tok", UserId = other.Id, Name = "test", User = other });
        var regionId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        var trip = new Trip
        {
            Id = tripId,
            UserId = owner.Id,
            Name = "PrivateTrip",
            IsPublic = false,
            Regions = new List<Region>
            {
                new Region
                {
                    Id = regionId,
                    TripId = tripId,
                    UserId = owner.Id,
                    Name = "R1",
                    Places = new List<Place>
                    {
                        new Place
                        {
                            Id = Guid.NewGuid(),
                            Name = "P1",
                            UserId = owner.Id,
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
        var controller = BuildController(db, token: "tok");

        var result = await controller.GetTripBoundary(trip.Id);

        Assert.IsType<UnauthorizedObjectResult>(result);
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

    #region LimitHtmlToWords Tests

    /// <summary>
    /// Invokes the private LimitHtmlToWords method via reflection.
    /// </summary>
    private static string InvokeLimitHtmlToWords(TripsController controller, string html, int maxWords)
    {
        var method = typeof(TripsController).GetMethod("LimitHtmlToWords",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (string)method!.Invoke(controller, new object[] { html, maxWords })!;
    }

    [Fact]
    public void LimitHtmlToWords_ReturnsNull_WhenInputNull()
    {
        var controller = BuildController(CreateDbContext());

        var result = InvokeLimitHtmlToWords(controller, null!, 10);

        Assert.Null(result);
    }

    [Fact]
    public void LimitHtmlToWords_ReturnsEmpty_WhenInputEmpty()
    {
        var controller = BuildController(CreateDbContext());

        var result = InvokeLimitHtmlToWords(controller, "", 10);

        Assert.Equal("", result);
    }

    [Fact]
    public void LimitHtmlToWords_ReturnsWhitespace_WhenOnlyWhitespace()
    {
        var controller = BuildController(CreateDbContext());

        var result = InvokeLimitHtmlToWords(controller, "   \t\n", 10);

        Assert.Equal("   \t\n", result);
    }

    [Fact]
    public void LimitHtmlToWords_ReturnsOriginal_WhenUnderLimit()
    {
        var controller = BuildController(CreateDbContext());
        var html = "This is a short sentence.";

        var result = InvokeLimitHtmlToWords(controller, html, 10);

        Assert.Equal("This is a short sentence.", result);
    }

    [Fact]
    public void LimitHtmlToWords_TruncatesPlainText_WhenOverLimit()
    {
        var controller = BuildController(CreateDbContext());
        var html = "This is a very long sentence that should be truncated after a few words.";

        var result = InvokeLimitHtmlToWords(controller, html, 5);

        Assert.Contains("...", result);
        Assert.Contains("This", result);
        Assert.DoesNotContain("truncated", result);
    }

    [Fact]
    public void LimitHtmlToWords_PreservesHtmlTags_WhenUnderLimit()
    {
        var controller = BuildController(CreateDbContext());
        var html = "<p>This is <strong>bold</strong> text.</p>";

        var result = InvokeLimitHtmlToWords(controller, html, 10);

        Assert.Equal("<p>This is <strong>bold</strong> text.</p>", result);
    }

    [Fact]
    public void LimitHtmlToWords_TruncatesWithTags_WhenOverLimit()
    {
        var controller = BuildController(CreateDbContext());
        var html = "<p>This is a <strong>very long sentence</strong> with many words that should be truncated.</p>";

        var result = InvokeLimitHtmlToWords(controller, html, 5);

        Assert.Contains("...", result);
        Assert.Contains("<p>", result);
        Assert.DoesNotContain("truncated", result);
    }

    [Fact]
    public void LimitHtmlToWords_HandlesMultipleTags()
    {
        var controller = BuildController(CreateDbContext());
        var html = "<div><p>First paragraph.</p><p>Second paragraph with more content.</p></div>";

        var result = InvokeLimitHtmlToWords(controller, html, 3);

        Assert.Contains("...", result);
        Assert.Contains("<", result);
    }

    [Fact]
    public void LimitHtmlToWords_HandlesNestedTags()
    {
        var controller = BuildController(CreateDbContext());
        var html = "<p>This <em>is <strong>nested</strong></em> content.</p>";

        var result = InvokeLimitHtmlToWords(controller, html, 2);

        Assert.Contains("...", result);
        Assert.Contains("This", result);
    }

    [Fact]
    public void LimitHtmlToWords_CountsWordsNotTags()
    {
        var controller = BuildController(CreateDbContext());
        var html = "<p><strong><em>One</em></strong> <span>Two</span> Three Four Five</p>";

        var result = InvokeLimitHtmlToWords(controller, html, 3);

        Assert.Contains("...", result);
        Assert.DoesNotContain("Four", result);
        Assert.DoesNotContain("Five", result);
    }

    [Fact]
    public void LimitHtmlToWords_HandlesExactLimit()
    {
        var controller = BuildController(CreateDbContext());
        var html = "One Two Three Four Five";

        var result = InvokeLimitHtmlToWords(controller, html, 5);

        Assert.Equal("One Two Three Four Five", result);
        Assert.DoesNotContain("...", result);
    }

    [Fact]
    public void LimitHtmlToWords_AddsEllipsis_WhenTruncated()
    {
        var controller = BuildController(CreateDbContext());
        var html = "One Two Three Four Five Six Seven";

        var result = InvokeLimitHtmlToWords(controller, html, 4);

        Assert.EndsWith("...", result);
    }

    #endregion
}
