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

/// <summary>Proves the finite, paired, optional coordinate contract at the remaining coordinate update actions.</summary>
public partial class ApiTripsControllerTests
{
    /// <summary>Rejected coordinates apply no supplied metadata/move/order fields and cause no save or warm-up.</summary>
    [Theory]
    [InlineData("update-place")]
    [InlineData("update-region")]
    public async Task UnconditionalMutation_RejectsMixedPayloadWithoutChanges(string action)
    {
        using var db = CreateDbContext();
        var (region, destination, place) = await SeedCoordinateTargetsAsync(db);
        var timestamp = region.Trip.UpdatedAt;
        var saves = 0;
        db.SavingChanges += (_, _) => saves++;
        var warmup = new Mock<ICacheWarmupScheduler>();
        var controller = BuildCoordinateController(db, warmup.Object);

        var result = await MutateCoordinatesAsync(controller, action, region, destination, place, double.NaN, 0);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
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

    /// <summary>Canonical name and invalid coordinate payloads cannot admit an unconditional Region update.</summary>
    [Fact]
    public async Task UnconditionalRegionMutation_RejectsReservedIdentityAndCoordinateBypass()
    {
        using var db = CreateDbContext();
        var reserved = await SeedRegionUpdateTargetAsync(db, "Unassigned Places", 0);
        var controller = BuildController(db, token: "tok");

        var update = await controller.UpdateRegion(reserved.Id, new RegionUpdateRequestDto
        {
            Name = "Renamed", Notes = "Rejected", CenterLatitude = double.NaN, CenterLongitude = 0
        });
        Assert.Equal(409, Assert.IsType<ObjectResult>(update).StatusCode);
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

    /// <summary>Submits coordinate/metadata changes without an unrelated protected Region-order change.</summary>
    private static Task<IActionResult> MutateCoordinatesAsync(TripsController controller, string action,
        Region region, Region destination, Place place, double? latitude, double? longitude) => action switch
    {
        "update-place" => controller.UpdatePlace(place.Id, new PlaceUpdateRequestDto
        {
            RegionId = destination.Id, Name = "Changed", Notes = "Changed", DisplayOrder = 7,
            IconName = "changed", MarkerColor = "changed", Latitude = latitude, Longitude = longitude
        }),
        "update-region" => controller.UpdateRegion(region.Id, new RegionUpdateRequestDto
        {
            Name = "Changed", Notes = "Changed", CoverImageUrl = "changed",
            CenterLatitude = latitude, CenterLongitude = longitude
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
}
