using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Proves legacy Place creation atomicity in an independently owned, migrated PostgreSQL database.</summary>
public sealed class ApiTripPlaceCreationPostgresTests(PostgresMigrationTestFixture fixture)
    : TestBase, IClassFixture<PostgresMigrationTestFixture>
{
    /// <summary>Routed first creation, sequential reuse and explicit destination each save once with compatible DTOs.</summary>
    [PostgresFact]
    public async Task RoutedCreationSavesNewReusedAndExplicitRegionsOnce()
    {
        var original = await SeedAsync();
        await using var db = fixture.CreateContext();
        var saves = 0;
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ =>
            {
                var context = fixture.CreateContext();
                context.SavingChanges += (_, _) => saves++;
                return context;
            });
            services.AddSingleton(Mock.Of<ICacheWarmupScheduler>());
        });
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", original.UserId);

        var first = await PostPlaceAsync(client, original.TripId, new
        {
            name = "First", latitude = 37, longitude = 23, notes = "<p>First notes</p>"
        });
        Assert.Equal(1, saves);
        var second = await PostPlaceAsync(client, original.TripId, new { name = "Second", regionId = (Guid?)null });
        Assert.Equal(2, saves);
        var explicitPlace = await PostPlaceAsync(client, original.TripId, new
        {
            name = "Explicit", regionId = original.Id, displayOrder = 7,
            latitude = 0, longitude = 0, iconName = "museum", markerColor = "bg-red"
        });
        Assert.Equal(3, saves);
        Assert.Equal(new[] { 23d, 37d }, first.Location);
        Assert.Equal("<p>First notes</p>", first.Notes);
        Assert.Null(second.Location);
        Assert.Null(second.Notes);
        Assert.Null(second.Address);
        Assert.Null(second.ResolvedFeatureName);
        Assert.Null(second.ResolvedFeatureType);
        Assert.Equal(("First", 1, "marker", "bg-blue"),
            (first.Name, first.DisplayOrder, first.IconName, first.MarkerColor));
        Assert.Equal(("Second", 2, "marker", "bg-blue"),
            (second.Name, second.DisplayOrder, second.IconName, second.MarkerColor));
        Assert.Equal(("Explicit", 7, "museum", "bg-red"),
            (explicitPlace.Name, explicitPlace.DisplayOrder, explicitPlace.IconName, explicitPlace.MarkerColor));
        Assert.Equal(new[] { 0d, 0d }, explicitPlace.Location);

        await using var verify = fixture.CreateContext();
        var fallback = await verify.Regions.Include(item => item.Places)
            .SingleAsync(item => item.TripId == original.TripId && item.Name == "Unassigned Places");
        Assert.NotEqual(Guid.Empty, fallback.Id);
        Assert.Equal(original.UserId, fallback.UserId);
        Assert.Equal(0, fallback.DisplayOrder);
        Assert.Equal(new[] { first.Id, second.Id }, fallback.Places.OrderBy(item => item.DisplayOrder).Select(item => item.Id));
        Assert.All(fallback.Places, item => Assert.Equal((original.UserId, fallback.Id), (item.UserId, item.RegionId)));
        var storedFirst = fallback.Places.Single(item => item.Id == first.Id);
        Assert.Equal((23d, 37d, 4326), (storedFirst.Location!.X, storedFirst.Location.Y, storedFirst.Location.SRID));
        Assert.Null(fallback.Places.Single(item => item.Id == second.Id).Location);
        var storedExplicit = await verify.Places.SingleAsync(item => item.Id == explicitPlace.Id);
        Assert.Equal((original.UserId, original.Id, 7),
            (storedExplicit.UserId, storedExplicit.RegionId, storedExplicit.DisplayOrder));
        Assert.Equal(2, await verify.Regions.CountAsync(item => item.TripId == original.TripId));
        Assert.True((await verify.Trips.SingleAsync(item => item.Id == original.TripId)).UpdatedAt > original.Trip.UpdatedAt);
    }

    /// <summary>A database failure after the parent exists rolls back new rows and the timestamp, preserving prior rows.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaceInsertionFailureRollsBackWithNewOrExistingFallback(bool existingFallback)
    {
        var original = await SeedAsync(existingFallback);
        await using var db = fixture.CreateContext();
        // This trigger is confined to the fixture's disposable database and one rejected Place name.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION reject_atomic_place() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM "Regions" WHERE "Id" = NEW."RegionId") THEN
                    RAISE EXCEPTION 'Parent Region was not inserted' USING ERRCODE = '23503';
                END IF;
                RAISE EXCEPTION 'Controlled Place insertion failure after parent exists' USING ERRCODE = '23514';
            END;
            $$;
            CREATE OR REPLACE TRIGGER reject_atomic_place BEFORE INSERT ON "Places"
                FOR EACH ROW WHEN (NEW."Name" = 'Reject atomic Place') EXECUTE FUNCTION reject_atomic_place();
            """);

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => Controller(db, original.UserId)
            .CreatePlace(original.TripId, new PlaceCreateRequestDto { Name = "Reject atomic Place" }));

        var postgres = Assert.IsType<PostgresException>(failure.InnerException);
        Assert.Equal("23514", postgres.SqlState);
        Assert.Equal("Controlled Place insertion failure after parent exists", postgres.MessageText);
        await AssertUnchangedAsync(original);
    }

    /// <summary>Cancellation at the final save propagates and leaves no committed fallback, Place or timestamp change.</summary>
    [PostgresFact]
    public async Task CancellationAtFinalSaveCommitsNothing()
    {
        var original = await SeedAsync();
        await using var db = fixture.CreateContext();
        using var cancellation = new CancellationTokenSource();
        var saves = 0;
        db.SavingChanges += (_, _) =>
        {
            saves++;
            if (db.ChangeTracker.Entries<Place>().Any(item => item.State == EntityState.Added))
                cancellation.Cancel();
        };
        var controller = Controller(db, original.UserId, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.CreatePlace(original.TripId,
            new PlaceCreateRequestDto { Name = "Cancelled" }));

        Assert.Equal(1, saves);
        Assert.True(cancellation.IsCancellationRequested);
        await AssertUnchangedAsync(original);
    }

    /// <summary>HTTP authority, explicit destination precedence, name/binding errors and unpaired input remain write-free.</summary>
    [PostgresFact]
    public async Task RejectedCreationPreservesAuthorityValidationAndBinding()
    {
        var original = await SeedAsync();
        await using var db = fixture.CreateContext();
        var saves = 0;
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ =>
            {
                var context = fixture.CreateContext();
                context.SavingChanges += (_, _) => saves++;
                return context;
            });
            services.AddSingleton(Mock.Of<ICacheWarmupScheduler>());
        });
        using var client = app.GetTestClient();
        var url = $"/api/trips/{original.TripId}/places";
        using var unauthenticated = await client.PostAsJsonAsync(url, new { name = "Rejected" });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal("Missing or invalid API token.", await unauthenticated.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", original.UserId);
        var cases = new (string Payload, string? Error)[]
        {
            ("""{"name":"Rejected","latitude":10}""", "Both latitude and longitude must be provided together."),
            ("""{"name":"Rejected","regionId":"00000000-0000-0000-0000-000000000000","latitude":"NaN","longitude":0}""", "Invalid regionId."),
            ("""{"name":" "}""", null),
            ("""{}""", null),
            ("""{"name":"Rejected","regionId":"invalid"}""", null),
            ("""{"name":"Rejected","latitude":NaN,"longitude":0}""", null)
        };
        foreach (var (payload, error) in cases)
        {
            using var response = await client.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            if (error != null) Assert.Equal(error, body);
            else
            {
                using var problem = JsonDocument.Parse(body);
                Assert.Equal(JsonValueKind.Object, problem.RootElement.GetProperty("errors").ValueKind);
            }
        }
        Assert.Equal(0, saves);
        await AssertUnchangedAsync(original);
    }

    /// <summary>Seeds only disposable-database identity, bearer and original Trip/Region/Place rows.</summary>
    private async Task<Region> SeedAsync(bool fallback = false)
    {
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Atomic Place creation" };
        var region = new Region
        {
            Id = Guid.NewGuid(), Trip = trip, UserId = user.Id,
            Name = fallback ? "Unassigned Places" : "Original", DisplayOrder = fallback ? 17 : 3,
            Notes = "<p>Original Region</p>"
        };
        var place = new Place
        {
            Id = Guid.NewGuid(), Region = region, UserId = user.Id, Name = "Original",
            DisplayOrder = 5, Notes = "<p>Original Place</p>"
        };
        await using var db = fixture.CreateContext();
        db.ApiTokens.Add(new ApiToken
        {
            UserId = user.Id, User = null!, Name = "atomic-place", TokenHash = ApiTokenService.HashToken(user.Id)
        });
        db.Places.Add(place);
        await db.SaveChangesAsync();
        // Compare the committed timestamp at PostgreSQL's microsecond precision.
        await db.Entry(trip).ReloadAsync();
        return region;
    }

    /// <summary>Reads independently so tracker state after failure cannot masquerade as committed data.</summary>
    private async Task AssertUnchangedAsync(Region original)
    {
        await using var verify = fixture.CreateContext();
        var region = await verify.Regions.Include(item => item.Trip)
            .SingleAsync(item => item.TripId == original.TripId);
        Assert.Equal((original.Id, original.Name, original.UserId, original.DisplayOrder, original.Notes),
            (region.Id, region.Name, region.UserId, region.DisplayOrder, region.Notes));
        Assert.Equal(original.Trip.UpdatedAt, region.Trip.UpdatedAt);
        var place = await verify.Places.SingleAsync(item => item.Region.TripId == original.TripId);
        var expectedPlace = Assert.Single(original.Places);
        Assert.Equal((expectedPlace.Id, expectedPlace.RegionId, expectedPlace.UserId, expectedPlace.Name, expectedPlace.DisplayOrder, expectedPlace.Notes),
            (place.Id, place.RegionId, place.UserId, place.Name, place.DisplayOrder, place.Notes));
    }

    /// <summary>Uses the production action with a bearer and the request's cancellation token.</summary>
    private static TripsController Controller(ApplicationDbContext db, string token, CancellationToken cancellationToken = default)
    {
        var context = CreateHttpContext(token);
        context.RequestAborted = cancellationToken;
        return new TripsController(db, NullLogger<BaseApiController>.Instance, Mock.Of<ITripTagService>(),
            Mock.Of<IApplicationSettingsService>(), Mock.Of<ICacheWarmupScheduler>())
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    /// <summary>Verifies the routed 200 envelope and exact legacy DTO field set before reading its representation.</summary>
    private static async Task<ApiTripPlaceDto> PostPlaceAsync(HttpClient client, Guid tripId, object payload)
    {
        using var response = await client.PostAsJsonAsync($"/api/trips/{tripId}/places", payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(new[] { "place", "success" }, root.EnumerateObject().Select(item => item.Name).Order());
        var place = root.GetProperty("place");
        Assert.Equal(new[]
        {
            "id", "name", "notes", "displayOrder", "iconName", "markerColor", "address",
            "resolvedFeatureName", "resolvedFeatureType", "location"
        }.Order(), place.EnumerateObject().Select(item => item.Name).Order());
        var dto = place.Deserialize<ApiTripPlaceDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.NotEqual(Guid.Empty, dto.Id);
        return dto;
    }
}
