using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Real MVC routing, Identity cookies, antiforgery and final browser response policy.</summary>
public sealed class UserConnectionTokenHttpTests : TestBase
{
    /// <summary>Only a current active User cookie with genuine antiforgery can issue a credential.</summary>
    [Theory]
    [InlineData(null, true, true, 302)]
    [InlineData("Admin", true, true, 302)]
    [InlineData("Manager", true, true, 302)]
    [InlineData("User", false, true, 403)]
    [InlineData("deleted", true, true, 403)]
    [InlineData("User", true, false, 400)]
    [InlineData("User", true, true, 201)]
    public async Task Create_EnforcesCookieAccountAndAntiforgery(string? role, bool active, bool antiforgery, int expected)
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(isActive: active);
        if (role != "deleted") db.Users.Add(user);
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://wayfarer.test");
        if (role != null) Cookie(client, app.Services, user.Id, role == "deleted" ? "User" : role);
        if (antiforgery) await AddAntiforgeryAsync(client);
        using var response = await client.PostAsJsonAsync("/User/ApiToken/Create", new { userId = "other", name = "arbitrary" });
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(expected == 201 ? 1 : 0, db.ApiTokens.Count());
        if (expected == 201)
        {
            var row = Assert.Single(db.ApiTokens);
            Assert.Equal(user.Id, row.UserId);
            Assert.Equal(ApiTokenService.ConnectionTokenName, row.Name);
        }
    }

    /// <summary>The browser route never interprets a bearer header as cookie Identity authority.</summary>
    [Fact]
    public async Task BearerAloneCannotIssue()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { User = user, UserId = user.Id, Name = "admin-issued", TokenHash = ApiTokenService.HashToken("bearer") });
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://wayfarer.test");
        client.DefaultRequestHeaders.Authorization = new("Bearer", "bearer");
        await AddAntiforgeryAsync(client);
        using var response = await client.PostAsync("/User/ApiToken/Create", null);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Single(db.ApiTokens);
    }

    /// <summary>GET is safe, owner-scoped, and requires a User cookie even when it only returns metadata.</summary>
    [Theory]
    [InlineData(null, true, 302)]
    [InlineData("Admin", true, 302)]
    [InlineData("User", true, 200)]
    [InlineData("User", false, 200)]
    public async Task Get_IsSecretFreeAndDoesNotMutate(string? role, bool exists, int expected)
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        if (exists) db.ApiTokens.Add(new ApiToken { User = user, UserId = user.Id, Name = ApiTokenService.ConnectionTokenName,
            TokenHash = ApiTokenService.HashToken("existing"), CreatedAt = DateTime.UtcNow });
        db.ApiTokens.Add(new ApiToken { User = user, UserId = user.Id, Name = "unrelated", Token = "legacy-extra" });
        await db.SaveChangesAsync();
        var before = db.ApiTokens.Select(row => new { row.Id, row.TokenHash, row.Token, row.CreatedAt }).ToArray();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://wayfarer.test");
        if (role != null) Cookie(client, app.Services, user.Id, role);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        using var response = await client.GetAsync("/User/ApiToken");
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        if (expected == 200)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (exists) Assert.Equal(["issuedAt", "tokenId"], json.RootElement.EnumerateObject().Select(field => field.Name).Order().ToArray());
            else Assert.Equal(JsonValueKind.Null, json.RootElement.ValueKind);
        }
        Assert.Equal(before, db.ApiTokens.Select(row => new { row.Id, row.TokenHash, row.Token, row.CreatedAt }).ToArray());
    }

    /// <summary>Plaintext occurs only in successful JSON; no redirect, cookie/session/TempData retention or verifier fields.</summary>
    [Fact]
    public async Task SuccessfulCreate_IsDirectJsonWithNoRetention()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://wayfarer.test");
        Cookie(client, app.Services, user.Id);
        await AddAntiforgeryAsync(client);
        using var response = await client.PostAsync("/User/ApiToken/Create", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Headers.Location);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["issuedAt", "token", "tokenId"], json.RootElement.EnumerateObject().Select(field => field.Name).Order().ToArray());
        var plaintext = json.RootElement.GetProperty("token").GetString()!;
        Assert.Matches("^wf_[A-Za-z0-9_-]{43}$", plaintext);
        var row = Assert.Single(db.ApiTokens);
        Assert.Null(row.Token);
        Assert.Equal(ApiTokenService.HashToken(plaintext), row.TokenHash);
        using var conflict = await client.PostAsync("/User/ApiToken/Create", null);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.DoesNotContain(plaintext, await conflict.Content.ReadAsStringAsync());
    }

    /// <summary>Only forwarding from the configured proxy can establish effective HTTPS for issuance.</summary>
    [Theory]
    [InlineData("192.0.2.10", 201)]
    [InlineData("192.0.2.20", 400)]
    public async Task EffectiveHttpsRequiresTrustedPeer(string peer, int expected)
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("http://backend.test");
        Cookie(client, app.Services, user.Id);
        client.DefaultRequestHeaders.Add("X-Test-Peer", peer);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "public.test");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.10");
        await AddAntiforgeryAsync(client);
        using var response = await client.PostAsync("/User/ApiToken/Create", null);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(expected == 201 ? 1 : 0, db.ApiTokens.Count());
    }

    /// <summary>Uses framework cookie protection, without an invented bearer-to-browser authentication seam.</summary>
    internal static void Cookie(HttpClient client, IServiceProvider services, string userId, string role = "User")
    {
        var scheme = IdentityConstants.ApplicationScheme;
        var options = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Name, userId),
            new Claim(ClaimTypes.Role, role)], scheme));
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties
            { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10) }, scheme);
        client.DefaultRequestHeaders.Add("Cookie", options.Cookie.Name + "=" + options.TicketDataFormat.Protect(ticket));
    }

    /// <summary>Preserves the Identity cookie while adding authentic framework antiforgery cookie/header values.</summary>
    internal static async Task AddAntiforgeryAsync(HttpClient client)
    {
        var identity = client.DefaultRequestHeaders.TryGetValues("Cookie", out var cookies) ? cookies.Single() : null;
        var token = await IdentityRouteHost.AntiforgeryAsync(client);
        var antiforgery = client.DefaultRequestHeaders.GetValues("Cookie").Single();
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", identity == null ? antiforgery : identity + "; " + antiforgery);
        client.DefaultRequestHeaders.Add("RequestVerificationToken", token);
    }
}
