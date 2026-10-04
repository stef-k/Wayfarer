using System.Net;
using System.Security.Claims;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Exercises production settings routing, Razor tokens, read-only navigation and bounded migration results.</summary>
public sealed class ProviderSettingsMigrationRouteTests : TestBase
{
    private const string LegacySecret = "legacy-settings-privacy-sentinel";
    private const string OtherSecret = "other-owner-privacy-sentinel";
    private const string Root = "/User/LocationProviderSettings";

    /// <summary>Every presentation state reads durable state without saving, repairing, retiring or contacting providers.</summary>
    [Theory]
    [InlineData("pending", "Legacy Mapbox migration pending")]
    [InlineData("migrated", "Mapbox is not selected")]
    [InlineData("cleanup", "verification")]
    [InlineData("conflict", "Conflicting stored values were retained")]
    [InlineData("unavailable", "cannot currently be read or trusted")]
    [InlineData("revoked", "Mapbox remains revoked")]
    public async Task Get_IsReadOnlyAndCredentialFree(string state, string message)
    {
        var saves = new RejectUnexpectedSave();
        var options = Options(saves);
        await using var db = Context(options);
        var credentials = CredentialTestFactory.Create(new EphemeralDataProtectionProvider());
        await SeedAsync(db, credentials, state);
        var before = await SnapshotAsync(options);
        saves.Reject = true;
        using var logs = new TestLogProvider();
        var http = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        using var providerClient = new HttpClient(handler.Object, disposeHandler: false);
        http.Setup(factory => factory.CreateClient(It.IsAny<string>())).Returns(providerClient);
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddSingleton(credentials);
            services.AddSingleton(http.Object);
            services.AddLogging(builder => builder.AddProvider(logs));
        });
        using var client = app.GetTestClient();
        Cookie(client, app.Services);
        foreach (var route in new[] { Root, Root + "/Index/other" })
        {
            using var page = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var text = await page.Content.ReadAsStringAsync();
            Assert.Contains(message, text);
            Assert.DoesNotContain("No provider contact is authorized", text);
            if (state == "conflict") Assert.Contains("Ready with mapbox.", text);
            AssertPrivate(text, db);
            var html = await new HtmlParser().ParseDocumentAsync(text);
            Assert.Equal(state is not ("migrated" or "cleanup"),
                html.QuerySelector("form[action*='MigrateLegacyMapbox']") != null);
            Assert.Equal(before, await SnapshotAsync(options));
        }
        using var getCommand = await client.GetAsync(Root + "/MigrateLegacyMapbox/other");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, getCommand.StatusCode);
        using var postIndex = await client.PostAsync(Root + "/Index", null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, postIndex.StatusCode);
        Assert.Equal(before, await SnapshotAsync(options));
        AssertPrivate(string.Join("\n", logs.Entries.Select(entry => entry.Message + entry.Exception
            + string.Join(" ", entry.Fields.Select(field => field.Value?.ToString())))), db);
        handler.VerifyNoOtherCalls();
    }

    /// <summary>A genuine Razor token is required, and submitted route/form ownership never overrides the cookie claim.</summary>
    [Fact]
    public async Task Post_RequiresAuthenticTokenAndMigratesOnlyClaimOwner()
    {
        var options = Options();
        await using var db = Context(options);
        var credentials = CredentialTestFactory.Create(new EphemeralDataProtectionProvider());
        await SeedAsync(db, credentials, "pending");
        var before = await SnapshotAsync(options);
        using var logs = new TestLogProvider();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddSingleton(credentials);
            services.AddLogging(builder => builder.AddProvider(logs));
        });
        using var client = app.GetTestClient();
        Cookie(client, app.Services);
        using var page = await client.GetAsync(Root);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync());
        // Pages owns shared Docs navigation and the canonical personal-provider user guide.
        foreach (var selector in new[] { "#mainNavbar a", ".site-footer a" })
            Assert.Equal("https://stef-k.github.io/Wayfarer/", Assert.Single(html.QuerySelectorAll(selector),
                link => link.TextContent.Trim() == "Docs").GetAttribute("href"));
        Assert.Equal("https://stef-k.github.io/Wayfarer/user/location-providers.html",
            Assert.Single(html.QuerySelectorAll("main a"),
                link => link.TextContent == "Read the credential and usage guide").GetAttribute("href"));
        var form = html.QuerySelector("form[action*='MigrateLegacyMapbox']")!;
        var token = form.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        Assert.False(string.IsNullOrEmpty(token));
        AddResponseCookies(client, page);
        foreach (var rejectedToken in new string?[] { null, "invalid-antiforgery" })
        {
            using var rejected = await client.PostAsync(Root + "/MigrateLegacyMapbox/other", Form(rejectedToken));
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Equal(before, await SnapshotAsync(options));
        }
        using var accepted = await client.PostAsync(Root + "/MigrateLegacyMapbox/other", Form(token));
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        Assert.Equal(Root, accepted.Headers.Location!.OriginalString);
        AssertPrivate(accepted.ToString() + await accepted.Content.ReadAsStringAsync(), db);
        AddResponseCookies(client, accepted);
        using var redirected = await client.GetAsync(accepted.Headers.Location);
        var text = await redirected.Content.ReadAsStringAsync();
        Assert.Contains("Legacy Mapbox credential safely prepared", text);
        Assert.Contains("Existing consent, verification and explicit provider selection", text);
        AssertPrivate(text, db);
        await using var verify = Context(options);
        var profile = await verify.PersonalLocationProviderProfiles.SingleAsync();
        Assert.Equal("owner", profile.UserId);
        Assert.Equal(LegacySecret, credentials.Read(profile).Credential);
        Assert.False(profile.HasCurrentPermanentGeocodingConsent());
        Assert.Equal(PersonalProviderVerification.Unverified, profile.GeocodingVerification);
        Assert.Empty(await verify.PersonalLocationProviderSelections.ToListAsync());
        Assert.Equal(OtherSecret, (await verify.ApiTokens.IgnoreQueryFilters().SingleAsync()).Token);
        AssertPrivate(string.Join("\n", logs.Entries.Select(entry => entry.Message + entry.Exception
            + string.Join(" ", entry.Fields.Select(field => field.Value?.ToString())))), db);
    }

    /// <summary>Normal blocked/no-op outcomes are successful credential-free command evaluations with PRG.</summary>
    [Theory]
    [InlineData("empty", "No legacy Mapbox credential currently requires conversion.")]
    [InlineData("conflict", "Conflicting stored recovery copies were retained.")]
    [InlineData("unavailable", "Legacy recovery copies were retained.")]
    [InlineData("revoked", "Migration did not reactivate it")]
    public async Task Command_ReportsBoundedRecoveryOutcomes(string state, string expected)
    {
        var options = Options();
        await using var db = Context(options);
        var credentials = CredentialTestFactory.Create(new EphemeralDataProtectionProvider());
        await SeedAsync(db, credentials, state);
        var controller = new LocationProviderSettingsController(db, credentials,
            new LegacyMapboxMigrationService(db, credentials), null!);
        ConfigureControllerWithUser(controller, "owner");
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
            controller.HttpContext, Mock.Of<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider>());
        var result = Assert.IsType<RedirectToActionResult>(await controller.MigrateLegacyMapbox(default));
        Assert.Equal("Index", result.ActionName);
        var status = Assert.IsType<string>(controller.TempData["ProviderStatus"]);
        Assert.Contains(expected, status);
        AssertPrivate(status, db);
        await using var verify = Context(options);
        Assert.Equal(state == "empty" ? 1 : 2, await verify.ApiTokens.IgnoreQueryFilters().CountAsync());
    }

    /// <summary>Cancellation and unexpected persistence errors escape rather than becoming successful TempData results.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Command_PropagatesFailureWithoutSuccessStatus(bool cancel)
    {
        var saves = new RejectUnexpectedSave();
        var options = Options(saves);
        await using var db = Context(options);
        var credentials = CredentialTestFactory.Create(new EphemeralDataProtectionProvider());
        await SeedAsync(db, credentials, "pending");
        var before = await SnapshotAsync(options);
        saves.Reject = true;
        var controller = new LocationProviderSettingsController(db, credentials,
            new LegacyMapboxMigrationService(db, credentials), null!);
        ConfigureControllerWithUser(controller, "owner");
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
            controller.HttpContext, Mock.Of<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider>());
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.MigrateLegacyMapbox(new CancellationToken(true)));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.MigrateLegacyMapbox(default));
        Assert.Empty(controller.TempData);
        Assert.Equal(before, await SnapshotAsync(options));
    }

    /// <summary>Both endpoints retain the established anonymous challenge and non-User denial.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Manager")]
    public async Task Settings_RejectsUnauthorizedRoles(string? role)
    {
        var options = Options();
        await using var db = Context(options);
        var credentials = CredentialTestFactory.Create(new EphemeralDataProtectionProvider());
        await SeedAsync(db, credentials, "pending");
        var before = await SnapshotAsync(options);
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        if (role != null) Cookie(client, app.Services, role);
        using var get = await client.GetAsync(Root);
        using var post = await client.PostAsync(Root + "/MigrateLegacyMapbox", null);
        foreach (var response in new[] { get, post })
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains(role == null ? "Login" : "AccessDenied", response.Headers.Location!.OriginalString);
        }
        Assert.Equal(before, await SnapshotAsync(options));
    }

    /// <summary>Missing identity challenges before querying or migrating, including an otherwise authenticated principal.</summary>
    [Fact]
    public async Task MissingIdentity_ChallengesBothActions()
    {
        var controller = new LocationProviderSettingsController(null!, null!, null!, null!);
        ConfigureControllerWithContext(controller, new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "User")], "test"))
        });
        Assert.IsType<ChallengeResult>(await controller.Index(default));
        Assert.IsType<ChallengeResult>(await controller.MigrateLegacyMapbox(default));
    }

    /// <summary>Seeds normal and blocked states, including independently eligible conflict authority.</summary>
    private static async Task SeedAsync(ApplicationDbContext db, PersonalProviderCredentialService credentials, string state)
    {
        var owner = TestDataFixtures.CreateUser(id: "owner");
        var other = TestDataFixtures.CreateUser(id: "other");
        db.AddRange(owner, other, new ApplicationSettings());
        db.ApiTokens.Add(new ApiToken { User = other, UserId = other.Id, Name = "Mapbox", Token = OtherSecret });
        if (state is not ("migrated" or "cleanup" or "empty"))
            db.ApiTokens.Add(new ApiToken { User = owner, UserId = owner.Id, Name = " MaPbOx ", Token = LegacySecret });
        if (state is not ("pending" or "empty"))
        {
            var profile = PersonalLocationProviderProfile.Create(owner.Id, PersonalLocationProvider.Mapbox);
            credentials.Replace(profile, "protected-settings-privacy-sentinel");
            profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
            profile.GrantPermanentGeocodingConsent(DateTimeOffset.UtcNow);
            credentials.RecordVerification(profile, PersonalProviderCapability.Geocoding, PersonalProviderVerification.Verified);
            profile.LegacyMigrationState = state switch
            {
                "conflict" => LegacyMapboxMigrationState.Conflict,
                "revoked" => LegacyMapboxMigrationState.Revoked,
                "unavailable" => LegacyMapboxMigrationState.ProtectedCredentialUnavailable,
                _ => LegacyMapboxMigrationState.Migrated
            };
            if (state == "unavailable") profile.StableProtectedCredential = "unreadable-ciphertext-sentinel";
            if (state == "revoked") credentials.Revoke(profile);
            if (state == "cleanup") profile.ClearPermanentGeocodingConsent();
            db.Add(profile);
            if (state is "conflict" or "cleanup")
            {
                var selection = PersonalLocationProviderSelection.Create(owner.Id);
                selection.Select(PersonalProviderCapability.Geocoding, PersonalLocationProvider.Mapbox);
                db.Add(selection);
            }
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>Uses independent contexts over one persisted test store, never just tracker state as evidence.</summary>
    private static DbContextOptions<ApplicationDbContext> Options(params IInterceptor[] interceptors) =>
        new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptors).Options;

    /// <summary>Opens an independent context against the shared test database options.</summary>
    private static ApplicationDbContext Context(DbContextOptions<ApplicationDbContext> options) =>
        new(options, new ServiceCollection().BuildServiceProvider());

    /// <summary>Includes every field of provider profiles, selections, and legacy rows from a fresh context.</summary>
    private static async Task<string> SnapshotAsync(DbContextOptions<ApplicationDbContext> options)
    {
        await using var db = Context(options);
        return JsonSerializer.Serialize(new
        {
            Profiles = await db.PersonalLocationProviderProfiles.AsNoTracking().OrderBy(p => p.UserId).ToListAsync(),
            Selections = await db.PersonalLocationProviderSelections.AsNoTracking().OrderBy(p => p.UserId).ToListAsync(),
            Tokens = await db.ApiTokens.IgnoreQueryFilters().AsNoTracking().OrderBy(p => p.Id)
                .Select(p => new { p.Id, p.UserId, p.Name, p.Token, p.TokenHash }).ToListAsync()
        });
    }

    /// <summary>Issues a genuine framework Identity cookie, retaining production authorization.</summary>
    private static void Cookie(HttpClient client, IServiceProvider services, string role = "User")
    {
        var scheme = IdentityConstants.ApplicationScheme;
        var options = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Name, "owner"),
            new Claim(ClaimTypes.Role, role)], scheme));
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties
            { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10) }, scheme);
        client.DefaultRequestHeaders.Add("Cookie", options.Cookie.Name + "=" + options.TicketDataFormat.Protect(ticket));
    }

    /// <summary>Retains auth, antiforgery, and TempData cookies across the real PRG flow.</summary>
    private static void AddResponseCookies(HttpClient client, HttpResponseMessage response)
    {
        var cookies = client.DefaultRequestHeaders.GetValues("Cookie").Single().Split("; ").ToList();
        if (response.Headers.TryGetValues("Set-Cookie", out var added))
            foreach (var header in added)
            {
                var cookie = header.Split(';')[0];
                var name = cookie.Split('=')[0] + "=";
                cookies.RemoveAll(value => value.StartsWith(name, StringComparison.Ordinal));
                cookies.Add(cookie);
            }
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies));
    }

    /// <summary>Malicious submitted ownership and state inputs have no corresponding action parameters.</summary>
    private static FormUrlEncodedContent Form(string? token)
    {
        var fields = new Dictionary<string, string>
        {
            ["userId"] = "other", ["profileId"] = Guid.NewGuid().ToString(), ["providerKey"] = "geoapify",
            ["credential"] = OtherSecret, ["migrationState"] = "Migrated", ["selection"] = "mapbox"
        };
        if (token != null) fields["__RequestVerificationToken"] = token;
        return new(fields);
    }

    /// <summary>Checks plaintext, unreadable sentinels and all actual persisted ciphertext against observable output.</summary>
    private static void AssertPrivate(string text, ApplicationDbContext db)
    {
        foreach (var secret in new[] { LegacySecret, OtherSecret, "protected-settings-privacy-sentinel", "unreadable-ciphertext-sentinel" })
            Assert.DoesNotContain(secret, text);
        foreach (var ciphertext in db.PersonalLocationProviderProfiles.AsNoTracking()
                     .Select(p => p.StableProtectedCredential).Where(value => value != null))
            Assert.DoesNotContain(ciphertext!, text);
    }

    /// <summary>Fails if navigation attempts even a no-op SaveChanges call.</summary>
    private sealed class RejectUnexpectedSave : SaveChangesInterceptor
    {
        internal bool Reject { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            Reject ? throw new InvalidOperationException("Settings navigation attempted persistence.") : ValueTask.FromResult(result);
    }
}
