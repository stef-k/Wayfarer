using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
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
        db.ApplicationSettings.Add(new ApplicationSettings());
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
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

    /// <summary>Bad credentials and noncanonical selections cannot set the preference.</summary>
    [Fact]
    public async Task PreferenceRequiresAntiforgeryAndCanonicalChoice()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings());
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
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
    public async Task TimelineValidationUsesOwnedGetAndProtectedTimelineWithholdsSwitching()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings());
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        var auth = IdentityRouteHost.Cookie(app.Services, user.Id);
        client.DefaultRequestHeaders.Add("Cookie", auth);
        using var settingsResponse = await client.GetAsync("/User/Timeline/Settings");
        var settings = await ParseAsync(settingsResponse);
        var token = settings.QuerySelector("#language-form input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        var csrfCookie = settingsResponse.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal)).Split(';')[0];
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"{auth}; {csrfCookie}");
        using var invalid = await client.PostAsync("/User/Timeline/UpdateSettings", Form(token,
            ("TimelineTitle", new string('x', 81)), ("PublicTimelineTimeThreshold", "custom"), ("CustomPublicTimelineTimeThreshold", "invalid")));
        var page = await ParseAsync(invalid);
        Assert.Equal("/User/Timeline/Settings", page.QuerySelector("#language-dialog")?.GetAttribute("data-safe-get"));
        using var preference = await client.PostAsync("/Language/Set", Form(token, ("locale", "el-GR")));
        Assert.Equal(HttpStatusCode.NoContent, preference.StatusCode);
        using var clean = await client.GetAsync("/User/Timeline/Settings");
        Assert.DoesNotContain(new string('x', 81), await clean.Content.ReadAsStringAsync());
        using var workspace = await client.GetAsync("/User/Timeline/Index");
        Assert.Null((await ParseAsync(workspace)).QuerySelector("#language-open"));
    }

    /// <summary>Test-only registrations and resources take the same routing and Razor bootstrap path.</summary>
    [Fact]
    public async Task AdditionalLocaleResolvesRazorAndPluralBootstrapWithEnglishFallback()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings());
        await db.SaveChangesAsync();
        var probe = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "Resources", "WebLocaleProbe.json"))
            .Build().GetSection("WebLocales").Get<WebLocale[]>() ?? [];
        var builtIn = WebLocalization.ReadLocales(new ConfigurationBuilder().Build());
        var locales = new WebLocales(builtIn.All.Concat(probe));
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
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

    /// <summary>Navigation uses the same locale in each authenticated role without widening menu authority.</summary>
    [Theory]
    [InlineData("User", "Chronological", "Users")]
    [InlineData("Manager", "Manager/Users", "Admin/Jobs")]
    [InlineData("Admin", "Admin/Jobs", "Manager/Users")]
    public async Task RoleNavigationSharesLocale(string role, string expected, string unrelated)
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings());
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
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

    private static FormUrlEncodedContent Form(string token, params (string Name, string Value)[] fields) =>
        new(fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value))
            .Append(new("__RequestVerificationToken", token)));

    private static async Task<IDocument> ParseAsync(HttpResponseMessage response)
    {
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}\n{html}");
        return new HtmlParser().ParseDocument(html);
    }
}
