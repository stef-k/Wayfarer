using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wayfarer.Localization;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Middleware;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Checks negotiated Greek at the public MVC, Razor and cache boundaries.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class PublicWebLanguageHttpTests(PostgresImportTestFixture fixture) : TestBase
{
    /// <summary>Search and tags run through PostgreSQL while Razor retains the filters in view and page links.</summary>
    [PostgresFact]
    public async Task DiscoveryRetainsQueryAndPreviewNegotiates()
    {
        var owner = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        db.Attach(owner);
        var tag = new Tag { Id = Guid.NewGuid(), Slug = $"test-{Guid.NewGuid():N}", Name = $"<Trail> {Guid.NewGuid():N}" };
        fixture.RegisterTag(tag);
        var first = CreateTrip(owner, "Test A <map>", tag);
        var second = CreateTrip(owner, "Test B", tag);
        db.Trips.AddRange(first, second);
        await db.SaveChangesAsync();

        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), ConfigureTripServices);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
        client.DefaultRequestHeaders.Add("Cookie", WebLocales.CookieName + "=c=el-GR%7Cuic=el-GR");
        var filter = $"q=Test&tags={tag.Slug}&tagMode=any&sort=name_asc&pageSize=1";
        using var gridResponse = await client.GetAsync($"/Public/Trips?view=grid&{filter}&page=1");
        var grid = await ParseAsync(gridResponse);
        Assert.Equal("Δημόσια ταξίδια", grid.QuerySelector("main h1")?.TextContent);
        Assert.Contains(first.Name, grid.QuerySelector(".card-title")!.TextContent);
        Assert.Equal(tag.Name, grid.QuerySelector("[data-tag-chip] span")?.TextContent);
        Assert.Equal("Προβολή λίστας", grid.QuerySelector("[aria-label='Προβολή λίστας']")?.GetAttribute("title"));
        AssertFilterLink(grid.QuerySelector("[aria-label='Προβολή λίστας']")!, "list", tag.Slug, "1");
        AssertFilterLink(grid.QuerySelector(".pagination [aria-label='Επόμενη σελίδα']")!, "grid", tag.Slug, "2");

        using var listResponse = await client.GetAsync($"/Public/Trips?view=list&{filter}&page=2");
        var list = await ParseAsync(listResponse);
        Assert.Contains("Ταξίδι", list.QuerySelector("table thead")!.TextContent);
        Assert.Contains(second.Name, list.QuerySelector("table tbody")!.TextContent);
        Assert.DoesNotContain(first.Name, list.QuerySelector("table tbody")!.TextContent);

        using var redirect = await client.GetAsync($"/Public/Trips/tag/{tag.Slug}?view=list&sort=name_asc&page=2");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        var location = redirect.Headers.Location!.OriginalString;
        Assert.Equal("/Public/Trips", location.Split('?')[0]);
        var redirectQuery = QueryHelpers.ParseQuery(location.Split('?')[1]);
        Assert.Equal(tag.Slug, redirectQuery["tags"].ToString());
        Assert.Equal("list", redirectQuery["view"].ToString());
        Assert.Equal("name_asc", redirectQuery["sort"].ToString());
        Assert.Equal("2", redirectQuery["page"].ToString());

        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.AcceptLanguage.Clear();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el-CY, en-US;q=0.5");
        using var previewResponse = await client.GetAsync($"/Public/Trips/Preview/{first.Id}?culture=en-US");
        var preview = await ParseAsync(previewResponse);
        Assert.Equal("el-GR", Assert.Single(previewResponse.Content.Headers.ContentLanguage));
        Assert.True(previewResponse.Headers.CacheControl?.Private);
        Assert.True(previewResponse.Headers.CacheControl?.NoCache);
        Assert.Contains("Cookie", previewResponse.Headers.Vary);
        Assert.Contains("Accept-Language", previewResponse.Headers.Vary);
        Assert.Equal(first.Name, preview.QuerySelector("h2")?.TextContent);
        Assert.Equal("Περιγραφή", preview.QuerySelector("h3")?.TextContent);
        Assert.Contains("Authored <notes>", preview.QuerySelector("h3 + div")!.TextContent);
    }

    /// <summary>Both embeds negotiate per request and keep the existing framing policy.</summary>
    [Theory]
    [InlineData("en-US", true, "el-GR", "Λεπτομέρειες τοποθεσίας")]
    [InlineData("el-CY", false, "el-GR", "Λεπτομέρειες τοποθεσίας")]
    [InlineData("fr-FR", false, "en-US", "Location Details")]
    public async Task EmbedsUseDocumentLocale(string header, bool withCookie, string locale, string detail)
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(username: "public-owner", displayName: "Alex <Map>");
        owner.IsTimelinePublic = true;
        owner.PublicTimelineTimeThreshold = "now";
        var trip = CreateTrip(owner, "Authored Trip");
        db.Trips.Add(trip);
        db.ApplicationSettings.Add(new ApplicationSettings());
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), ConfigureTripServices);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(header);
        if (withCookie)
            client.DefaultRequestHeaders.Add("Cookie", WebLocales.CookieName + "=c=el-GR%7Cuic=el-GR");

        using var tripResponse = await client.GetAsync($"/Public/Trips/{trip.Id}?embed=true");
        using var timelineResponse = await client.GetAsync("/Public/Users/Timeline/public-owner/embed");
        var tripPage = await ParseAsync(tripResponse);
        var timeline = await ParseAsync(timelineResponse);
        foreach (var response in new[] { tripResponse, timelineResponse })
        {
            Assert.Equal(locale, Assert.Single(response.Content.Headers.ContentLanguage));
            BrowserResponsePolicyTests.AssertHeaders(response, true);
        }
        Assert.Equal(locale, tripPage.DocumentElement.GetAttribute("lang"));
        Assert.Equal(locale, timeline.DocumentElement.GetAttribute("lang"));
        Assert.NotNull(tripPage.QuerySelector("#trip-view[data-embed='true']"));
        Assert.Null(tripPage.QuerySelector("#language-open"));
        Assert.Null(timeline.QuerySelector("#language-open"));
        Assert.Equal(detail, timeline.QuerySelector("#locationModalLabel")?.TextContent);
        Assert.Equal(locale == "el-GR" ? "Χρονολόγιο του χρήστη Alex <Map>" : "Timeline of Alex <Map>",
            timeline.QuerySelector("h1")?.TextContent.Trim());
    }

    /// <summary>Missing and private documents have the same translated status, body and framing in both routes.</summary>
    [Fact]
    public async Task TimelineErrorsDoNotDisclosePrivateUsers()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(username: "private-owner");
        owner.TimelineTitle = "Private title";
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el");
        var bodies = new List<string>();
        foreach (var name in new[] { "private-owner", "missing" })
        foreach (var suffix in new[] { "", "/embed" })
        {
            using var reply = await client.GetAsync($"/Public/Users/Timeline/{name}{suffix}");
            Assert.Equal(HttpStatusCode.NotFound, reply.StatusCode);
            Assert.Equal("el-GR", Assert.Single(reply.Content.Headers.ContentLanguage));
            BrowserResponsePolicyTests.AssertHeaders(reply, false);
            bodies.Add(await reply.Content.ReadAsStringAsync());
        }

        Assert.All(bodies, body => Assert.Equal(bodies[0], body));
        var page = new HtmlParser().ParseDocument(bodies[0]);
        Assert.Equal("Το χρονολόγιο δεν είναι διαθέσιμο.", page.QuerySelector("h1")?.TextContent);
        Assert.DoesNotContain(owner.TimelineTitle, page.Body!.TextContent);
    }

    /// <summary>Both public layouts render a supplied title verbatim as text in a Greek document.</summary>
    [Fact]
    public async Task TimelinePreservesAuthoredHeading()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(username: "custom-owner");
        owner.IsTimelinePublic = true;
        owner.PublicTimelineTimeThreshold = "1d";
        owner.TimelineTitle = "Evening <Walk> & More";
        db.Users.Add(owner);
        db.ApplicationSettings.Add(new ApplicationSettings());
        await db.SaveChangesAsync();

        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el");
        foreach (var suffix in new[] { "", "/embed" })
        {
            using var response = await client.GetAsync($"/Public/Users/Timeline/custom-owner{suffix}");
            var page = await ParseAsync(response);
            var heading = page.QuerySelector("#timeline-header h1")!;
            Assert.Equal(owner.TimelineTitle, heading.TextContent);
            Assert.Empty(heading.Children);
            BrowserResponsePolicyTests.AssertHeaders(response, suffix.Length > 0);
        }
    }

    /// <summary>Public landing and registration copy render in Greek while the Privacy policy has its own language.</summary>
    [Fact]
    public async Task PublicPagesRenderGreekAndPrivacyMarksEnglishProse()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings());
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), configureApp: app =>
            app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}"));
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el");
        using var response = await client.GetAsync("/Home/Privacy");
        var page = await ParseAsync(response);
        Assert.Equal("el-GR", page.DocumentElement.GetAttribute("lang"));
        Assert.Equal("Πολιτική απορρήτου", page.QuerySelector("main h1")?.TextContent);
        Assert.Null(page.QuerySelector("main")?.GetAttribute("lang"));
        Assert.Contains("This instance of Wayfarer is deployed for", page.QuerySelector("main [lang='en']")!.TextContent);
        Assert.Contains("Αποθετήριο GitHub", page.QuerySelector("main")!.TextContent);

        using var homeResponse = await client.GetAsync("/");
        var home = await ParseAsync(homeResponse);
        Assert.Equal("Λογότυπο Wayfarer", home.QuerySelector(".landing-image-left")?.GetAttribute("alt"));
        Assert.Contains("οργανώστε τα ταξίδια σας", home.QuerySelector(".landing-subtitle")!.TextContent);

        using var registrationResponse = await client.GetAsync("/Home/RegistrationClosed");
        var registration = await ParseAsync(registrationResponse);
        Assert.Contains("Οι εγγραφές νέων χρηστών είναι προσωρινά κλειστές.", registration.QuerySelector("main")!.TextContent);
    }

    /// <summary>Only thumbnail generation is substituted, so document checks need no capture process.</summary>
    private static void ConfigureTripServices(IServiceCollection services)
    {
        services.AddSingleton<TripThumbnailStorage>();
        services.AddSingleton(Mock.Of<ITripThumbnailService>());
    }

    /// <summary>Registers Trip rows for cleanup using the existing relational fixture.</summary>
    private Trip CreateTrip(ApplicationUser owner, string name, Tag? tag = null)
    {
        var trip = new Trip { Id = Guid.NewGuid(), User = owner, UserId = owner.Id, Name = name, IsPublic = true,
            Notes = "<p>Authored &lt;notes&gt;</p>", UpdatedAt = DateTime.UtcNow };
        if (tag is not null) trip.Tags.Add(tag);
        if (tag is not null) fixture.RegisterTrip(trip.Id);
        return trip;
    }

    /// <summary>Checks the wire values carried between discovery states.</summary>
    private static void AssertFilterLink(IElement link, string view, string slug, string page)
    {
        var query = QueryHelpers.ParseQuery(link.GetAttribute("href")!.Split('?')[1]);
        Assert.Equal(view, query["view"].ToString());
        Assert.Equal(slug, query["tags"].ToString());
        Assert.Equal("Test", query["q"].ToString());
        Assert.Equal("any", query["tagMode"].ToString());
        Assert.Equal("name_asc", query["sort"].ToString());
        Assert.Equal("1", query["pageSize"].ToString());
        Assert.Equal(page, query["page"].ToString());
    }

    /// <summary>Reports the real routed failure body if an expected document fails.</summary>
    private static async Task<IDocument> ParseAsync(HttpResponseMessage response)
    {
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}\n{html}");
        return new HtmlParser().ParseDocument(html);
    }
}
