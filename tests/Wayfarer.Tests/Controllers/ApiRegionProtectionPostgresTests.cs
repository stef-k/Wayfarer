using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Exercises real legacy bearer/MVC Region mutations and verifies fresh PostgreSQL/PostGIS state.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class ApiRegionProtectionPostgresTests(PostgresImportTestFixture fixture) : TestBase
{
    /// <summary>Separate rename/reorder attempts preserve all fields and cannot remove subsequent deletion protection.</summary>
    [PostgresTheory]
    [InlineData("Renamed", null)]
    [InlineData(null, 7)]
    public async Task ReservedIdentityRejectionPreservesPersistenceAndDeletionGuard(string? name, int? order)
    {
        var original = await SeedRegionAsync("Unassigned Places");
        await using var db = fixture.CreateContext();
        var warmup = new Mock<ICacheWarmupScheduler>();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext());
            services.AddSingleton(warmup.Object);
        });
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", original.UserId);

        using var rejected = await client.PutAsJsonAsync($"/api/trips/regions/{original.Id}",
            new { name, displayOrder = order });
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal("mutation-originals-required", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        await AssertUnchangedAsync(original);

        using var deletion = await client.DeleteAsync($"/api/trips/regions/{original.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, deletion.StatusCode);
        Assert.Equal("forbidden", (await deletion.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        client.DefaultRequestHeaders.Add("X-Wayfarer-Dependency-Confirmation", "not-a-bypass");
        using var confirmedDeletion = await client.DeleteAsync($"/api/trips/regions/{original.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, confirmedDeletion.StatusCode);
        Assert.Equal("forbidden", (await confirmedDeletion.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        await AssertUnchangedAsync(original);
        warmup.Verify(item => item.ScheduleWarmupAsync(It.IsAny<Guid>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>Complete, null and empty original-free canonical updates reject with no persistence or warm-up.</summary>
    [PostgresTheory]
    [InlineData("{}")]
    [InlineData("{\"notes\":null,\"name\":null}")]
    [InlineData("{\"name\":\"Unassigned Places\",\"notes\":\"\",\"coverImageUrl\":\"\",\"centerLatitude\":0,\"centerLongitude\":0}")]
    public async Task ReservedMetadataAndNoOpPayloadsRejectWithoutMutation(string payload)
    {
        var original = await SeedRegionAsync("Unassigned Places");
        await using var db = fixture.CreateContext();
        var warmup = new Mock<ICacheWarmupScheduler>();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext());
            services.AddSingleton(warmup.Object);
        });
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", original.UserId);
        using var response = await client.PutAsync($"/api/trips/regions/{original.Id}",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("mutation-originals-required", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        await AssertUnchangedAsync(original);
        Assert.Empty(warmup.Invocations);
    }

    /// <summary>A protected Notes save succeeds; unconditional identity/order attempts cannot bypass it.</summary>
    [PostgresFact]
    public async Task OrdinaryRegionPartialUpdatesCannotBypassProtectedOrdering()
    {
        var original = await SeedRegionAsync("Ordinary");
        await using var db = fixture.CreateContext();
        var warmup = new Mock<ICacheWarmupScheduler>();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext());
            services.AddSingleton(warmup.Object);
        });
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", original.UserId);

        using var notes = await client.PatchAsJsonAsync($"/api/trips/{original.TripId}/conditional/regions/{original.Id}",
            new { expected = new { notes = original.Notes }, changes = new { notes = "<p>Partial</p>" } });
        Assert.Equal(HttpStatusCode.OK, notes.StatusCode);
        await using var verify = fixture.CreateContext();
        var updated = await verify.Regions.Include(item => item.Trip).SingleAsync(item => item.Id == original.Id);
        Assert.Equal("<p>Partial</p>", updated.Notes);
        Assert.Equal(original.Name, updated.Name);
        Assert.Equal(original.DisplayOrder, updated.DisplayOrder);
        Assert.Equal(original.CoverImageUrl, updated.CoverImageUrl);
        Assert.Equal((23d, 37d, 4326), (updated.Center!.X, updated.Center.Y, updated.Center.SRID));

        using var reservedName = await client.PutAsJsonAsync($"/api/trips/regions/{original.Id}", new { name = "  uNassigned PLaces  " });
        Assert.Equal(HttpStatusCode.Conflict, reservedName.StatusCode);
        await AssertUnchangedAsync(updated);

        using var identity = await client.PutAsJsonAsync($"/api/trips/regions/{original.Id}", new { name = "  Renamed  ", displayOrder = -2 });
        Assert.Equal(HttpStatusCode.Conflict, identity.StatusCode);
        await using var fresh = fixture.CreateContext();
        var renamed = await fresh.Regions.SingleAsync(item => item.Id == original.Id);
        Assert.Equal(original.Name, renamed.Name);
        Assert.Equal(original.DisplayOrder, renamed.DisplayOrder);
        warmup.Verify(item => item.ScheduleWarmupAsync(original.TripId, false), Times.Once);
        warmup.VerifyNoOtherCalls();
    }

    /// <summary>Missing bearer rejects; a real second account receives the same concealed missing-scope result.</summary>
    [PostgresFact]
    public async Task ReservedRegionStillRequiresItsBearerOwner()
    {
        var original = await SeedRegionAsync("Unassigned Places");
        var other = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        db.ApiTokens.Add(new ApiToken
        {
            UserId = other.Id, User = null!, Name = "region-protection", TokenHash = ApiTokenService.HashToken(other.Id)
        });
        await db.SaveChangesAsync();
        var warmup = new Mock<ICacheWarmupScheduler>();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext());
            services.AddSingleton(warmup.Object);
        });
        using var client = app.GetTestClient();

        using var unauthenticated = await client.PutAsJsonAsync($"/api/trips/regions/{original.Id}", new { notes = "Rejected" });
        await AssertErrorAsync(unauthenticated, HttpStatusCode.Unauthorized, "Missing or invalid API token.");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.Id);
        using var update = await client.PutAsJsonAsync($"/api/trips/regions/{original.Id}", new { notes = "Rejected", name = "Renamed" });
        await AssertErrorAsync(update, HttpStatusCode.NotFound, "Region not found.");
        using var deletion = await client.DeleteAsync($"/api/trips/regions/{original.Id}");
        Assert.Equal(HttpStatusCode.NotFound, deletion.StatusCode);
        Assert.DoesNotContain("Unassigned Places", await deletion.Content.ReadAsStringAsync());
        await AssertUnchangedAsync(original);
        warmup.Verify(item => item.ScheduleWarmupAsync(It.IsAny<Guid>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>Seeds fixture-owned identity, hashed bearer token and Region rows without touching development data.</summary>
    private async Task<Region> SeedRegionAsync(string name)
    {
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Region protection" };
        fixture.RegisterTrip(trip.Id);
        var region = new Region
        {
            Id = Guid.NewGuid(), Trip = trip, UserId = user.Id, Name = name, DisplayOrder = 0,
            Notes = "<p>Original</p>", CoverImageUrl = "original-cover", Center = new Point(23, 37) { SRID = 4326 }
        };
        await using var db = fixture.CreateContext();
        db.ApiTokens.Add(new ApiToken
        {
            UserId = user.Id, User = null!, Name = "region-protection", TokenHash = ApiTokenService.HashToken(user.Id)
        });
        db.Regions.Add(region);
        await db.SaveChangesAsync();
        // PostgreSQL timestamps have microsecond precision; compare the committed value on later rejections.
        await db.Entry(trip).ReloadAsync();
        return region;
    }

    /// <summary>Checks all mutable fields, ownership and Trip timestamp from an independent database context.</summary>
    private async Task AssertUnchangedAsync(Region expected)
    {
        await using var db = fixture.CreateContext();
        var stored = await db.Regions.Include(item => item.Trip).SingleAsync(item => item.Id == expected.Id);
        Assert.Equal(expected.Name, stored.Name);
        Assert.Equal(expected.DisplayOrder, stored.DisplayOrder);
        Assert.Equal(expected.Notes, stored.Notes);
        Assert.Equal(expected.CoverImageUrl, stored.CoverImageUrl);
        Assert.Equal((expected.Center!.X, expected.Center.Y, expected.Center.SRID),
            (stored.Center!.X, stored.Center.Y, stored.Center.SRID));
        Assert.Equal(expected.UserId, stored.UserId);
        Assert.Equal(expected.TripId, stored.TripId);
        Assert.Equal(expected.Trip.UpdatedAt, stored.Trip.UpdatedAt);
    }

    /// <summary>Verifies the legacy string-valued controller error and its negotiated HTTP status.</summary>
    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string message)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(message, await response.Content.ReadAsStringAsync());
    }
}
