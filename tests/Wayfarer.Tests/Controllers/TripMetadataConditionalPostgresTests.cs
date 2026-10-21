using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Exercises the paired metadata routes with real Identity, antiforgery and PostgreSQL commits.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class TripMetadataConditionalPostgresTests(PostgresImportTestFixture fixture) : TestBase
{
    /// <summary>Both clients retain their original read while the independent field commits first in either order.</summary>
    [PostgresTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task DisjointOriginalDrafts_BothSurvive(bool webFirst, bool webNames)
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext());
            services.AddSingleton(Mock.Of<ICacheWarmupScheduler>());
            services.AddSingleton(Mock.Of<ITripMapThumbnailGenerator>());
            services.AddSingleton(Mock.Of<ITripEditorGeocodeSearchService>());
        });
        using var web = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(web, app.Services, trip.UserId);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(web);
        using var bearer = app.GetTestClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", trip.UserId);
        var webDraft = webNames
            ? new { expected = new Dictionary<string, object?> { ["name"] = trip.Name }, changes = new Dictionary<string, object?> { ["name"] = "Web name" } }
            : new { expected = new Dictionary<string, object?> { ["notesHtml"] = trip.Notes }, changes = new Dictionary<string, object?> { ["notesHtml"] = "Web notes" } };
        var mobileDraft = webNames
            ? new { expected = new Dictionary<string, object?> { ["notes"] = trip.Notes }, changes = new Dictionary<string, object?> { ["notes"] = "Mobile notes" } }
            : new { expected = new Dictionary<string, object?> { ["name"] = trip.Name }, changes = new Dictionary<string, object?> { ["name"] = "Mobile name" } };

        async Task SaveWeb()
        {
            using var response = await web.PatchAsJsonAsync($"/api/trips/{trip.Id}/editor/metadata/conditional", webDraft);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }
        async Task SaveBearer()
        {
            using var response = await bearer.PatchAsJsonAsync($"/api/trips/{trip.Id}/conditional", mobileDraft);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("bd2-1", result.GetProperty("contract").GetString());
            Assert.True(result.GetProperty("applied").GetBoolean());
        }

        if (webFirst) { await SaveWeb(); await SaveBearer(); }
        else { await SaveBearer(); await SaveWeb(); }
        await using var verification = fixture.CreateContext();
        var stored = await verification.Trips.SingleAsync(t => t.Id == trip.Id);
        Assert.Equal(webNames ? "Web name" : "Mobile name", stored.Name);
        Assert.Equal(webNames ? "Mobile notes" : "Web notes", stored.Notes);
        Assert.Equal(trip.CoverImageUrl, stored.CoverImageUrl);
        Assert.True(stored.IsPublic && stored.ShareProgressEnabled);
    }

    /// <summary>Seeds a separately owned Trip and an actual accepted incoming bearer, with scoped cleanup.</summary>
    private async Task<Trip> SeedAsync(string? notes = "Original notes")
    {
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Original name", Notes = notes,
            IsPublic = true, ShareProgressEnabled = true, CoverImageUrl = "https://example.test/before.jpg",
            CenterLat = 1, CenterLon = 2, Zoom = 3 };
        fixture.RegisterTrip(trip.Id);
        await using var db = fixture.CreateContext();
        db.Trips.Add(trip);
        db.ApiTokens.Add(new ApiToken { UserId = user.Id, User = null!, Name = "853 Slice 3", TokenHash = ApiTokenService.HashToken(user.Id) });
        await db.SaveChangesAsync();
        return trip;
    }
}
