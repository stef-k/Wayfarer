using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// User location export flows.
/// </summary>
public class LocationExportControllerTests : TestBase
{
    [Fact]
    public async Task GeoJson_ExportsOnlyCurrentUserLocations()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        var other = TestDataFixtures.CreateUser(id: "u2", username: "bob");
        db.Users.AddRange(user, other);
        db.Locations.AddRange(
            CreateLoc(user.Id, 1, 1, "CA"),
            CreateLoc(other.Id, 2, 2, "TX"));
        await db.SaveChangesAsync();

        var controller = BuildController(db, user);

        var result = await controller.GeoJson();

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/geo+json", file.ContentType);
        using var reader = new StreamReader(file.FileStream, Encoding.UTF8, leaveOpen: true);
        var json = reader.ReadToEnd();
        Assert.Contains("\"Region\":\"CA\"", json);
        Assert.DoesNotContain("TX", json);
    }

    [Fact]
    public async Task Csv_ExportsOnlyCurrentUserLocations()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        var other = TestDataFixtures.CreateUser(id: "u2", username: "bob");
        db.Users.AddRange(user, other);
        db.Locations.AddRange(
            CreateLoc(user.Id, 1, 1, "CA"),
            CreateLoc(other.Id, 2, 2, "TX"));
        await db.SaveChangesAsync();

        var controller = BuildController(db, user);

        var result = await controller.Csv();

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("text/csv", file.ContentType);
        using var reader = new StreamReader(file.FileStream, Encoding.UTF8, leaveOpen: true);
        var csv = reader.ReadToEnd();
        Assert.Contains("CA", csv);
        Assert.DoesNotContain("TX", csv);
    }

    private static LocationExportController BuildController(ApplicationDbContext db, ApplicationUser user)
    {
        var controller = new LocationExportController(db);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id),
                new Claim(ClaimTypes.Name, user.UserName!)
            }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static Wayfarer.Models.Location CreateLoc(string userId, double x, double y, string region)
    {
        return new Wayfarer.Models.Location
        {
            UserId = userId,
            Coordinates = new Point(x, y) { SRID = 4326 },
            Timestamp = DateTime.UtcNow,
            LocalTimestamp = DateTime.UtcNow,
            TimeZoneId = "UTC",
            Region = region
        };
    }
}
