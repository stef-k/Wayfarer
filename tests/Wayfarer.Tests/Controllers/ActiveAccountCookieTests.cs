using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Middleware;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Request-time account admission through production Identity registrations and real protected routes.</summary>
[Collection("API token singleton admission")]
public sealed class ActiveAccountCookieTests : TestBase
{
    /// <summary>Each cookie boundary admits an active account and deletes inactive or missing-account sessions.</summary>
    [Theory]
    [InlineData("/User/Visit")]
    [InlineData("/Identity/Account/Manage")]
    [InlineData("/api/Activity")]
    public async Task CookieRoutes_CheckCurrentAccount(string path)
    {
        var db = Seed();
        await using var app = await StartAsync(db);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Cookie", IdentityRouteHost.Cookie(app.Services, "owner"));
        using var active = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        db.Users.Find("owner")!.IsActive = false;
        await db.SaveChangesAsync();
        using var inactive = await client.GetAsync(path);
        AssertRejected(inactive, path.StartsWith("/api"));
        db.Users.Remove(db.Users.Find("owner")!);
        await db.SaveChangesAsync();
        using var missing = await client.GetAsync(path);
        AssertRejected(missing, path.StartsWith("/api"));
    }

    /// <summary>Both original sharing writes reject before persistence, including fetch navigation and error normalization.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharingWrites_DeleteCookieAndDoNotPublish(bool editor)
    {
        var db = Seed();
        var trip = db.Trips.Single();
        await using var app = await StartAsync(db);
        using var client = app.GetTestClient();
        var cookie = IdentityRouteHost.Cookie(app.Services, "owner");
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        var token = await IdentityRouteHost.AntiforgeryAsync(client);
        var antiforgeryCookie = client.DefaultRequestHeaders.GetValues("Cookie").Single();
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", cookie + "; " + antiforgeryCookie);
        client.DefaultRequestHeaders.Add("RequestVerificationToken", token);
        client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "cors");
        var path = editor ? $"/api/trips/{trip.Id}/editor/share-progress" : $"/User/Trip/ToggleShareProgress?id={trip.Id}&enabled=true";
        using var active = editor ? await client.PatchAsJsonAsync(path, new { enabled = true }) : await client.PostAsync(path, null);
        Assert.True(active.StatusCode == HttpStatusCode.OK, await active.Content.ReadAsStringAsync());
        Assert.True(trip.ShareProgressEnabled);
        trip.ShareProgressEnabled = false;
        db.Users.Find("owner")!.IsActive = false;
        await db.SaveChangesAsync();
        using var denied = editor ? await client.PatchAsJsonAsync(path, new { enabled = true }) : await client.PostAsync(path, null);
        AssertRejected(denied, true);
        Assert.Contains("session has ended", await denied.Content.ReadAsStringAsync());
        Assert.False(trip.ShareProgressEnabled);
    }

    /// <summary>An unused old cookie stays invalid after reactivation; newly issued Identity credentials work.</summary>
    [Fact]
    public async Task Reactivation_RequiresFreshCookieButRetainsApiToken()
    {
        var db = Seed();
        await using var app = await StartAsync(db);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Cookie", IdentityRouteHost.Cookie(app.Services, "owner"));
        using var scope = app.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = db.Users.Find("owner")!;
        user.IsActive = false;
        Assert.True((await manager.UpdateSecurityStampAsync(user)).Succeeded);
        user.IsActive = true;
        Assert.True((await manager.UpdateSecurityStampAsync(user)).Succeeded);
        using var stale = await client.GetAsync("/User/Visit");
        AssertRejected(stale, false);
        using var login = await client.GetAsync(stale.Headers.Location);
        Assert.Contains("session has ended", await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", IdentityRouteHost.Cookie(app.Services, "owner"));
        using var fresh = await client.GetAsync("/User/Visit");
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Single(db.ApiTokens);
    }

    /// <summary>Legacy, mixed and mobile adapters retain token recovery and token-only route authority.</summary>
    [Theory]
    [InlineData("/api/Trips")]
    [InlineData("/api/Activity")]
    [InlineData("/api/mobile/me")]
    public async Task BearerRoutes_DenyInactiveAndRecover(string path)
    {
        var db = Seed();
        await using var app = await StartAsync(db);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "phone");
        using var active = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        db.Users.Find("owner")!.IsActive = false;
        await db.SaveChangesAsync();
        using var denied = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        db.Users.Find("owner")!.IsActive = true;
        await db.SaveChangesAsync();
        using var recovered = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal("phone", Assert.Single(db.ApiTokens).Token);
        db.Users.Remove(db.Users.Find("owner")!);
        await db.SaveChangesAsync();
        using var missing = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
    }

    /// <summary>A rejected cookie cannot select another bearer owner on manual mixed routes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedRoute_DoesNotFallBackAfterCookieRejection(bool stampChanged)
    {
        var db = Seed();
        var bearer = TestDataFixtures.CreateUser(id: "bearer");
        db.Users.Add(bearer);
        db.ApiTokens.Add(new ApiToken { User = bearer, UserId = bearer.Id, Name = "other", Token = "other" });
        await db.SaveChangesAsync();
        await using var app = await StartAsync(db);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Cookie", IdentityRouteHost.Cookie(app.Services, "owner"));
        client.DefaultRequestHeaders.Authorization = new("Bearer", "other");
        if (stampChanged) db.Users.Find("owner")!.SecurityStamp = Guid.NewGuid().ToString();
        else db.Users.Find("owner")!.IsActive = false;
        await db.SaveChangesAsync();
        using var denied = await client.GetAsync("/api/Activity");
        AssertRejected(denied, true);
        // A token-only route continues to select its own authority even with a rejected cookie.
        using var tokenOnly = await client.GetAsync("/api/mobile/me");
        Assert.Equal(HttpStatusCode.OK, tokenOnly.StatusCode);
        Assert.Contains("bearer", await tokenOnly.Content.ReadAsStringAsync());
    }

    /// <summary>A custom route without Authorize loses private-owner access while public export remains available.</summary>
    [Fact]
    public async Task ManualPrivateExportAndAnonymousPublicExport_PreserveVisibility()
    {
        var db = Seed();
        var trip = db.Trips.Single();
        trip.IsPublic = false;
        await db.SaveChangesAsync();
        await using var app = await StartAsync(db);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Cookie", IdentityRouteHost.Cookie(app.Services, "owner"));
        var path = $"/Trip/ExportWayfarerKml/{trip.Id}";
        using var active = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        db.Users.Find("owner")!.IsActive = false;
        await db.SaveChangesAsync();
        using var inactive = await client.GetAsync(path);
        AssertRejected(inactive, false);
        trip.IsPublic = true;
        trip.ShareProgressEnabled = true;
        await db.SaveChangesAsync();
        client.DefaultRequestHeaders.Remove("Cookie");
        using var anonymous = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
        Assert.True(trip.ShareProgressEnabled);
    }

    /// <summary>Install production error normalization around the same real Identity registrations.</summary>
    private Task<WebApplication> StartAsync(ApplicationDbContext db) => IdentityRouteHost.StartAsync(db,
        CreateTestDirectory(), services =>
        {
            // These routes do not capture thumbnails or run background jobs in the registration host.
            services.AddSingleton(Moq.Mock.Of<Quartz.IScheduler>());
            services.AddSingleton(Moq.Mock.Of<Wayfarer.Services.ITripMapThumbnailGenerator>());
            // Provider capability dependencies are constructed but never queried by these sharing mutations.
            services.AddSingleton(Moq.Mock.Of<IDbContextFactory<ApplicationDbContext>>());
        },
        app => app.UseMiddleware<ApiErrorResponseMiddleware>());

    /// <summary>Small owned data set; requests retain the same token and cookie bytes across state changes.</summary>
    private ApplicationDbContext Seed()
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "owner");
        db.Users.Add(user);
        db.ApplicationSettings.Add(new ApplicationSettings());
        db.Trips.Add(new Trip { User = user, UserId = user.Id, Name = "Public trip", IsPublic = true });
        db.ApiTokens.Add(new ApiToken { User = user, UserId = user.Id, Name = "phone", Token = "phone" });
        db.SaveChanges();
        return db;
    }

    /// <summary>Deletion headers and a denial/explicit navigation response must survive the complete pipeline.</summary>
    private static void AssertRejected(HttpResponseMessage response, bool fetch)
    {
        Assert.Equal(fetch ? HttpStatusCode.Unauthorized : HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith(".AspNetCore.Identity.Application=;") && value.Contains("expires="));
        if (!fetch) Assert.Contains("sessionEnded", response.Headers.Location!.ToString());
    }
}
