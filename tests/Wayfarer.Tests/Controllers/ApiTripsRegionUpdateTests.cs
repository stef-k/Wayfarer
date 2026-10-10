using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Proves legacy Region validation leaves tracked state intact and preserves partial updates.</summary>
public partial class ApiTripsControllerTests
{
    /// <summary>Rejects mixed metadata/identity changes, including case-only names and historically reordered rows.</summary>
    [Theory]
    [InlineData("Unassigned Places", 0, "Renamed", null)]
    [InlineData("Unassigned Places", 0, null, 7)]
    [InlineData("Unassigned Places", 0, "unassigned places", null)]
    [InlineData("Unassigned Places", 0, "   ", null)]
    [InlineData("unassigned places", 7, null, 0)]
    public async Task UpdateRegion_ProtectedIdentityRejectsMixedPayloadWithoutTrackedChanges(
        string storedName, int storedOrder, string? name, int? order)
    {
        using var db = CreateDbContext();
        var region = await SeedRegionUpdateTargetAsync(db, storedName, storedOrder);
        var timestamp = region.Trip.UpdatedAt;
        var controller = BuildController(db, token: "tok");

        var result = await controller.UpdateRegion(region.Id, new RegionUpdateRequestDto
        {
            Name = name, DisplayOrder = order, Notes = "Changed", CoverImageUrl = "Changed",
            CenterLatitude = 0, CenterLongitude = 0
        });

        var rejected = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Cannot change the Unassigned Places region name or display order.", rejected.Value);
        Assert.Equal(storedName, region.Name);
        Assert.Equal(storedOrder, region.DisplayOrder);
        Assert.Equal("<p>Original</p>", region.Notes);
        Assert.Equal("original-cover", region.CoverImageUrl);
        Assert.Equal((23d, 37d, 4326), (region.Center!.X, region.Center.Y, region.Center.SRID));
        Assert.Equal(timestamp, region.Trip.UpdatedAt);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    /// <summary>Invalid paired/range center input cannot dirty earlier supplied identity or metadata fields.</summary>
    [Theory]
    [InlineData("Unassigned Places", 0d, null, "Both centerLatitude and centerLongitude must be provided together.")]
    [InlineData("Ordinary", 91d, 10d, "Center latitude or longitude is out of range.")]
    public async Task UpdateRegion_InvalidCenterRejectsBeforeTrackedAssignments(
        string storedName, double? latitude, double? longitude, string error)
    {
        using var db = CreateDbContext();
        var region = await SeedRegionUpdateTargetAsync(db, storedName, 0);
        var timestamp = region.Trip.UpdatedAt;
        var controller = BuildController(db, token: "tok");

        var result = await controller.UpdateRegion(region.Id, new RegionUpdateRequestDto
        {
            Name = storedName == "Ordinary" ? "Changed" : storedName,
            Notes = "Changed", CoverImageUrl = "Changed", DisplayOrder = 0,
            CenterLatitude = latitude, CenterLongitude = longitude
        });

        Assert.Equal(error, Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Equal(storedName, region.Name);
        Assert.Equal(0, region.DisplayOrder);
        Assert.Equal("<p>Original</p>", region.Notes);
        Assert.Equal("original-cover", region.CoverImageUrl);
        Assert.Equal((23d, 37d, 4326), (region.Center!.X, region.Center.Y, region.Center.SRID));
        Assert.Equal(timestamp, region.Trip.UpdatedAt);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    /// <summary>Trimmed identical protected values are no-ops, including recognizable historical spelling/order drift.</summary>
    [Theory]
    [InlineData("Unassigned Places", 0)]
    [InlineData("unassigned places", 7)]
    public async Task UpdateRegion_IdenticalProtectedFieldsDoNotSave(string storedName, int storedOrder)
    {
        using var db = CreateDbContext();
        var region = await SeedRegionUpdateTargetAsync(db, storedName, storedOrder);
        var timestamp = region.Trip.UpdatedAt;
        var saves = 0;
        db.SavingChanges += (_, _) => saves++;
        var controller = BuildController(db, token: "tok");

        var result = await controller.UpdateRegion(region.Id, new RegionUpdateRequestDto
        {
            Name = $"  {storedName}  ", DisplayOrder = storedOrder
        });

        var payload = Assert.IsType<OkObjectResult>(result).Value!;
        Assert.Equal("No changes applied.", payload.GetType().GetProperty("message")!.GetValue(payload));
        Assert.Equal(storedName, region.Name);
        Assert.Equal(storedOrder, region.DisplayOrder);
        Assert.Equal(timestamp, region.Trip.UpdatedAt);
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Equal(0, saves);
    }

    /// <summary>Non-null empty strings still clear reserved metadata while omitted center values preserve its point.</summary>
    [Fact]
    public async Task UpdateRegion_ReservedMetadataRetainsEmptyStringClearing()
    {
        using var db = CreateDbContext();
        var region = await SeedRegionUpdateTargetAsync(db, "Unassigned Places", 0);
        var controller = BuildController(db, token: "tok");

        Assert.IsType<OkObjectResult>(await controller.UpdateRegion(region.Id,
            new RegionUpdateRequestDto { Notes = "", CoverImageUrl = "" }));

        Assert.Equal("", region.Notes);
        Assert.Equal("", region.CoverImageUrl);
        Assert.Equal((23d, 37d, 4326), (region.Center!.X, region.Center.Y, region.Center.SRID));
        Assert.Equal("Unassigned Places", region.Name);
        Assert.Equal(0, region.DisplayOrder);
    }

    /// <summary>Seeds only the owned Region fields that the partial update contract may mutate.</summary>
    private async Task<Region> SeedRegionUpdateTargetAsync(ApplicationDbContext db, string name, int order)
    {
        var user = SeedUserWithToken(db, "tok");
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Trip" };
        var region = new Region
        {
            Id = Guid.NewGuid(), Trip = trip, UserId = user.Id, Name = name, DisplayOrder = order,
            Notes = "<p>Original</p>", CoverImageUrl = "original-cover", Center = new Point(23, 37) { SRID = 4326 }
        };
        db.Regions.Add(region);
        await db.SaveChangesAsync();
        return region;
    }
}
