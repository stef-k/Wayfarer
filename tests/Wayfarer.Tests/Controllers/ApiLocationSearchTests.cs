using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Search filtering and complete address projections, including the shared timeline DTO.</summary>
public partial class ApiLocationControllerTests
{
    [Fact]
    public async Task Search_ReturnsUnauthorized_WhenNoUser()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var controller = BuildApiController(db, user, includeAuthHeader: false);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal();

        var result = await controller.Search(null, null!, null!, null!, null!, null!, null!, null!, null!, null!);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Search_FiltersByActivityNotesAndAddress()
    {
        var db = CreateDbContext();
        var user = SeedUserWithToken(db, "tok");
        var activity = new ActivityType { Id = 1, Name = "Walk" };
        db.ActivityTypes.Add(activity);
        db.Locations.Add(new Wayfarer.Models.Location
        {
            UserId = user.Id,
            Coordinates = new Point(1, 1) { SRID = 4326 },
            Timestamp = DateTime.UtcNow,
            LocalTimestamp = DateTime.UtcNow,
            TimeZoneId = "UTC",
            ActivityTypeId = activity.Id,
            ActivityType = activity,
            Notes = "morning walk",
            Address = "123 Main St",
            StreetName = "Main St", AddressNumber = "001", ProviderAddressLine1 = "Independent line",
            ResolvedFeatureName = "Feature", ResolvedFeatureType = "building",
            ReverseGeocodingProvider = "geoapify", ReverseGeocodingStorageMode = "persistent",
            ReverseGeocodedAt = DateTimeOffset.UtcNow,
            Country = "USA",
            Region = "CA",
            Place = "LA"
        });
        db.Locations.Add(new Wayfarer.Models.Location
        {
            UserId = user.Id,
            Coordinates = new Point(2, 2) { SRID = 4326 },
            Timestamp = DateTime.UtcNow,
            LocalTimestamp = DateTime.UtcNow,
            TimeZoneId = "UTC",
            ActivityTypeId = null,
            Notes = "other",
            Address = "Other St",
            Country = "Canada",
            Region = "BC",
            Place = "Vancouver"
        });
        await db.SaveChangesAsync();

        var controller = BuildApiController(db, user);

        var result = await controller.Search(null, null, null, null, "walk", "morning", "Main", "usa", "ca", "LA");

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var dataProp = payload.GetType().GetProperty("Data");
        var data = Assert.IsAssignableFrom<IEnumerable<object>>(dataProp!.GetValue(payload)!);
        var row = Assert.Single(data);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(row);
        Assert.Equal("Main St", json.GetProperty("StreetName").GetString());
        Assert.Equal("001", json.GetProperty("AddressNumber").GetString());
        Assert.Equal("Independent line", json.GetProperty("ProviderAddressLine1").GetString());
        Assert.Equal("Feature", json.GetProperty("ResolvedFeatureName").GetString());
        Assert.Equal("building", json.GetProperty("ResolvedFeatureType").GetString());
        Assert.True(json.GetProperty("IsGeoapifyAddress").GetBoolean());
        var today = DateTime.UtcNow;
        var (timeline, _) = await new LocationService(db).GetLocationsByDateAsync(user.Id, "day", today.Year, today.Month, today.Day);
        var projected = Assert.Single(timeline, item => item.ProviderAddressLine1 == "Independent line");
        Assert.Equal("001", projected.AddressNumber);
        Assert.True(projected.IsGeoapifyAddress);
    }

}
