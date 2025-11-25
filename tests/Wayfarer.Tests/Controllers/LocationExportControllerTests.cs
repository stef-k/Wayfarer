using System;
using System.IO;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Location = Wayfarer.Models.Location;
using Wayfarer.Models;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Ensures LocationExportController exports only the signed-in user's data.
/// </summary>
public class LocationExportControllerTests : TestBase
{
    [Fact]
    public async Task GeoJson_ExportsOnlyCurrentUserLocations()
    {
        var db = CreateDbContext();
        var current = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        var other = TestDataFixtures.CreateUser(id: "u2", username: "bob");
        db.Users.AddRange(current, other);
        db.Locations.AddRange(
            CreateLocation(current.Id, "Main St"),
            CreateLocation(other.Id, "ShouldNotAppear"));
        await db.SaveChangesAsync();
        var controller = BuildController(db, current.Id);

        var result = await controller.GeoJson();

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/geo+json", file.ContentType);
        using var reader = new StreamReader(file.FileStream, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync();
        Assert.Contains("Main St", payload);
        Assert.DoesNotContain("ShouldNotAppear", payload);
    }

    [Fact]
    public async Task Csv_ExportsOnlyCurrentUserLocations()
    {
        var db = CreateDbContext();
        var current = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        var other = TestDataFixtures.CreateUser(id: "u2", username: "bob");
        db.Users.AddRange(current, other);
        db.Locations.AddRange(
            CreateLocation(current.Id, "AlicePlace"),
            CreateLocation(other.Id, "BobPlace"));
        await db.SaveChangesAsync();
        var controller = BuildController(db, current.Id);

        var result = await controller.Csv();

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("text/csv", file.ContentType);
        using var reader = new StreamReader(file.FileStream, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync();
        Assert.Contains("AlicePlace", payload);
        Assert.DoesNotContain("BobPlace", payload);
    }

    [Fact]
    public async Task Kml_ExportsOnlyCurrentUserLocations()
    {
        var db = CreateDbContext();
        var current = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        var other = TestDataFixtures.CreateUser(id: "u2", username: "bob");
        db.Users.AddRange(current, other);
        db.Locations.AddRange(
            CreateLocation(current.Id, "AliceKml"),
            CreateLocation(other.Id, "BobKml"));
        await db.SaveChangesAsync();
        var controller = BuildController(db, current.Id);

        var result = await controller.Kml();

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/vnd.google-earth.kml+xml", file.ContentType);
        using var reader = new StreamReader(file.FileStream, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync();
        Assert.Contains("AliceKml", payload);
        Assert.DoesNotContain("BobKml", payload);
    }

    private static LocationExportController BuildController(ApplicationDbContext db, string userId)
    {
        var controller = new LocationExportController(db);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Name, "test-user")
            }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static Location CreateLocation(string userId, string? notes)
    {
        return new Location
        {
            UserId = userId,
            Coordinates = new Point(23.72, 37.98) { SRID = 4326 },
            Timestamp = DateTime.UtcNow,
            LocalTimestamp = DateTime.UtcNow,
            TimeZoneId = "UTC",
            Notes = notes,
            Place = notes
        };
    }
}
