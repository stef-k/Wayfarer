using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Proves the finite, paired, optional coordinate contract at the four legacy mutation actions.</summary>
public partial class ApiTripsControllerTests
{
    /// <summary>Exercises non-finite values in either component, finite range failures and incomplete pairs.</summary>
    public static IEnumerable<object?[]> InvalidLegacyCoordinates()
    {
        var pairs = new (double? Latitude, double? Longitude)[]
        {
            (double.NaN, 1), (1, double.NaN),
            (double.PositiveInfinity, 1), (1, double.PositiveInfinity),
            (double.NegativeInfinity, 1), (1, double.NegativeInfinity),
            (-91, 0), (91, 0), (0, -181), (0, 181),
            (0, null), (null, 0), (double.NaN, null), (null, double.NaN)
        };
        foreach (var action in new[] { "create-place", "update-place", "create-region", "update-region" })
            foreach (var pair in pairs)
                yield return [action, pair.Latitude, pair.Longitude];
    }

    /// <summary>Exercises inclusive WGS84 corners, paired zeroes and the optional null pair.</summary>
    public static IEnumerable<object?[]> ValidLegacyCoordinates()
    {
        var pairs = new (double? Latitude, double? Longitude)[]
        {
            (-90, -180), (-90, 180), (90, -180), (90, 180), (0, 0), (null, null)
        };
        foreach (var action in new[] { "create-place", "update-place", "create-region", "update-region" })
            foreach (var pair in pairs)
                yield return [action, pair.Latitude, pair.Longitude];
    }

