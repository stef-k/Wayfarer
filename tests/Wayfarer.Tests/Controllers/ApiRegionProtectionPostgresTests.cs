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
        await AssertErrorAsync(rejected, HttpStatusCode.BadRequest,
            "Cannot change the Unassigned Places region name or display order.");
        await AssertUnchangedAsync(original);

        using var deletion = await client.DeleteAsync($"/api/trips/regions/{original.Id}");
        await AssertErrorAsync(deletion, HttpStatusCode.BadRequest, "Cannot delete the Unassigned Places region.");
        client.DefaultRequestHeaders.Add("X-Wayfarer-Dependency-Confirmation", "not-a-bypass");
        using var confirmedDeletion = await client.DeleteAsync($"/api/trips/regions/{original.Id}");
        await AssertErrorAsync(confirmedDeletion, HttpStatusCode.BadRequest, "Cannot delete the Unassigned Places region.");
        await AssertUnchangedAsync(original);
        warmup.Verify(item => item.ScheduleWarmupAsync(It.IsAny<Guid>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>Accepted reserved metadata commits once; null/empty payloads and a rejected mixed update preserve it.</summary>
    [PostgresFact]
    public async Task ReservedMetadataAndNoOpPayloadsRetainPartialContract()
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

        using var accepted = await client.PutAsJsonAsync($"/api/trips/regions/{original.Id}", new
        {
            name = "  Unassigned Places  ", displayOrder = 0,
            notes = "<p onclick='bad()'>Updated</p><script>bad()</script>", coverImageUrl = "updated-cover",
            centerLatitude = 0, centerLongitude = 0
        });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var document = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("message", out _));
        var responseRegion = document.RootElement.GetProperty("region");
        Assert.Equal(original.Id, responseRegion.GetProperty("id").GetGuid());
        Assert.Equal("<p>Updated</p>", responseRegion.GetProperty("notes").GetString());
        Assert.Equal(new[] { 0d, 0d }, responseRegion.GetProperty("center").EnumerateArray().Select(item => item.GetDouble()));
        await using var verify = fixture.CreateContext();
        var committed = await verify.Regions.Include(item => item.Trip).SingleAsync(item => item.Id == original.Id);
        Assert.Equal("Unassigned Places", committed.Name);
        Assert.Equal(0, committed.DisplayOrder);
        Assert.Equal("<p>Updated</p>", committed.Notes);
        Assert.Equal("updated-cover", committed.CoverImageUrl);
        Assert.Equal((0d, 0d, 4326), (committed.Center!.X, committed.Center.Y, committed.Center.SRID));
        Assert.True(committed.Trip.UpdatedAt > original.Trip.UpdatedAt);

        foreach (var payload in new[] { "{}", """{"name":null,"displayOrder":null,"notes":null,"coverImageUrl":null,"centerLatitude":null,"centerLongitude":null}""" })
        {
            using var noOp = await client.PutAsync($"/api/trips/regions/{original.Id}",
                new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, noOp.StatusCode);
            using var noOpDocument = JsonDocument.Parse(await noOp.Content.ReadAsStringAsync());
            Assert.Equal("No changes applied.", noOpDocument.RootElement.GetProperty("message").GetString());
        }

        using var rejected = await client.PutAsJsonAsync($"/api/trips/regions/{original.Id}", new
        {
            name = "Renamed", displayOrder = 7, notes = "Rejected", coverImageUrl = "rejected-cover",
            centerLatitude = 1, centerLongitude = 1
        });
        await AssertErrorAsync(rejected, HttpStatusCode.BadRequest,
            "Cannot change the Unassigned Places region name or display order.");
        await AssertUnchangedAsync(committed);
        warmup.Verify(item => item.ScheduleWarmupAsync(original.TripId, false), Times.Once);
        warmup.VerifyNoOtherCalls();
    }

    /// <summary>An ordinary zero-order Region still accepts notes-only updates and trimmed names/negative orders.</summary>
    [PostgresFact]
    public async Task OrdinaryRegionPartialUpdatesRemainCompatible()
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

        using var notes = await client.PutAsJsonAsync($"/api/trips/regions/{original.Id}", new { notes = "<p>Partial</p>" });
        Assert.Equal(HttpStatusCode.OK, notes.StatusCode);
        await using var verify = fixture.CreateContext();
        var updated = await verify.Regions.Include(item => item.Trip).SingleAsync(item => item.Id == original.Id);
        Assert.Equal("<p>Partial</p>", updated.Notes);
        Assert.Equal(original.Name, updated.Name);
        Assert.Equal(original.DisplayOrder, updated.DisplayOrder);
        Assert.Equal(original.CoverImageUrl, updated.CoverImageUrl);
        Assert.Equal((23d, 37d, 4326), (updated.Center!.X, updated.Center.Y, updated.Center.SRID));

        using var reservedName = await client.PutAsJsonAsync($"/api/trips/regions/{original.Id}", new { name = "  uNassigned PLaces  " });
        await AssertErrorAsync(reservedName, HttpStatusCode.BadRequest, "Region name is reserved.");
        await AssertUnchangedAsync(updated);

        using var identity = await client.PutAsJsonAsync($"/api/trips/regions/{original.Id}", new { name = "  Renamed  ", displayOrder = -2 });
        Assert.Equal(HttpStatusCode.OK, identity.StatusCode);
        await using var fresh = fixture.CreateContext();
        var renamed = await fresh.Regions.SingleAsync(item => item.Id == original.Id);
        Assert.Equal("Renamed", renamed.Name);
        Assert.Equal(-2, renamed.DisplayOrder);
        warmup.Verify(item => item.ScheduleWarmupAsync(original.TripId, false), Times.Exactly(2));
        warmup.VerifyNoOtherCalls();
    }

    /// <summary>Missing bearer and a real second account retain existing 401 responses before reserved-field policy.</summary>
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
        await AssertErrorAsync(update, HttpStatusCode.Unauthorized, "Not your region.");
        using var deletion = await client.DeleteAsync($"/api/trips/regions/{original.Id}");
        await AssertErrorAsync(deletion, HttpStatusCode.Unauthorized, "Not your region.");
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
