using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Wayfarer.Localization;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Exercises the real Razor layout and language action with browser-equivalent form posts.</summary>
public sealed class WebLanguageHttpTests : TestBase
{
    /// <summary>Cookie validation, quality order, primary-language fallback and query independence all reach Razor.</summary>
    [Theory]
    [InlineData("el", null, "el-GR")]
    [InlineData("EL-gr", null, "el-GR")]
    [InlineData("el-CY, en-US;q=0.5", null, "el-GR")]
    [InlineData("en;q=0.1, el;q=0.8", null, "el-GR")]
    [InlineData("el;q=0.8, en;q=0.8", null, "el-GR")]
    [InlineData("el;q=0, *;q=1", null, "en-US")]
    [InlineData("el;q=broken, en;q=0.5", null, "en-US")]
    [InlineData("el;q=0.9999, en-US;q=0.5", null, "en-US")]
    [InlineData("en-US;q=0.9999, el-CY;q=0.5", null, "el-GR")]
    [InlineData("el;q=1.0000, en-US;q=0.5", null, "en-US")]
    [InlineData("el;q=0.999, en-US;q=0.500", null, "el-GR")]
    [InlineData("el;q=0.999, en-US;q=1.000", null, "en-US")]
    [InlineData("el; Q=1., en-US;q=0.9", null, "el-GR")]
    [InlineData("el", "c=en-US%7Cuic=en-US", "en-US")]
    [InlineData("el", "c=en-US%7Cuic=el-GR", "el-GR")]
    [InlineData("el", "c=en-US", "el-GR")]
    [InlineData("el", "c=fr-FR%7Cuic=fr-FR", "el-GR")]
    public async Task NegotiationReachesRazorAndBootstrap(string header, string? cookie, string locale)
    {
        var db = CreateDbContext();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), IdentityRouteHost.WithStartupSettings);
        // Set the raw header at the request boundary so HttpClient cannot round malformed quality values.
        var context = await app.GetTestServer().SendAsync(http =>
        {
            var request = http.Request;
            request.Method = HttpMethods.Get;
            request.Scheme = "https";
            request.Host = new HostString("wayfarer.test");
            request.Path = "/Identity/Account/Login";
            request.QueryString = new QueryString("?culture=fr-FR&ui-culture=fr-FR");
            request.Headers.AcceptLanguage = header;
            if (cookie != null) request.Headers.Cookie = WebLocales.CookieName + "=" + cookie;
        });
        var response = context.Response;
        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        using var body = response.Body;
        var page = await new HtmlParser().ParseDocumentAsync(body);
        Assert.Equal(locale, page.DocumentElement.GetAttribute("lang"));
        Assert.Equal(locale, response.Headers.ContentLanguage.Single());
        Assert.Contains("private", response.Headers.GetCommaSeparatedValues("Cache-Control"));
        var vary = response.Headers.GetCommaSeparatedValues("Vary");
        Assert.Contains("Accept-Language", vary);
        Assert.Contains("Cookie", vary);
        Assert.Equal(locale, page.QuerySelector("#language-options input[checked]")?.GetAttribute("value"));
        Assert.Contains($"\"locale\":\"{locale}\"", page.QuerySelector("#wayfarer-locale")!.TextContent);
    }

    /// <summary>Identity login validation offers a clean GET with the original return URL.</summary>
    [Fact]
    public async Task LocalIdentityFailureKeepsFieldAndReturnUrl()
    {
        await using var app = await IdentityRouteHost.StartAsync(CreateDbContext(), CreateTestDirectory(),
            IdentityRouteHost.WithStartupSettings);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el");
        var token = await IdentityRouteHost.AntiforgeryAsync(client);
        const string target = "/Identity/Account/Login?returnUrl=%2Fdestination%3Fview%3Dmap";
        using var reply = await client.PostAsync(target, Form(token, ("Input.Username", "draft-name")));
        var page = await ParseAsync(reply);
        Assert.Equal("el-GR", page.DocumentElement.GetAttribute("lang"));
        Assert.Equal("Σύνδεση", page.QuerySelector("main h1")?.TextContent);
        Assert.Equal("draft-name", page.QuerySelector("#Input_Username")?.GetAttribute("value"));
        const string required = "Το πεδίο «Κωδικός πρόσβασης» είναι υποχρεωτικό.";
        Assert.Equal(required, page.QuerySelector("[data-valmsg-for='Input.Password']")?.TextContent);
        Assert.Equal(required, page.QuerySelector("#Input_Password")?.GetAttribute("data-val-required"));
        Assert.Equal("Κωδικός πρόσβασης", page.QuerySelector("label[for=Input_Password]")?.TextContent);
        Assert.Equal(target, page.QuerySelector("#language-dialog")?.GetAttribute("data-safe-get"));
        using var clean = await client.GetAsync(target);
        Assert.DoesNotContain("draft-name", await clean.Content.ReadAsStringAsync());

        using var error = await client.GetAsync("/Identity/Error");
        var errorPage = await ParseAsync(error);
        Assert.Equal("el-GR", errorPage.DocumentElement.GetAttribute("lang"));
        Assert.Equal("Σφάλμα", errorPage.QuerySelector("main h1")?.TextContent);
        Assert.Equal("el-GR", errorPage.QuerySelector("main h1")?.Closest("[lang]")?.GetAttribute("lang"));
        Assert.NotEmpty(errorPage.QuerySelector("main code")?.TextContent ?? "");
    }

    /// <summary>Switching languages after failed Login restores the session-expiry notice and return target.</summary>
    [Fact]
    public async Task LoginLanguageSwitchPreservesSessionEndedNoticeAndReturnTarget()
    {
        const string loginPath = "/Identity/Account/Login";
        await using var app = await IdentityRouteHost.StartAsync(CreateDbContext(), CreateTestDirectory(),
            IdentityRouteHost.WithStartupSettings);
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://wayfarer.test");
        var token = await IdentityRouteHost.AntiforgeryAsync(client);

        using var failed = await client.PostAsync(loginPath + "?sessionEnded=true&ReturnUrl=%2FUser%2FTimeline",
            Form(token, ("Input.Username", "draft-name")));
        var page = await ParseAsync(failed);
        Assert.Equal("en-US", page.DocumentElement.GetAttribute("lang"));
        Assert.NotEmpty(page.QuerySelector("[data-valmsg-for='Input.Password']")?.TextContent ?? "");
        var destination = page.QuerySelector("#language-dialog")?.GetAttribute("data-safe-get");
        Assert.NotNull(destination);
        Assert.StartsWith(loginPath + "?", destination);
        var query = QueryHelpers.ParseQuery(destination[loginPath.Length..]);
        Assert.Equal("true", query["sessionEnded"].ToString());
        Assert.Equal("/User/Timeline", query["ReturnUrl"].ToString());
        Assert.Equal(2, query.Count);

        var languageToken = page.QuerySelector("#language-form input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        using var preference = await client.PostAsync("/Language/Set", Form(languageToken, ("locale", "el-GR")));
        Assert.Equal(HttpStatusCode.NoContent, preference.StatusCode);
        SetCookies(client, ReadCookie(preference, WebLocales.CookieName + "="));

        using var clean = await client.GetAsync(destination);
        var greek = await ParseAsync(clean);
        Assert.Equal("el-GR", greek.DocumentElement.GetAttribute("lang"));
        Assert.Equal("el-GR", greek.QuerySelector("main h1")?.Closest("[lang]")?.GetAttribute("lang"));
        Assert.Contains("Η συνεδρία σας έληξε ή ο λογαριασμός σας έχει απενεργοποιηθεί.",
            greek.QuerySelector("#account .validation-summary-errors")?.TextContent);
        Assert.Equal("/Identity/Account/Register?returnUrl=%2FUser%2FTimeline",
            greek.QuerySelector("#account a")?.GetAttribute("href"));
    }

    /// <summary>Renders a package-managed page, a named-handler failure, and redirected status in the destination locale.</summary>
    [Fact]
    public async Task ManageEmailLocalizesNestedLayoutValidationAndRedirectStatus()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        user.Email = "owner@example.test";
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), IdentityRouteHost.WithStartupSettings);
        using var client = app.GetTestClient();
        var auth = IdentityRouteHost.Cookie(app.Services, user.Id);
        client.DefaultRequestHeaders.Add("Cookie", auth);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el");
        const string path = "/Identity/Account/Manage/Email";

        using var initial = await client.GetAsync(path);
        var page = await ParseAsync(initial);
        Assert.Equal("el-GR", page.DocumentElement.GetAttribute("lang"));
        Assert.Equal("Διαχείριση λογαριασμού", page.QuerySelector("main h1")?.TextContent);
        Assert.Equal("Διαχείριση email", page.QuerySelector("main h3")?.TextContent);
        Assert.Equal("el-GR", page.QuerySelector("main h3")?.Closest("[lang]")?.GetAttribute("lang"));
        Assert.Equal("Προσωπικά δεδομένα", page.QuerySelector("#personal-data")?.TextContent);
        Assert.Equal("owner@example.test", page.QuerySelector("#Email")?.GetAttribute("value"));

        var token = page.QuerySelector("#email-form input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        var antiforgery = ReadCookie(initial, ".AspNetCore.Antiforgery.");
        SetCookies(client, auth, antiforgery);
        var change = page.QuerySelector("#change-email-button")!.GetAttribute("formaction")!;
        using var rejected = await client.PostAsync(change, Form(token, ("Input.NewEmail", "invalid-address")));
        var invalid = await ParseAsync(rejected);
        const string message = "Το πεδίο «Νέο email» δεν περιέχει έγκυρη διεύθυνση email.";
        Assert.Equal(message, invalid.QuerySelector("#Input_NewEmail")?.GetAttribute("data-val-email"));
        Assert.Equal(message, invalid.QuerySelector("[data-valmsg-for='Input.NewEmail']")?.TextContent);
        Assert.Equal("invalid-address", invalid.QuerySelector("#Input_NewEmail")?.GetAttribute("value"));
        Assert.Equal(path, invalid.QuerySelector("#language-dialog")?.GetAttribute("data-safe-get"));

        client.DefaultRequestHeaders.AcceptLanguage.Clear();
        using var unchanged = await client.PostAsync(change, Form(token, ("Input.NewEmail", user.Email)));
        Assert.Equal(HttpStatusCode.Redirect, unchanged.StatusCode);
        SetCookies(client, auth, antiforgery, ReadCookie(unchanged, ".AspNetCore.Mvc.CookieTempDataProvider="));
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el");
        using var destination = await client.GetAsync(unchanged.Headers.Location);
        var status = (await ParseAsync(destination)).QuerySelector("main .alert-success");
        Assert.Contains("Η διεύθυνση email σας παραμένει ίδια.", status?.TextContent);
        Assert.Equal("Κλείσιμο", status?.QuerySelector("button")?.GetAttribute("aria-label"));

        const string downloadPath = "/Identity/Account/Manage/DownloadPersonalData";
        using var greekDownload = await client.PostAsync(downloadPath, Form(token));
        Assert.Equal(HttpStatusCode.OK, greekDownload.StatusCode);
        Assert.Equal("PersonalData.json", greekDownload.Content.Headers.ContentDisposition?.FileName);
        client.DefaultRequestHeaders.AcceptLanguage.Clear();
        using var englishDownload = await client.PostAsync(downloadPath, Form(token));
        Assert.Equal(HttpStatusCode.OK, englishDownload.StatusCode);
        Assert.Equal(greekDownload.Content.Headers.ContentDisposition, englishDownload.Content.Headers.ContentDisposition);
        Assert.Equal(await greekDownload.Content.ReadAsByteArrayAsync(), await englishDownload.Content.ReadAsByteArrayAsync());
    }

    /// <summary>Retains the wire cookie pair needed to follow an authenticated form and its status redirect.</summary>
    private static string ReadCookie(HttpResponseMessage response, string prefix) =>
        response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(prefix, StringComparison.Ordinal)).Split(';')[0];

    /// <summary>Sets an explicit test-browser cookie snapshot without following redirects automatically.</summary>
    private static void SetCookies(HttpClient client, params string[] values)
    {
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", values));
    }

    /// <summary>Bad credentials and noncanonical selections cannot set the preference.</summary>
    [Fact]
    public async Task PreferenceRequiresAntiforgeryAndCanonicalChoice()
    {
        var db = CreateDbContext();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), IdentityRouteHost.WithStartupSettings);
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://wayfarer.test");
        var token = await IdentityRouteHost.AntiforgeryAsync(client);
        foreach (var (tag, csrf) in new[] { ("el-GR", "invalid"), ("EL-gr", token), ("fr-FR", token) })
        {
            using var failure = await client.PostAsync("/Language/Set", Form(csrf, ("locale", tag)));
            Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
            Assert.False(failure.Headers.TryGetValues("Set-Cookie", out _));
        }

        using var success = await client.PostAsync("/Language/Set", Form(token, ("locale", "el-GR")));
        Assert.Equal(HttpStatusCode.NoContent, success.StatusCode);
        var preference = success.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith(".AspNetCore.Culture=c%3Del-GR%7Cuic%3Del-GR", preference);
        foreach (var attribute in new[] { "path=/", "httponly", "secure", "samesite=lax", "max-age=31536000" })
            Assert.Contains(attribute, preference, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", preference, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Timeline validation preserves entered values and advertises a GET independent of its POST URL.</summary>
    [Fact]
    public async Task TimelineValidationUsesOwnedGetAndPrivateTimelineAllowsGuardedSwitching()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(displayName: "Alice <North>");
        user.PublicTimelineTimeThreshold = "2.5w";
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), IdentityRouteHost.WithStartupSettings);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el-GR");
        var auth = IdentityRouteHost.Cookie(app.Services, user.Id);
        client.DefaultRequestHeaders.Add("Cookie", auth);
        using var settingsResponse = await client.GetAsync("/User/Timeline/Settings");
        var settings = await ParseAsync(settingsResponse);
        Assert.Equal("2.5w", settings.QuerySelector("#CustomThreshold")?.GetAttribute("value"));
        Assert.Equal("Ο τίτλος του χρονολογίου δεν μπορεί να υπερβαίνει τους 80 χαρακτήρες.",
            settings.QuerySelector("#TimelineTitle")?.GetAttribute("data-val-length"));
        Assert.Contains("Χρονολόγιο του χρήστη", settings.QuerySelector("main")?.TextContent);
        var token = settings.QuerySelector("#language-form input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        var csrfCookie = settingsResponse.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal)).Split(';')[0];
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"{auth}; {csrfCookie}");
        using var invalid = await client.PostAsync("/User/Timeline/UpdateSettings", Form(token,
            ("TimelineTitle", "Draft <North>"), ("IsTimelinePublic", "true"), ("PublicTimelineTimeThreshold", "now")));
        var page = await ParseAsync(invalid);
        Assert.Equal("/User/Timeline/Settings", page.QuerySelector("#language-dialog")?.GetAttribute("data-safe-get"));
        Assert.Equal("el-GR", page.DocumentElement.GetAttribute("lang"));
        Assert.Equal("Draft <North>", page.QuerySelector("#TimelineTitle")?.GetAttribute("value"));
        Assert.Contains("Επιβεβαιώστε τη ζωντανή δημόσια κοινοποίηση πριν από την αποθήκευση.",
            page.QuerySelector("[data-valmsg-for=ConfirmLivePublicTimeline]")?.TextContent);
        db.ChangeTracker.Clear();
        var persisted = db.Users.Single(u => u.Id == user.Id);
        Assert.False(persisted.IsTimelinePublic);
        Assert.Equal("2.5w", persisted.PublicTimelineTimeThreshold);
        Assert.Null(persisted.TimelineTitle);
        using var preference = await client.PostAsync("/Language/Set", Form(token, ("locale", "en-US")));
        Assert.Equal(HttpStatusCode.NoContent, preference.StatusCode);
        SetCookies(client, auth, csrfCookie, ReadCookie(preference, WebLocales.CookieName + "="));
        using var clean = await client.GetAsync("/User/Timeline/Settings");
        var cleanPage = await ParseAsync(clean);
        Assert.Equal("en-US", cleanPage.DocumentElement.GetAttribute("lang"));
        Assert.Empty(cleanPage.QuerySelector("#TimelineTitle")?.GetAttribute("value") ?? string.Empty);
        using var workspace = await client.GetAsync("/User/Timeline/Index");
        var live = await ParseAsync(workspace);
        Assert.Equal("", live.QuerySelector("#language-dialog")?.GetAttribute("data-safe-get"));
        Assert.Equal("Timeline of Alice <North>", live.QuerySelector("#timeline-header h1")?.TextContent);
    }

    /// <summary>Test-only registrations and resources take the same routing and Razor bootstrap path.</summary>
    [Fact]
    public async Task AdditionalLocaleResolvesRazorAndPluralBootstrapWithEnglishFallback()
    {
        var db = CreateDbContext();
        var probe = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "Resources", "WebLocaleProbe.json"))
            .Build().GetSection("WebLocales").Get<WebLocale[]>() ?? [];
        var builtIn = WebLocalization.ReadLocales(new ConfigurationBuilder().Build());
        var locales = new WebLocales(builtIn.All.Concat(probe));
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            IdentityRouteHost.WithStartupSettings(services);
            services.AddSingleton(locales);
            services.AddSingleton(provider => new WebMessages(locales, provider.GetRequiredService<ILogger<WebMessages>>(),
                typeof(WebLanguageHttpTests).Assembly));
        });
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pl;q=1, el;q=0.5");
        using var response = await client.GetAsync("/Identity/Account/Login");
        var page = await ParseAsync(response);
        Assert.Equal("pl-PL", page.DocumentElement.GetAttribute("lang"));
        Assert.Equal("Język", page.QuerySelector("#language-open")?.TextContent);
        Assert.Equal("Cancel", page.QuerySelector("#language-cancel")?.TextContent);
        using var bootstrap = JsonDocument.Parse(page.QuerySelector("#wayfarer-locale")!.TextContent);
        var active = bootstrap.RootElement.GetProperty("messages");
        Assert.Equal("Masz {0} oczekujące zaproszenia.", active.GetProperty("Shell.InvitationsPending.few").GetString());
        Assert.False(active.TryGetProperty("Shell.Cancel", out _));
        Assert.Equal("Cancel", bootstrap.RootElement.GetProperty("english").GetProperty("Shell.Cancel").GetString());
        var polish = Assert.Single(probe);
        Assert.Equal("few", polish.PluralCategory(3));
        Assert.Equal("many", polish.PluralCategory(12));
        Assert.Equal("many", polish.PluralCategory(0));
        Assert.Equal("one", polish.PluralCategory(1));
    }

    /// <summary>My Trips and both Vue entries share the resolved document locale. Create can discard through its owned GET.</summary>
    [Fact]
    public async Task TripDocumentsShareLocaleAndCreateRetainsInvalidDraft()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            IdentityRouteHost.WithStartupSettings(services);
            services.AddSingleton(Moq.Mock.Of<Wayfarer.Services.ICacheWarmupScheduler>());
            services.AddSingleton(Moq.Mock.Of<Wayfarer.Services.ITripMapThumbnailGenerator>());
            services.AddSingleton(Moq.Mock.Of<Wayfarer.Services.ITripTagService>());
        });
        using var client = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(client, app.Services, user.Id);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el-GR");
        foreach (var (path, title) in new[]
        {
            ("/User/Trip", "Τα ταξίδια μου"),
            ("/User/Trip/Compose", "Σύγκριση και συνδυασμός ταξιδιών"),
            ("/User/Trip/Copy", "Αντιγραφή σε υπάρχον ταξίδι")
        })
        {
            using var response = await client.GetAsync(path);
            var page = await ParseAsync(response);
            Assert.Equal("el-GR", page.DocumentElement!.GetAttribute("lang"));
            Assert.Contains(title, page.Title);
            Assert.Equal("true", page.QuerySelector("#language-dialog")?.GetAttribute("data-workspace"));
            using var bootstrap = JsonDocument.Parse(page.QuerySelector("#wayfarer-locale")!.TextContent);
            Assert.Equal("el-GR", bootstrap.RootElement.GetProperty("locale").GetString());
        }
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(client);
        using var invalid = await client.PostAsync("/User/Trip/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = "", ["Notes"] = "Δοκιμή <Trail>", ["submitAction"] = "save"
        }));
        var create = await ParseAsync(invalid);
        Assert.Contains("Το όνομα είναι υποχρεωτικό.", create.QuerySelector(".validation-summary-errors")?.TextContent);
        Assert.Equal("Δοκιμή <Trail>", create.QuerySelector("#Notes")?.TextContent.Trim());
        Assert.Equal("/User/Trip/Create", create.QuerySelector("#language-dialog")?.GetAttribute("data-safe-get"));
        Assert.Empty(db.Trips);
    }

    /// <summary>Navigation uses the same locale in each authenticated role without widening menu authority.</summary>
    [Theory]
    [InlineData("User", "Chronological", "Users")]
    [InlineData("Manager", "Manager/Users", "Admin/Jobs")]
    [InlineData("Admin", "Admin/Jobs", "Manager/Users")]
    public async Task RoleNavigationSharesLocale(string role, string expected, string unrelated)
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), IdentityRouteHost.WithStartupSettings);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Cookie", IdentityRouteHost.Cookie(app.Services, user.Id, role));
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el-GR");
        using var reply = await client.GetAsync("/Identity/Account/Login");
        var page = await ParseAsync(reply);
        Assert.Equal("Γλώσσα", page.QuerySelector("#language-open")?.TextContent);
        var links = page.QuerySelectorAll("#mainNavbar a").Select(anchor => anchor.GetAttribute("href")).OfType<string>().ToArray();
        Assert.Contains(links, href => href.Contains(expected, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(links, href => href.Contains(unrelated, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Submits browser fields with the form's antiforgery token.</summary>
    private static FormUrlEncodedContent Form(string token, params (string Name, string Value)[] fields) =>
        new(fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value))
            .Append(new("__RequestVerificationToken", token)));

    /// <summary>Reads a successful routed HTML document for semantic assertions.</summary>
    private static async Task<IDocument> ParseAsync(HttpResponseMessage response)
    {
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}\n{html}");
        return new HtmlParser().ParseDocument(html);
    }
}
