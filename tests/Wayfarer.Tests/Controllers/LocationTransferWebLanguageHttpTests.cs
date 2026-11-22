using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Point = NetTopologySuite.Geometries.Point;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Checks localized download feedback and language-independent export bytes at the routed owner.</summary>
public sealed class LocationTransferWebLanguageHttpTests : TestBase
{
    /// <summary>GeoJSON format, recovery metadata, authored values and filename convention survive a locale change.</summary>
    [Fact]
    public async Task GreekGeoJsonRetainsDownloadContract()
    {
        var db = CreateDbContext();
        var location = SeedLocation(db);
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), IdentityRouteHost.WithStartupSettings);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Cookie", IdentityRouteHost.Cookie(app.Services, location.UserId));
        using var english = await client.GetAsync("/LocationExport/GeoJson");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el-GR");
        using var greek = await client.GetAsync("/LocationExport/GeoJson");
        foreach (var response in new[] { english, greek })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/geo+json", response.Content.Headers.ContentType?.MediaType);
            var disposition = Assert.IsType<System.Net.Http.Headers.ContentDispositionHeaderValue>(response.Content.Headers.ContentDisposition);
            Assert.Equal("attachment", disposition.DispositionType);
            var fileName = Assert.IsType<string>(disposition.FileNameStar);
            Assert.StartsWith("Wayfarer_Locations_Export_", fileName);
            Assert.EndsWith(".geojson", fileName);
        }
        var bytes = await english.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes, await greek.Content.ReadAsByteArrayAsync());
        var json = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("[23.75,37.25]", json);
        Assert.Contains("Διεύθυνση <test>", json);
        Assert.Contains("\"WayfarerRecovery\":{\"version\":1,\"kind\":\"effective\"", json);
    }

    /// <summary>An XML serialization failure reaches the localized download error page with the original status.</summary>
    [Fact]
    public async Task FailedExportShowsGreekDownloadFeedback()
    {
        var db = CreateDbContext();
        var location = SeedLocation(db, "Invalid XML \u0001");
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(),
            IdentityRouteHost.WithStartupSettings, configureApp: app => app.UseExceptionHandler("/Home/Error"));
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Cookie", IdentityRouteHost.Cookie(app.Services, location.UserId));
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el-GR");
        using var response = await client.GetAsync("/LocationExport/Gpx");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var page = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync());
        Assert.Contains("Δεν ήταν δυνατή η εξαγωγή του ιστορικού τοποθεσιών", page.QuerySelector("main h2")?.TextContent);
        Assert.DoesNotContain("Invalid XML", page.Body?.TextContent);
    }

    /// <summary>Seeds a representative owner's history with stable authored data and coordinate precision.</summary>
    private static Location SeedLocation(ApplicationDbContext db, string address = "Διεύθυνση <test>")
    {
        var owner = TestDataFixtures.CreateUser(id: "transfer-owner", username: "transfer-owner");
        var instant = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var location = new Location { UserId = owner.Id, Timestamp = instant, LocalTimestamp = instant,
            TimeZoneId = "UTC", Coordinates = new Point(23.75, 37.25) { SRID = 4326 }, FullAddress = address };
        db.AddRange(owner, location);
        db.SaveChanges();
        return location;
    }
}
