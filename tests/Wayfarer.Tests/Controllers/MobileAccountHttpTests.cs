using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wayfarer.Middleware;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Exercises account proof through production MVC, Identity cookies and the real token resolver.</summary>
[Collection("API token singleton admission")]
public sealed class MobileAccountHttpTests : TestBase
{
    private const string Route = "/api/mobile/me";
    private const string FirstToken = "wf_identity_first_secret";
    private const string SecondToken = "wf_identity_second_secret";
    private const string OtherToken = "wf_identity_other_secret";

    /// <summary>Legacy/hash credentials prove only their stable owner, with no writes or extra profile/secret data.</summary>
    [Fact]
    public async Task ActiveTokens_ReturnOnlyStableOwnerWithoutWrites()
    {
        using var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        var other = TestDataFixtures.CreateUser();
        db.Users.AddRange(owner, other);
        db.ApiTokens.AddRange(Token(owner, FirstToken, false), Token(owner, SecondToken), Token(other, OtherToken));
        await db.SaveChangesAsync();
        var auditCount = await db.AuditLogs.CountAsync();
        var saves = 0;
        db.SavingChanges += (_, _) => saves++;
        using var logs = new TestLogProvider();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(),
            services => services.AddLogging(logging => logging.AddProvider(logs)),
            configureApp: host => host.UseMiddleware<ApiErrorResponseMiddleware>());
        using var client = app.GetTestClient();
        // A genuine different-owner Identity cookie cannot select or override bearer ownership.
        UserConnectionTokenHttpTests.Cookie(client, app.Services, other.Id);
        foreach (var (secret, expected) in new[] { (FirstToken, owner.Id), (SecondToken, owner.Id), (OtherToken, other.Id) })
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", secret);
            using var response = await client.GetAsync(Route + "?userId=" + other.Id);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var body = await response.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            var property = Assert.Single(json.RootElement.EnumerateObject());
            Assert.Equal("userId", property.Name);
            Assert.Equal(expected, property.Value.GetString());
            Assert.DoesNotContain(owner.Email!, body);
            Assert.DoesNotContain(owner.UserName!, body);
            foreach (var token in new[] { FirstToken, SecondToken, OtherToken })
            {
                Assert.DoesNotContain(token, body + response.Headers + response.Content.Headers);
                Assert.DoesNotContain(ApiTokenService.HashToken(token), body + response.Headers + response.Content.Headers);
            }
        }
        Assert.Equal(0, saves);
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Equal(auditCount, await db.AuditLogs.CountAsync());
        Assert.Empty(db.Locations);
        Assert.Empty(db.PlaceVisitEvents);
        var diagnostics = string.Join(Environment.NewLine, logs.Entries.Select(entry =>
            $"{entry.Message} {entry.Exception} {string.Join(" ", entry.Fields.Values)}"));
        foreach (var secret in new[] { FirstToken, SecondToken, OtherToken, owner.Email!, owner.UserName! })
            Assert.DoesNotContain(secret, diagnostics);
        foreach (var token in new[] { FirstToken, SecondToken, OtherToken })
            Assert.DoesNotContain(ApiTokenService.HashToken(token), diagnostics);
    }

    /// <summary>Anonymous, cookie-only, malformed and invalid credentials never disclose cookie or bearer identity.</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("Bearer", true)]
    [InlineData(FirstToken, true)]
    [InlineData("Basic " + FirstToken, true)]
    [InlineData("Bearer ignored " + FirstToken, true)]
    [InlineData("Bearer " + FirstToken + ", Bearer " + FirstToken, true)]
    [InlineData("Bearer unknown-secret", true)]
    public async Task InvalidAuthorization_Returns401(string? authorization, bool cookie)
    {
        using var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        db.Users.Add(owner);
        db.ApiTokens.Add(Token(owner, FirstToken));
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(),
            configureApp: host => host.UseMiddleware<ApiErrorResponseMiddleware>());
        using var client = app.GetTestClient();
        if (cookie) UserConnectionTokenHttpTests.Cookie(client, app.Services, owner.Id);
        if (authorization != null) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        using var response = await client.GetAsync(Route);
        await AssertUnauthorizedAsync(response, owner.Id);
    }

    /// <summary>Every request rereads revocation/replacement, account activity and account deletion.</summary>
    [Fact]
    public async Task ChangedCredentialsAndAccounts_AreRevalidated()
    {
        using var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser();
        db.Users.Add(owner);
        var token = Token(owner, FirstToken);
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(),
            configureApp: host => host.UseMiddleware<ApiErrorResponseMiddleware>());
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", FirstToken);
        using (var response = await client.GetAsync(Route)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        token.TokenHash = ApiTokenService.HashToken(SecondToken);
        await db.SaveChangesAsync();
        using (var response = await client.GetAsync(Route)) await AssertUnauthorizedAsync(response, owner.Id);
        client.DefaultRequestHeaders.Authorization = new("bearer", SecondToken);
        using (var response = await client.GetAsync(Route)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        owner.IsActive = false;
        await db.SaveChangesAsync();
        using (var response = await client.GetAsync(Route)) await AssertUnauthorizedAsync(response, owner.Id);
        db.Users.Remove(owner);
        await db.SaveChangesAsync();
        using (var response = await client.GetAsync(Route)) await AssertUnauthorizedAsync(response, owner.Id);
    }

    /// <summary>Per-IP and global admission keep their existing status, retry hint and secret-free envelope.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overload_RemainsDistinctFromInvalidCredentials(bool global)
    {
        using var db = CreateDbContext();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(),
            configureApp: host => host.UseMiddleware<ApiErrorResponseMiddleware>());
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", FirstToken);
        var permits = new List<IDisposable>();
        try
        {
            for (var i = 0; i < (global ? 32 : 16); i++)
                permits.Add(ApiWorkAdmission.TokenLookups.TryAcquire(global ? $"ip-{i}" : "192.0.2.20", out _)!);
            using var response = await client.GetAsync(Route);
            Assert.Equal(global ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter?.Delta);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("{\"message\":\"API token lookup capacity exceeded.\"}", await response.Content.ReadAsStringAsync());
        }
        finally { foreach (var permit in permits) permit.Dispose(); }
    }

    /// <summary>Store failure and server cancellation never become invalid-token responses or account proof.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledLookup_DoesNotProveInvalidCredentials(bool cancel)
    {
        using var db = CreateDbContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var logs = new TestLogProvider();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(),
            services => services.AddLogging(logging => logging.AddProvider(logs)), host =>
            {
                if (cancel) host.Use((context, next) => { context.RequestAborted = cancellation.Token; return next(context); });
                host.UseMiddleware<ApiErrorResponseMiddleware>();
            });
        if (!cancel) await db.DisposeAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", FirstToken);
        using var response = await client.GetAsync(Route);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("userId", body);
        Assert.DoesNotContain(FirstToken, body);
        var failure = Assert.Single(logs.Entries, entry => entry.Category == typeof(ApiErrorResponseMiddleware).FullName);
        Assert.Equal(cancel ? "OperationCanceledException" : "ObjectDisposedException", failure.Fields["ExceptionType"]);
        Assert.Null(failure.Exception);
        Assert.Equal(0, ApiWorkAdmission.TokenLookups.IdentityCount);
    }

    /// <summary>Seeds actual stored verifiers, including an existing plaintext administrative token.</summary>
    private static ApiToken Token(ApplicationUser owner, string secret, bool hashed = true) => new()
    {
        User = owner, UserId = owner.Id, Name = secret,
        Token = hashed ? null : secret, TokenHash = hashed ? ApiTokenService.HashToken(secret) : null
    };

    /// <summary>Checks the stable mobile 401 JSON without challenge redirects, secrets or owner disclosure.</summary>
    private static async Task AssertUnauthorizedAsync(HttpResponseMessage response, string ownerId)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Null(response.Headers.RetryAfter);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("{\"message\":\"Invalid or missing API token.\"}", body);
        Assert.DoesNotContain(ownerId, body);
        Assert.DoesNotContain(FirstToken, body);
    }
}
