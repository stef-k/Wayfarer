using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Point = NetTopologySuite.Geometries.Point;
using Wayfarer.Util;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Real routed production mutations with framework cookie authentication and genuine antiforgery.</summary>
[Collection("API token singleton admission")]
public sealed class CookieAntiforgeryRouteTests : TestBase
{
    /// <summary>Cookie authority wins every header shape; only resolved bearer authority is token-free.</summary>
    [Theory]
    [InlineData("active", null, false, 400)]
    [InlineData("active", null, true, 200)]
    [InlineData("active", "Bearer", false, 400)]
    [InlineData("active", "Bearer invalid", false, 400)]
    [InlineData("active", "Bearer invalid", true, 200)]
    [InlineData("active", "Bearer revoked", false, 400)]
    [InlineData("active", "Bearer phone", false, 400)]
    [InlineData("active", "Bearer phone", true, 200)]
    [InlineData("active", "Legacy loose phone", false, 400)]
    [InlineData(null, "Bearer phone", false, 200)]
    [InlineData(null, "Bearer invalid", false, 401)]
    [InlineData(null, null, false, 401)]
    [InlineData("inactive", "Bearer phone", false, 400)]
    [InlineData("inactive", "Bearer phone", true, 401)]
    [InlineData("missing", "Bearer phone", true, 401)]
    [InlineData("missing", "Bearer phone", false, 400)]
    public async Task LocationPut_PreservesAuthority(string? cookieUser, string? bearer, bool token, int status)
    {
        var db = Seed();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        if (cookieUser != null) Cookie(client, app.Services, cookieUser);
        if (token) client.DefaultRequestHeaders.Add("RequestVerificationToken", await Token(client));
        if (bearer != null) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", bearer);
        var target = cookieUser == null ? 2 : 1;
        using var response = await client.PutAsJsonAsync($"/api/Location/{target}", new { notes = "changed" });
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(status == 200 ? "changed" : "original", db.Locations.Find(target)!.Notes);
        Assert.Equal("original", db.Locations.Find(target == 1 ? 2 : 1)!.Notes);
        if (status == 401) Assert.Equal("Invalid or missing API token.", await response.Content.ReadAsStringAsync());
    }

