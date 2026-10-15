using System;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Location = Wayfarer.Models.Location;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Ensures LocationExportController exports only the signed-in user's data.
/// </summary>
public class LocationExportControllerTests : TestBase
{
    /// <summary>Optional types retain nonempty text, including records with no other GPX extension data.</summary>
    [Theory]
    [InlineData(LocationImportFileType.Csv)]
    [InlineData(LocationImportFileType.WayfarerGeoJson)]
    [InlineData(LocationImportFileType.Gpx)]
    [InlineData(LocationImportFileType.Kml)]
    public async Task LocationType_RoundTripsOptionalValuesAndFieldOnlyRecords(LocationImportFileType format)
    {
        using var db = CreateDbContext();
        string?[] values = [null, string.Empty, " \t\r\n ", " MiXeD, <&> \"\t\u00a0\r ",
            "Manual", "locationTypeUtf8Base64V1:AA==", "WayfarerLocationTypeUtf8Base64V1", "🚀"];
        for (var i = 0; i < values.Length; i++)
            db.Locations.Add(new Location
            {
                UserId = "u1", TimeZoneId = string.Empty, LocationType = values[i],
                Timestamp = DateTime.UnixEpoch.AddHours(i), LocalTimestamp = DateTime.UnixEpoch.AddHours(i),
                Coordinates = new Point(22, 40) { SRID = 4326 }
            });
        await db.SaveChangesAsync();
        var controller = BuildController(db, "u1");
        var file = Assert.IsType<FileStreamResult>(format switch
        {
            LocationImportFileType.Csv => await controller.Csv(),
            LocationImportFileType.WayfarerGeoJson => await controller.GeoJson(),
            LocationImportFileType.Gpx => await controller.Gpx(),
            _ => await controller.Kml()
        });
        await using var stream = file.FileStream;
        if (format is LocationImportFileType.Gpx or LocationImportFileType.Kml)
        {
            // Ordinary XML-compatible text keeps its existing field and never acquires an encoding marker.
            var xml = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            XNamespace ns = format == LocationImportFileType.Gpx
                ? "http://www.topografix.com/GPX/1/1" : "http://www.opengis.net/kml/2.2";
            XNamespace wf = "https://wayfarer.app/schemas/gpx";
            var fields = format == LocationImportFileType.Gpx ? xml.Descendants(wf + "locationType")
                : xml.Descendants(ns + "Data").Where(element => (string?)element.Attribute("name") == "LocationType");
            Assert.Equal(values.Skip(2), fields.Select(element => format == LocationImportFileType.Gpx
                ? element.Value : element.Element(ns + "value")!.Value));
            Assert.Empty(xml.Descendants(wf + "locationTypeUtf8Base64V1"));
            Assert.DoesNotContain(xml.Descendants(ns + "Data"),
                element => (string?)element.Attribute("name") == "WayfarerLocationTypeUtf8Base64V1");
            stream.Position = 0;
        }
        var parser = new LocationDataParserFactory(NullLoggerFactory.Instance).GetParser(format);
        var parsed = await parser.ParseAsync(stream, "destination");

        Assert.Equal(values.Select(value => string.IsNullOrEmpty(value) ? null : value), parsed.Select(row => row.LocationType));
        Assert.All(parsed, row => Assert.Null(row.ImportedActivityName));
    }

    /// <summary>Invalid UTF-16 cannot silently become replacement characters in the UTF-8 fallback.</summary>
    [Theory]
    [InlineData(LocationImportFileType.Gpx)]
    [InlineData(LocationImportFileType.Kml)]
    public async Task XmlLocationType_RejectsInvalidUnicodeInsteadOfReplacingIt(LocationImportFileType format)
    {
        using var db = CreateDbContext();
        var location = CreateLocation("u1", "Synthetic");
        location.LocationType = "\ud800";
        db.Locations.Add(location);
        await db.SaveChangesAsync();
        var controller = BuildController(db, "u1");

        await Assert.ThrowsAsync<EncoderFallbackException>(() => format == LocationImportFileType.Gpx
            ? controller.Gpx() : controller.Kml());
    }

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
        // Native writers preserve carriage returns with XML character references where required.
        Assert.Equal(original.ProviderAddressLine1, saved.ProviderAddressLine1);
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
