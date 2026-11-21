using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Exercises authenticated Area 4 routes with their real layout and antiforgery.</summary>
public sealed class SettingsWebLanguageHttpTests : TestBase
{
    /// <summary>Greek Hidden Area editing round-trips the form's unchanged WKT and preserves precise geometry.</summary>
    [Fact]
    public async Task GreekHiddenAreaEditPreservesWktCoordinatesAndSrid()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "alice", username: "alice");
        var start = new Coordinate(23.1234567890123, 37.6543210987654);
        var polygon = new GeometryFactory(new PrecisionModel(), 4326).CreatePolygon(
            [start, new(23.2345678901234, 37.6543210987654), new(23.25, 37.7), start.Copy()]);
        var area = new HiddenArea { User = owner, UserId = owner.Id, Name = "Home <N>", Description = "Keep <text>", Area = polygon };
        db.Add(owner);
        db.Add(area);
        await db.SaveChangesAsync();

        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), IdentityRouteHost.WithStartupSettings);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el-GR");
        var auth = IdentityRouteHost.Cookie(app.Services, owner.Id);
        client.DefaultRequestHeaders.Add("Cookie", auth);
        using var response = await client.GetAsync($"/User/HiddenAreas/Edit/{area.Id}");
        var page = await ParseAsync(response);
        Assert.Contains("Επεξεργασία κρυφής ζώνης", page.QuerySelector("main h2")?.TextContent);
        Assert.NotNull(page.QuerySelector("#language-open"));
        Assert.NotNull(page.QuerySelector("link[href*='leaflet.draw.css']"));
        var form = Assert.IsAssignableFrom<IElement>(page.QuerySelector("form[action*='HiddenAreas/Edit']"));
        var csrfCookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal)).Split(';')[0];
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"{auth}; {csrfCookie}");
        var token = form.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        var shape = form.QuerySelector("#areaInput")!.GetAttribute("value")!;
        Assert.Contains("23.1234567890123 37.6543210987654", shape);
        using var submission = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Id"] = area.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Name"] = "Renamed <N>",
            ["Description"] = "Keep <text>",
            ["AreaWKT"] = shape
        });
        using var saved = await client.PostAsync(form.GetAttribute("action"), submission);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var tempData = saved.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(".AspNetCore.Mvc.CookieTempDataProvider=", StringComparison.Ordinal)).Split(';')[0];
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"{auth}; {csrfCookie}; {tempData}");
        using var destination = await client.GetAsync(saved.Headers.Location);
        var index = await ParseAsync(destination);
        Assert.Contains("Renamed <N>", index.QuerySelector("main")?.TextContent);
        Assert.Contains("Η κρυφή ζώνη ενημερώθηκε.", index.QuerySelector(".site-content [role=alert]")?.TextContent);
        db.ChangeTracker.Clear();
        var persisted = await db.HiddenAreas.SingleAsync();
        Assert.Equal("Renamed <N>", persisted.Name);
        Assert.Equal("Keep <text>", persisted.Description);
        Assert.Equal(4326, persisted.Area.SRID);
        Assert.True(polygon.EqualsExact(persisted.Area));
    }

    /// <summary>The localized token document withholds switching and renders no one-time material.</summary>
    [Fact]
    public async Task GreekConnectionPageWithholdsSelector()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        db.Add(owner);
        await db.SaveChangesAsync();

        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), IdentityRouteHost.WithStartupSettings);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el-GR");
        client.DefaultRequestHeaders.Add("Cookie", IdentityRouteHost.Cookie(app.Services, owner.Id));
        using var response = await client.GetAsync("/User/ApiToken");
        var page = await ParseAsync(response);
        Assert.Contains("Σύνδεση εφαρμογών στο Wayfarer", page.QuerySelector("main h1")?.TextContent);
        Assert.Null(page.QuerySelector("#language-open"));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Empty(page.QuerySelector("#connection-token")?.TextContent ?? string.Empty);
        Assert.Empty(page.QuerySelector("#connection-qr")?.Children ?? throw new InvalidOperationException("QR container missing"));
    }

    /// <summary>Reads a successful routed HTML document using the test suite's renderer seam.</summary>
    private static async Task<IDocument> ParseAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync());
    }
}