    /// <summary>Rejected coordinates apply no supplied metadata/move/order fields and cause no save or warm-up.</summary>
    [Theory]
    [MemberData(nameof(InvalidLegacyCoordinates))]
    public async Task LegacyMutation_InvalidCoordinatesRejectWithoutChanges(string action, double? latitude, double? longitude)
    {
        using var db = CreateDbContext();
        var (region, destination, place) = await SeedCoordinateTargetsAsync(db);
        var timestamp = region.Trip.UpdatedAt;
        var saves = 0;
        db.SavingChanges += (_, _) => saves++;
        var warmup = new Mock<ICacheWarmupScheduler>();
        var controller = BuildCoordinateController(db, warmup.Object);

        var result = await MutateCoordinatesAsync(controller, action, region, destination, place, latitude, longitude);

        var paired = latitude.HasValue && longitude.HasValue;
        var isPlace = action.EndsWith("place", StringComparison.Ordinal);
        var error = isPlace
            ? paired ? "Latitude or Longitude is out of range." : "Both latitude and longitude must be provided together."
            : paired ? "Center latitude or longitude is out of range." : "Both centerLatitude and centerLongitude must be provided together.";
        Assert.Equal(error, Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Equal("Ordinary", region.Name);
        Assert.Equal(0, region.DisplayOrder);
        Assert.Equal("<p>Original</p>", region.Notes);
        Assert.Equal("original-cover", region.CoverImageUrl);
        Assert.Equal((23d, 37d, 4326), (region.Center!.X, region.Center.Y, region.Center.SRID));
        Assert.Equal("Original", place.Name);
        Assert.Equal("<p>Original</p>", place.Notes);
        Assert.Equal("Original address", place.Address);
        Assert.Equal((region.Id, 1, "original-icon", "original-color"),
            (place.RegionId, place.DisplayOrder, place.IconName, place.MarkerColor));
        Assert.Equal((23d, 37d, 4326), (place.Location!.X, place.Location.Y, place.Location.SRID));
        Assert.Equal(timestamp, region.Trip.UpdatedAt);
        Assert.Equal(2, await db.Regions.CountAsync());
        Assert.Equal(1, await db.Places.CountAsync());
        Assert.False(await db.Regions.AnyAsync(item => item.Name == "Unassigned Places"));
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Equal(0, saves);
        warmup.Verify(item => item.ScheduleWarmupAsync(It.IsAny<Guid>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>Valid and absent pairs retain the success envelope, axis order, SRID and update preservation.</summary>
    [Theory]
    [MemberData(nameof(ValidLegacyCoordinates))]
    public async Task LegacyMutation_ValidCoordinatesRemainCompatible(string action, double? latitude, double? longitude)
    {
        using var db = CreateDbContext();
        var (region, destination, place) = await SeedCoordinateTargetsAsync(db);
        var controller = BuildController(db, token: "tok");

        var result = await MutateCoordinatesAsync(controller, action, region, destination, place, latitude, longitude);

        var payload = Assert.IsType<OkObjectResult>(result).Value!;
        Assert.Equal(true, payload.GetType().GetProperty("success")!.GetValue(payload));
        var isPlace = action.EndsWith("place", StringComparison.Ordinal);
        var dto = payload.GetType().GetProperty(isPlace ? "place" : "region")!.GetValue(payload);
        var coordinates = isPlace ? Assert.IsType<ApiTripPlaceDto>(dto).Location : Assert.IsType<ApiTripRegionDto>(dto).Center;
        var expected = latitude.HasValue ? new[] { longitude!.Value, latitude.Value }
            : action.StartsWith("update", StringComparison.Ordinal) ? new[] { 23d, 37d } : null;
        Assert.Equal(expected, coordinates);
        var point = isPlace
            ? (await db.Places.FindAsync(Assert.IsType<ApiTripPlaceDto>(dto).Id))!.Location
            : (await db.Regions.FindAsync(Assert.IsType<ApiTripRegionDto>(dto).Id))!.Center;
        if (expected == null) Assert.Null(point);
        else Assert.Equal((expected[0], expected[1], 4326), (point!.X, point.Y, point.SRID));
    }

    /// <summary>Explicit destination errors and reserved identity errors retain precedence over NaN input.</summary>
    [Fact]
    public async Task LegacyMutation_AuthorityAndReservedIdentityErrorsPrecedeCoordinates()
    {
        using var db = CreateDbContext();
        var reserved = await SeedRegionUpdateTargetAsync(db, "Unassigned Places", 0);
        var controller = BuildController(db, token: "tok");

        var place = await controller.CreatePlace(reserved.TripId, new PlaceCreateRequestDto
        {
            RegionId = Guid.NewGuid(), Name = "Rejected", Latitude = double.NaN, Longitude = 0
        });
        Assert.Equal("Invalid regionId.", Assert.IsType<BadRequestObjectResult>(place).Value);
        var create = await controller.CreateRegion(reserved.TripId, new RegionCreateRequestDto
        {
            Name = "Unassigned Places", CenterLatitude = double.NaN, CenterLongitude = 0
        });
        Assert.Equal("Region name is reserved.", Assert.IsType<BadRequestObjectResult>(create).Value);
        var update = await controller.UpdateRegion(reserved.Id, new RegionUpdateRequestDto
        {
            Name = "Renamed", Notes = "Rejected", CenterLatitude = double.NaN, CenterLongitude = 0
        });
        Assert.Equal("Cannot change the Unassigned Places region name or display order.",
            Assert.IsType<BadRequestObjectResult>(update).Value);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    /// <summary>Seeds existing metadata and an owned same-Trip move destination; no fallback Region exists.</summary>
    private async Task<(Region Region, Region Destination, Place Place)> SeedCoordinateTargetsAsync(ApplicationDbContext db)
    {
        var region = await SeedRegionUpdateTargetAsync(db, "Ordinary", 0);
        var destination = new Region
        {
            Id = Guid.NewGuid(), Trip = region.Trip, UserId = region.UserId, Name = "Destination", DisplayOrder = 2
        };
        var place = new Place
        {
            Id = Guid.NewGuid(), Region = region, UserId = region.UserId, Name = "Original",
            Notes = "<p>Original</p>", Address = "Original address", DisplayOrder = 1,
            IconName = "original-icon", MarkerColor = "original-color", Location = new Point(23, 37) { SRID = 4326 }
        };
        db.Regions.Add(destination);
        db.Places.Add(place);
        await db.SaveChangesAsync();
        return (region, destination, place);
    }

    /// <summary>Uses the production controller and observes its existing cache scheduler seam.</summary>
    private static TripsController BuildCoordinateController(ApplicationDbContext db, ICacheWarmupScheduler warmup) =>
        new(db, NullLogger<BaseApiController>.Instance, Mock.Of<ITripTagService>(), Mock.Of<IApplicationSettingsService>(), warmup)
        {
            ControllerContext = new ControllerContext { HttpContext = CreateHttpContext("tok") }
        };

    /// <summary>Submits the same numeric pair together with otherwise-valid fields to each real action.</summary>
    private static Task<IActionResult> MutateCoordinatesAsync(TripsController controller, string action,
        Region region, Region destination, Place place, double? latitude, double? longitude) => action switch
    {
        "create-place" => controller.CreatePlace(region.TripId, new PlaceCreateRequestDto
        {
            Name = "Changed", Notes = "Changed", DisplayOrder = 7, IconName = "changed", MarkerColor = "changed",
            Latitude = latitude, Longitude = longitude
        }),
        "update-place" => controller.UpdatePlace(place.Id, new PlaceUpdateRequestDto
        {
            RegionId = destination.Id, Name = "Changed", Notes = "Changed", DisplayOrder = 7,
            IconName = "changed", MarkerColor = "changed", Latitude = latitude, Longitude = longitude
        }),
        "create-region" => controller.CreateRegion(region.TripId, new RegionCreateRequestDto
        {
            Name = "Changed", Notes = "Changed", CoverImageUrl = "changed", DisplayOrder = 7,
            CenterLatitude = latitude, CenterLongitude = longitude
        }),
        "update-region" => controller.UpdateRegion(region.Id, new RegionUpdateRequestDto
        {
            Name = "Changed", Notes = "Changed", CoverImageUrl = "changed", DisplayOrder = 7,
            CenterLatitude = latitude, CenterLongitude = longitude
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
}