    /// <summary>A bearer owner's antiforgery pair cannot authorize the selected cookie owner's request.</summary>
    [Fact]
    public async Task LocationPut_RejectsAnotherUsersRequestToken()
    {
        var db = Seed();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        Cookie(client, app.Services, "bearer");
        var token = await Token(client);
        var antiforgeryCookie = client.DefaultRequestHeaders.GetValues("Cookie").Single().Split("; ", 2)[1];
        Cookie(client, app.Services, "active");
        var authCookie = client.DefaultRequestHeaders.GetValues("Cookie").Single();
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", authCookie + "; " + antiforgeryCookie);
        client.DefaultRequestHeaders.Add("RequestVerificationToken", token);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "phone");
        using var response = await client.PutAsJsonAsync("/api/Location/1", new { notes = "changed" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("original", db.Locations.Find(1)!.Notes);
    }

    /// <summary>The conditional filter stays inside the existing overload result boundary.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocationPut_PreservesLookupOverload(bool global)
    {
        var db = Seed();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        var permits = new List<IDisposable>();
        try
        {
            for (var i = 0; i < (global ? 32 : 16); i++)
                permits.Add(ApiWorkAdmission.TokenLookups.TryAcquire(global ? $"ip-{i}" : "192.0.2.20", out _)!);
            using var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", "phone");
            using var response = await client.PutAsJsonAsync("/api/Location/2", new { notes = "changed" });
            Assert.Equal(global ? 503 : 429, (int)response.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter?.Delta);
            Assert.Equal("original", db.Locations.Find(2)!.Notes);
        }
        finally { foreach (var permit in permits) permit.Dispose(); }
    }

    /// <summary>JSON deletion fails before persistence and succeeds with the same authentic header.</summary>
    [Fact]
    public async Task BulkDelete_RequiresTokenBeforeDeletion()
    {
        var db = Seed();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        Cookie(client, app.Services, "active");
        using var rejected = await client.PostAsJsonAsync("/api/Location/bulk-delete", new { locationIds = new[] { 1 } });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.NotNull(db.Locations.Find(1));
        client.DefaultRequestHeaders.Add("RequestVerificationToken", await Token(client));
        using var accepted = await client.PostAsJsonAsync("/api/Location/bulk-delete", new { locationIds = new[] { 1 } });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Null(db.Locations.Find(1));
        Assert.NotNull(db.Locations.Find(2));
    }

    /// <summary>A real authenticated Razor page exposes a token accepted by a JSON mutation.</summary>
    [Fact]
    public async Task RenderedPageToken_AuthenticatesBrowserMutation()
    {
        var db = Seed();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        Cookie(client, app.Services, "active");
        var authCookie = client.DefaultRequestHeaders.GetValues("Cookie").Single();
        using var page = await client.GetAsync("/User/Visit");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync());
        var token = html.QuerySelector("#browser-antiforgery input[name=__RequestVerificationToken]")!.GetAttribute("value");
        Assert.False(string.IsNullOrEmpty(token));
        var antiforgeryCookie = page.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(".AspNetCore.Antiforgery.")).Split(';')[0];
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", authCookie + "; " + antiforgeryCookie);
        client.DefaultRequestHeaders.Add("RequestVerificationToken", token);
        using var response = await client.PutAsJsonAsync("/api/Location/1", new { notes = "page-token" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("page-token", db.Locations.Find(1)!.Notes);
    }

    /// <summary>Bodyless group leave requires a header without changing its route or response.</summary>
    [Fact]
    public async Task GroupLeave_RequiresTokenBeforeService()
    {
        var db = Seed();
        var groupId = Guid.NewGuid();
        var groups = new Mock<IGroupService>(MockBehavior.Strict);
        groups.Setup(service => service.LeaveGroupAsync(groupId, "active", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(),
            services => services.AddSingleton(groups.Object));
        using var client = app.GetTestClient();
        Cookie(client, app.Services, "active");
        using var rejected = await client.PostAsync($"/api/groups/{groupId}/leave", null);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        groups.VerifyNoOtherCalls();
        client.DefaultRequestHeaders.Add("RequestVerificationToken", await Token(client));
        using var accepted = await client.PostAsync($"/api/groups/{groupId}/leave", null);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Contains("Left group", await accepted.Content.ReadAsStringAsync());
        groups.Verify(service => service.LeaveGroupAsync(groupId, "active", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Multipart header validation precedes import work and retains mode/result semantics.</summary>
    [Fact]
    public async Task TripImport_RequiresTokenBeforeImportService()
    {
        var db = Seed();
        var tripId = Guid.NewGuid();
        var importer = new Mock<ITripImportService>(MockBehavior.Strict);
        importer.Setup(service => service.ImportWayfarerKmlAsync(It.IsAny<Stream>(), "active",
                TripImportMode.CreateNew, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TripImportResult(tripId, []));
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(),
            services => services.AddSingleton(importer.Object));
        using var client = app.GetTestClient();
        Cookie(client, app.Services, "active");
        using var rejected = await client.PostAsync("/User/Trip/Import", Multipart());
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        importer.VerifyNoOtherCalls();
        client.DefaultRequestHeaders.Add("RequestVerificationToken", await Token(client));
        using var accepted = await client.PostAsync("/User/Trip/Import", Multipart());
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Contains($"/User/Trip/Edit/{tripId}", await accepted.Content.ReadAsStringAsync());
        importer.Verify(service => service.ImportWayfarerKmlAsync(It.IsAny<Stream>(), "active",
            TripImportMode.CreateNew, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Existing location-import validation still rejects before filesystem staging.</summary>
    [Fact]
    public async Task LocationImport_RejectsBeforeStaging()
    {
        var db = Seed();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        Cookie(client, app.Services, "active");
        using var rejected = await client.PostAsync("/User/LocationImport/Upload", Multipart());
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Empty(db.LocationImports);
        Assert.False(Directory.Exists(app.Services.GetRequiredService<LocationImportStagedFiles>().DirectoryPath));
    }

    /// <summary>The genuine bearer DELETE uses the shared resolver; a cookie alone is not its authority.</summary>
    [Fact]
    public async Task BearerDelete_RemainsTokenFreeAndRejectsCookieAlone()
    {
        var db = Seed();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        Cookie(client, app.Services, "active");
        using var cookieOnly = await client.DeleteAsync("/api/Location/1");
        Assert.Equal(HttpStatusCode.Unauthorized, cookieOnly.StatusCode);
        Assert.NotNull(db.Locations.Find(1));
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Authorization = new("Bearer", "phone");
        using var bearer = await client.DeleteAsync("/api/Location/2");
        Assert.Equal(HttpStatusCode.OK, bearer.StatusCode);
        Assert.Null(db.Locations.Find(2));
    }

    /// <summary>Public timeline queries remain callable without cookie or antiforgery credentials.</summary>
    [Fact]
    public async Task PublicQuery_RemainsTokenFree()
    {
        var db = Seed();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/Public/Users/GetPublicTimeline", new { username = "absent" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("User not found or timeline is not public.", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Seed separate cookie and bearer owners and a revoked credential with no matching row.</summary>
    private ApplicationDbContext Seed()
    {
        var db = CreateDbContext();
        db.Users.AddRange(TestDataFixtures.CreateUser(id: "active"), TestDataFixtures.CreateUser(id: "bearer"),
            TestDataFixtures.CreateUser(id: "inactive", isActive: false));
        db.ApiTokens.Add(new ApiToken { User = db.Users.Local.Single(user => user.Id == "bearer"), UserId = "bearer", Name = "phone", TokenHash = ApiTokenService.HashToken("phone") });
        db.ApplicationSettings.Add(new ApplicationSettings());
        foreach (var (id, owner) in new[] { (1, "active"), (2, "bearer") })
            db.Locations.Add(new Location { Id = id, UserId = owner, Coordinates = new Point(20, 10) { SRID = 4326 },
                Notes = "original", Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow, TimeZoneId = "UTC" });
        db.SaveChanges();
        return db;
    }

    /// <summary>Issue an actual protected Identity cookie; authentication still runs through the framework handler.</summary>
    private static void Cookie(HttpClient client, IServiceProvider services, string userId)
    {
        var scheme = IdentityConstants.ApplicationScheme;
        var options = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Name, userId),
            new Claim(ClaimTypes.Role, "User")], scheme));
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties
            { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10) }, scheme);
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", options.Cookie.Name + "=" + options.TicketDataFormat.Protect(ticket));
    }

    /// <summary>Keep the authenticated cookie when adding the framework's antiforgery cookie.</summary>
    private static async Task<string> Token(HttpClient client)
    {
        var existing = client.DefaultRequestHeaders.TryGetValues("Cookie", out var cookies) ? cookies.Single() : null;
        var token = await IdentityRouteHost.AntiforgeryAsync(client);
        var antiforgery = client.DefaultRequestHeaders.GetValues("Cookie").Single();
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", existing == null ? antiforgery : existing + "; " + antiforgery);
        return token;
    }

    /// <summary>A small multipart body exercises binding and mode independently of the import parser.</summary>
    private static MultipartFormDataContent Multipart() => new()
    {
        { new StringContent("<kml/>"), "file", "trip.kml" },
        { new StringContent("CreateNew"), "mode" }
    };
}
