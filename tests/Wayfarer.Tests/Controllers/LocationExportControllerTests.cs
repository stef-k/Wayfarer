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

    [Fact]
    public async Task Gpx_ExportsOnlyCurrentUserLocations()
    {
        var db = CreateDbContext();
        var current = TestDataFixtures.CreateUser(id: "u1", username: "alice");
        var other = TestDataFixtures.CreateUser(id: "u2", username: "bob");
        db.Users.AddRange(current, other);
        db.Locations.AddRange(
            CreateLocation(current.Id, "AliceGpx"),
            CreateLocation(other.Id, "BobGpx"));
        await db.SaveChangesAsync();
        var controller = BuildController(db, current.Id);

        var result = await controller.Gpx();

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/gpx+xml", file.ContentType);
        using var reader = new StreamReader(file.FileStream, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync();
        Assert.Contains("AliceGpx", payload);
        Assert.DoesNotContain("BobGpx", payload);
    }

    /// <summary>Backend history formats retain independent provider text and historical address values.</summary>
    [Theory]
    [InlineData("geojson", "\n")]
    [InlineData("csv", "\n")]
    [InlineData("gpx", "\n")]
    [InlineData("kml", "\n")]
    [InlineData("geojson", "\r\n")]
    [InlineData("csv", "\r\n")]
    [InlineData("gpx", "\r\n")]
    [InlineData("kml", "\r\n")]
    public async Task RetainedProviderLineRoundTrips(string format, string newline)
    {
        using var db = CreateDbContext();
        var original = CreateLocation("u1", "Synthetic");
        original.ProviderAddressLine1 = $"Hotel{newline}Main\tStreet & \"Οδός\", 10-12";
        original.FullAddress = "Historical display";
        original.Address = "Historical feature-bearing line";
        original.ReverseGeocodingProvider = "geoapify";
        original.ReverseGeocodingStorageMode = "persistent";
        original.ReverseGeocodedAt = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        db.Locations.Add(original);
        await db.SaveChangesAsync();
        var controller = BuildController(db, "u1");
        var file = Assert.IsType<FileStreamResult>(format switch
        {
            "geojson" => await controller.GeoJson(), "csv" => await controller.Csv(),
            "gpx" => await controller.Gpx(), _ => await controller.Kml()
        });
        Wayfarer.Parsers.ILocationDataParser parser = format switch
        {
            "geojson" => new Wayfarer.Parsers.WayfarerGeoJsonParser(Microsoft.Extensions.Logging.Abstractions.NullLogger<Wayfarer.Parsers.WayfarerGeoJsonParser>.Instance),
            "csv" => new Wayfarer.Parsers.CsvLocationParser(Microsoft.Extensions.Logging.Abstractions.NullLogger<Wayfarer.Parsers.CsvLocationParser>.Instance),
            "gpx" => new Wayfarer.Parsers.GpxLocationParser(Microsoft.Extensions.Logging.Abstractions.NullLogger<Wayfarer.Parsers.GpxLocationParser>.Instance),
            _ => new Wayfarer.Parsers.KmlLocationParser(Microsoft.Extensions.Logging.Abstractions.NullLogger<Wayfarer.Parsers.KmlLocationParser>.Instance)
        };
        var imported = new List<Location>();
        await foreach (var row in parser.ParseAsync(file.FileStream, "import-owner")) imported.Add(row);
        var saved = Assert.Single(imported);
        // XML element text normalizes line endings to LF; JSON and quoted CSV preserve them.
        var expectedLine = format is "gpx" or "kml"
            ? original.ProviderAddressLine1.Replace("\r\n", "\n") : original.ProviderAddressLine1;
        Assert.Equal(expectedLine, saved.ProviderAddressLine1);
        Assert.Equal(original.FullAddress, saved.FullAddress);
        Assert.Equal(original.Address, saved.Address);
        Assert.Equal(("geoapify", "persistent", original.ReverseGeocodedAt),
            (saved.ReverseGeocodingProvider, saved.ReverseGeocodingStorageMode, saved.ReverseGeocodedAt));
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
