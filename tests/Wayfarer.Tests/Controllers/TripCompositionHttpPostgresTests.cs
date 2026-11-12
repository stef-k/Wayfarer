using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Real routed lifecycle authority, antiforgery, mobile bearer transport and public/private boundaries.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class TripCompositionHttpPostgresTests(PostgresImportTestFixture fixture) : TestBase
{
    [PostgresFact]
    public async Task CookieAndBearerResumeSameCWithoutPublicWorkspaceOrPlanningFreshness()
    {
        var owner = await fixture.CreateUserAsync(); var foreign = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var a = new Trip { Id = Guid.NewGuid(), UserId = owner.Id, Name = "Private A" };
        var b = new Trip { Id = Guid.NewGuid(), UserId = foreign.Id, Name = "Reusable B", IsPublic = true, ContributionPolicy = "CC-BY-4.0",
            Planning = new(1, [new(Guid.NewGuid(), new("unknown"), "foreign-secret")]) };
        fixture.RegisterTrip(a.Id); fixture.RegisterTrip(b.Id);
        db.AddRange(a, b, new ApiToken { Name = "composition", Token = owner.Id, UserId = owner.Id, User = await db.Users.SingleAsync(u => u.Id == owner.Id) });
        await db.SaveChangesAsync();
        var logs = new TestLogProvider();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddLogging(l => l.AddProvider(logs));
            services.AddScoped(_ => fixture.CreateContext());
            services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(fixture));
            services.AddSingleton(Mock.Of<ICacheWarmupScheduler>());
            services.AddSingleton(Mock.Of<IIconColorProvider>());
            services.AddSingleton(Mock.Of<ITripMapThumbnailGenerator>());
        });
        using var browser = app.GetTestClient(); using var mobile = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(browser, app.Services, owner.Id);
        var request = new { sourceA = a.Id, sourceB = b.Id, name = "Public result" };
        using var csrf = await browser.PostAsJsonAsync("/api/trip-compositions", request);
        Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode); Assert.True(csrf.Headers.CacheControl!.NoStore);
        // A cookie cannot authorize the mobile surface.
        using var noBearer = await browser.PostAsJsonAsync("/api/trips/compositions", request);
        Assert.True(noBearer.StatusCode == HttpStatusCode.Unauthorized, await noBearer.Content.ReadAsStringAsync() + string.Join("\n", logs.Entries.Select(e => e.Exception?.ToString()))); Assert.True(noBearer.Headers.CacheControl!.NoStore);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(browser);
        using var start = await browser.PostAsJsonAsync("/api/trip-compositions", request);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        using var created = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        var c = created.RootElement.GetProperty("tripId").GetGuid(); fixture.RegisterTrip(c);
        var workspace = created.RootElement.GetProperty("compositionId").GetGuid();
        mobile.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Id);
        using var foreignVersion = await mobile.PostAsJsonAsync("/api/trips/versions", new { sourceA = b.Id });
        Assert.Equal(HttpStatusCode.NotFound, foreignVersion.StatusCode);
        using var resumed = await mobile.GetAsync($"/api/trips/compositions/{workspace}");
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode); Assert.True(resumed.Headers.CacheControl!.NoStore);
        using var comparison = JsonDocument.Parse(await resumed.Content.ReadAsStringAsync());
        Assert.Equal(c, comparison.RootElement.GetProperty("tripId").GetGuid());
        Assert.False(comparison.RootElement.GetProperty("sourceB").TryGetProperty("planning", out _));
        Assert.DoesNotContain("foreign-secret", comparison.RootElement.ToString());
        using var list = await mobile.GetAsync($"/api/trips/compositions?sourceA={a.Id}&sourceB={b.Id}");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains(workspace.ToString(), await list.Content.ReadAsStringAsync());
        // Publication uses the normal conditional writer and remains independent of Finish.
        using var published = await browser.PatchAsJsonAsync($"/api/trips/{c}/editor/metadata/conditional", new
        {
            expected = new { isPublic = false, contributionPolicy = (string?)null },
            changes = new { isPublic = true, contributionPolicy = "CC0-1.0" }
        });
        Assert.True(published.StatusCode == HttpStatusCode.OK, await published.Content.ReadAsStringAsync());
        var publicBefore = await browser.GetByteArrayAsync($"/api/trips/{c}");
        var publicText = System.Text.Encoding.UTF8.GetString(publicBefore);
        Assert.DoesNotContain("planning", publicText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("composition", publicText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Private A", publicText);
        Assert.DoesNotContain(a.Id.ToString(), publicText);
        Assert.DoesNotContain("origin", publicText, StringComparison.OrdinalIgnoreCase);
        using var planning = await mobile.PatchAsJsonAsync($"/api/trips/{c}/planning/trip/{c}", new
        {
            expected = new { parentId = c, planning = (object?)null }, operation = "set",
            value = new { format = 1, entries = new[] { new { id = Guid.NewGuid(), when = new { form = "unknown" }, label = "owner-secret" } } }
        });
        Assert.Equal(HttpStatusCode.OK, planning.StatusCode);
        Assert.Equal(publicBefore, await browser.GetByteArrayAsync($"/api/trips/{c}"));
        using var finish = await mobile.PostAsync($"/api/trips/compositions/{workspace}/finish", null);
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
        Assert.Equal(publicBefore, await browser.GetByteArrayAsync($"/api/trips/{c}"));
        using var closed = await browser.GetAsync($"/api/trip-compositions/{workspace}");
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode); Assert.True(closed.Headers.CacheControl!.NoStore);
    }

    /// <summary>Production full reads receive fresh contexts, as in the established routed planning fixtures.</summary>
    private sealed class Factory(PostgresImportTestFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateContext();
    }
}
