using System.Net;
using System.Net.Http.Headers;
using System.Text;
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

/// <summary>Qualifies routed coordinate binding, rejection persistence and successful Place projections.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class ApiTripCoordinatesPostgresTests(PostgresImportTestFixture fixture) : TestBase
{
    /// <summary>One routed NaN witness per action leaves rows, metadata, membership, orders, routes and timestamps intact.</summary>
    [PostgresTheory]
    [InlineData("create-place")]
    [InlineData("update-place")]
    [InlineData("create-region")]
    [InlineData("update-region")]
    public async Task NamedNaNRejectsWithoutPersistingAnySuppliedChanges(string action)
    {
        var original = await SeedAsync();
        await using var db = fixture.CreateContext();
        var saves = 0;
        var warmup = new Mock<ICacheWarmupScheduler>();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ =>
            {
                var context = fixture.CreateContext();
                context.SavingChanges += (_, _) => saves++;
                return context;
            });
            services.AddSingleton(warmup.Object);
        });
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", original.Region.UserId);
        var (method, url, payload) = action switch
        {
            "create-place" => (HttpMethod.Post, $"/api/trips/{original.Region.TripId}/places",
                """{"name":"Changed","notes":"Changed","latitude":"NaN","longitude":10}"""),
            "update-place" => (HttpMethod.Put, $"/api/trips/places/{original.Place.Id}",
                $$"""{"regionId":"{{original.Destination.Id}}","name":"Changed","notes":"Changed","displayOrder":7,"iconName":"changed","markerColor":"changed","latitude":10,"longitude":"NaN"}"""),
            "create-region" => (HttpMethod.Post, $"/api/trips/{original.Region.TripId}/regions",
                """{"name":"Changed","notes":"Changed","coverImageUrl":"changed","displayOrder":7,"centerLatitude":"NaN","centerLongitude":10}"""),
            "update-region" => (HttpMethod.Put, $"/api/trips/regions/{original.Region.Id}",
                """{"name":"Changed","notes":"Changed","coverImageUrl":"changed","displayOrder":7,"centerLatitude":10,"centerLongitude":"NaN"}"""),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        using var request = new HttpRequestMessage(method, url) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(action.EndsWith("place", StringComparison.Ordinal)
            ? "Latitude or Longitude is out of range." : "Center latitude or longitude is out of range.",
            await response.Content.ReadAsStringAsync());
        await AssertUnchangedAsync(original);
        Assert.Equal(0, saves);
        warmup.Verify(item => item.ScheduleWarmupAsync(It.IsAny<Guid>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>Real JSON null/omission keeps optional creates and partial edits compatible, including Place provenance.</summary>
    [PostgresFact]
    public async Task OptionalCoordinatesRetainCreateAndPartialUpdateBinding()
    {
        var original = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext());
            services.AddSingleton(Mock.Of<ICacheWarmupScheduler>());
        });
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", original.Region.UserId);

        using var regionResponse = await client.PostAsync($"/api/trips/{original.Region.TripId}/regions",
            new StringContent("""{"name":"Optional","centerLatitude":null}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, regionResponse.StatusCode);
        using var regionDocument = JsonDocument.Parse(await regionResponse.Content.ReadAsStringAsync());
        Assert.True(regionDocument.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, regionDocument.RootElement.GetProperty("region").GetProperty("center").ValueKind);
        using var placeResponse = await client.PostAsync($"/api/trips/{original.Region.TripId}/places",
            new StringContent($$"""{"name":"Optional","regionId":"{{original.Region.Id}}"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, placeResponse.StatusCode);
        Assert.Equal("application/json", placeResponse.Content.Headers.ContentType?.MediaType);
        using var placeDocument = JsonDocument.Parse(await placeResponse.Content.ReadAsStringAsync());
        var createdPlace = AssertPlaceShape(placeDocument.RootElement);
        var createdId = createdPlace.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, createdId);
        Assert.Equal("Optional", createdPlace.GetProperty("name").GetString());
        Assert.Equal(3, createdPlace.GetProperty("displayOrder").GetInt32());
        Assert.Equal("marker", createdPlace.GetProperty("iconName").GetString());
        Assert.Equal("bg-blue", createdPlace.GetProperty("markerColor").GetString());
        foreach (var field in new[] { "notes", "address", "resolvedFeatureName", "resolvedFeatureType", "location" })
            Assert.Equal(JsonValueKind.Null, createdPlace.GetProperty(field).ValueKind);

        using var placeUpdate = await client.PutAsync($"/api/trips/places/{original.Place.Id}",
            new StringContent("""{"name":"Partial","latitude":null,"notes":"<p>Partial <strong>rich</strong></p><script>alert('x')</script>"}""",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, placeUpdate.StatusCode);
        Assert.Equal("application/json", placeUpdate.Content.Headers.ContentType?.MediaType);
        using var updateDocument = JsonDocument.Parse(await placeUpdate.Content.ReadAsStringAsync());
        var updatedPlace = AssertPlaceShape(updateDocument.RootElement);
        Assert.Equal(original.Place.Id, updatedPlace.GetProperty("id").GetGuid());
        Assert.Equal("Partial", updatedPlace.GetProperty("name").GetString());
        Assert.Equal("<p>Partial <strong>rich</strong></p>", updatedPlace.GetProperty("notes").GetString());
        Assert.Equal(1, updatedPlace.GetProperty("displayOrder").GetInt32());
        Assert.Equal("original-icon", updatedPlace.GetProperty("iconName").GetString());
        Assert.Equal("original-color", updatedPlace.GetProperty("markerColor").GetString());
        Assert.Equal("Original address", updatedPlace.GetProperty("address").GetString());
        Assert.Equal("Original feature", updatedPlace.GetProperty("resolvedFeatureName").GetString());
        Assert.Equal("museum", updatedPlace.GetProperty("resolvedFeatureType").GetString());
        var coordinates = updatedPlace.GetProperty("location");
        Assert.Equal(JsonValueKind.Array, coordinates.ValueKind);
        Assert.Equal(new[] { 23d, 37d }, coordinates.EnumerateArray().Select(item => item.GetDouble()));

        // Metadata-only updates preserve absent positions and omitted nullable Notes/Address without coercion (#853 Slice 2).
        using var absentUpdate = await client.PutAsync($"/api/trips/places/{createdId}",
            new StringContent("""{"name":"Optional edited"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, absentUpdate.StatusCode);
        Assert.Equal("application/json", absentUpdate.Content.Headers.ContentType?.MediaType);
        using var absentDocument = JsonDocument.Parse(await absentUpdate.Content.ReadAsStringAsync());
        var absentPlace = AssertPlaceShape(absentDocument.RootElement);
        Assert.Equal(createdId, absentPlace.GetProperty("id").GetGuid());
        Assert.Equal("Optional edited", absentPlace.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, absentPlace.GetProperty("notes").ValueKind);
        Assert.Equal(JsonValueKind.Null, absentPlace.GetProperty("address").ValueKind);
        Assert.Equal(3, absentPlace.GetProperty("displayOrder").GetInt32());
        Assert.Equal("marker", absentPlace.GetProperty("iconName").GetString());
        Assert.Equal("bg-blue", absentPlace.GetProperty("markerColor").GetString());
        foreach (var field in new[] { "resolvedFeatureName", "resolvedFeatureType", "location" })
            Assert.Equal(JsonValueKind.Null, absentPlace.GetProperty(field).ValueKind);
        using var regionUpdate = await client.PutAsync($"/api/trips/regions/{original.Region.Id}",
            new StringContent("""{"notes":"Partial","centerLongitude":null}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, regionUpdate.StatusCode);
        await using var verify = fixture.CreateContext();
        var place = await verify.Places.SingleAsync(item => item.Id == original.Place.Id);
        var region = await verify.Regions.SingleAsync(item => item.Id == original.Region.Id);
        Assert.Equal("Partial", place.Name);
        Assert.Equal("<p>Partial <strong>rich</strong></p>", place.Notes);
        Assert.Equal("Partial", region.Notes);
        Assert.Equal((23d, 37d, 4326), (place.Location!.X, place.Location.Y, place.Location.SRID));
        Assert.Equal((23d, 37d, 4326), (region.Center!.X, region.Center.Y, region.Center.SRID));
        Assert.Equal(original.Place.Address, place.Address);
        Assert.Equal(original.Place.ResolvedFeatureName, place.ResolvedFeatureName);
        Assert.Equal(original.Place.ResolvedFeatureType, place.ResolvedFeatureType);
        Assert.Equal(original.Place.AddressEnrichmentProvider, place.AddressEnrichmentProvider);
        Assert.Equal(original.Place.AddressEnrichmentStorageMode, place.AddressEnrichmentStorageMode);
        Assert.Equal(original.Place.AddressEnrichedAt, place.AddressEnrichedAt);
    }

    /// <summary>A metadata-only update suppresses a historical non-finite response position without repairing geography.</summary>
    [PostgresFact]
    public async Task HistoricalNonFinitePlaceUpdateReturnsNullLocationWithoutChangingStoredPoint()
    {
        var original = await SeedAsync();
        await using var db = fixture.CreateContext();
        // Seed outside current coordinate-input validation, without any dependent Segment.
        var historical = new Place
        {
            Id = Guid.NewGuid(), RegionId = original.Region.Id, UserId = original.Region.UserId,
            Name = "Historical", Notes = "<p>Historical</p>", DisplayOrder = 3,
            Address = "Historical address", IconName = "museum", MarkerColor = "bg-red",
            ResolvedFeatureName = "Historical museum", ResolvedFeatureType = "museum",
            Location = new Point(23, double.NaN) { SRID = 4326 }
        };
        db.Places.Add(historical);
        await db.SaveChangesAsync();
        await using (var before = fixture.CreateContext())
        {
            var stored = await before.Places.SingleAsync(item => item.Id == historical.Id);
            Assert.NotNull(stored.Location);
            Assert.Equal(23d, stored.Location.X);
            Assert.True(double.IsNaN(stored.Location.Y));
            Assert.Equal(4326, stored.Location.SRID);
        }

        var saves = 0;
        var warmup = new Mock<ICacheWarmupScheduler>();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ =>
            {
                var context = fixture.CreateContext();
                context.SavingChanges += (_, _) => saves++;
                return context;
            });
            services.AddSingleton(warmup.Object);
        });
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", original.Region.UserId);

        using var response = await client.PutAsync($"/api/trips/places/{historical.Id}",
            new StringContent("""{"name":"Historical edited","notes":"<p>Updated <strong>history</strong></p><script>alert('x')</script>"}""",
                Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var place = AssertPlaceShape(document.RootElement);
        Assert.Equal(historical.Id, place.GetProperty("id").GetGuid());
        Assert.Equal("Historical edited", place.GetProperty("name").GetString());
        Assert.Equal("<p>Updated <strong>history</strong></p>", place.GetProperty("notes").GetString());
        Assert.Equal(3, place.GetProperty("displayOrder").GetInt32());
        Assert.Equal("museum", place.GetProperty("iconName").GetString());
        Assert.Equal("bg-red", place.GetProperty("markerColor").GetString());
        Assert.Equal("Historical address", place.GetProperty("address").GetString());
        Assert.Equal("Historical museum", place.GetProperty("resolvedFeatureName").GetString());
        Assert.Equal("museum", place.GetProperty("resolvedFeatureType").GetString());
        await using var verify = fixture.CreateContext();
        var persisted = await verify.Places.SingleAsync(item => item.Id == historical.Id);
        Assert.Equal("Historical edited", persisted.Name);
        Assert.Equal("<p>Updated <strong>history</strong></p>", persisted.Notes);
        Assert.Equal("Historical address", persisted.Address);
        Assert.Equal("Historical museum", persisted.ResolvedFeatureName);
        Assert.Equal("museum", persisted.ResolvedFeatureType);
        Assert.NotNull(persisted.Location);
        Assert.Equal(23d, persisted.Location.X);
        Assert.True(double.IsNaN(persisted.Location.Y));
        Assert.Equal(4326, persisted.Location.SRID);
        Assert.Equal(1, saves);
        warmup.Verify(item => item.ScheduleWarmupAsync(original.Region.TripId, false), Times.Once);
        warmup.VerifyNoOtherCalls();
        // Keep this last so the red-first run proves all existing mutation/persistence behavior first.
        Assert.Equal(JsonValueKind.Null, place.GetProperty("location").ValueKind);
    }

    /// <summary>Freezes the successful envelope and ten Place keys without imposing JSON property order.</summary>
    private static JsonElement AssertPlaceShape(JsonElement root)
    {
        Assert.Equal(new[] { "place", "success" }, root.EnumerateObject().Select(item => item.Name).Order());
        Assert.Equal(JsonValueKind.True, root.GetProperty("success").ValueKind);
        var place = root.GetProperty("place");
        Assert.Equal(new[]
        {
            "id", "name", "notes", "displayOrder", "iconName", "markerColor", "address",
            "resolvedFeatureName", "resolvedFeatureType", "location"
        }.Order(), place.EnumerateObject().Select(item => item.Name).Order());
        return place;
    }

    /// <summary>Seeds fixture-owned bearer authority and a waypoint route with sibling orders and retained provenance.</summary>
    private async Task<CoordinateSeed> SeedAsync()
    {
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Coordinates" };
        fixture.RegisterTrip(trip.Id);
        var region = new Region
        {
            Id = Guid.NewGuid(), Trip = trip, UserId = user.Id, Name = "Original", DisplayOrder = 1,
            Notes = "Original", CoverImageUrl = "original-cover", Center = new Point(23, 37) { SRID = 4326 }
        };
        var destination = new Region
        {
            Id = Guid.NewGuid(), Trip = trip, UserId = user.Id, Name = "Destination", DisplayOrder = 2
        };
        var place = new Place
        {
            Id = Guid.NewGuid(), Region = region, UserId = user.Id, Name = "Original", DisplayOrder = 1,
            Notes = "Original", Address = "Original address", IconName = "original-icon", MarkerColor = "original-color",
            Location = new Point(23, 37) { SRID = 4326 }, ResolvedFeatureName = "Original feature", ResolvedFeatureType = "museum",
            AddressEnrichmentProvider = "geoapify", AddressEnrichmentStorageMode = "retained",
            AddressEnrichedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)
        };
        var sibling = new Place
        {
            Id = Guid.NewGuid(), Region = region, UserId = user.Id, Name = "Sibling", DisplayOrder = 2,
            Location = new Point(24, 38) { SRID = 4326 }
        };
        var segment = new Segment
        {
            Id = Guid.NewGuid(), Trip = trip, UserId = user.Id, FromPlace = sibling, ToPlace = sibling, DisplayOrder = 1,
            RouteGeometry = new LineString([new(24, 38), new(23, 37), new(24, 38)]) { SRID = 4326 },
            EstimatedDistanceKm = 2, EstimatedDuration = TimeSpan.FromMinutes(10)
        };
        segment.Waypoints.Add(new SegmentWaypoint { Place = place, Position = 0, RouteVertexIndex = 1 });
        await using var db = fixture.CreateContext();
        db.ApiTokens.Add(new ApiToken
        {
            UserId = user.Id, User = null!, Name = "coordinates", TokenHash = ApiTokenService.HashToken(user.Id)
        });
        db.Regions.AddRange(region, destination);
        db.Places.AddRange(place, sibling);
        db.Segments.Add(segment);
        await db.SaveChangesAsync();
        // Compare PostgreSQL's committed microsecond timestamp after any rejected request.
        await db.Entry(trip).ReloadAsync();
        return new(region, destination, place, sibling, segment);
    }

    /// <summary>Reads committed state independently, including fallback absence and lifecycle-owned dependencies.</summary>
    private async Task AssertUnchangedAsync(CoordinateSeed expected)
    {
        await using var db = fixture.CreateContext();
        var region = await db.Regions.Include(item => item.Trip).SingleAsync(item => item.Id == expected.Region.Id);
        Assert.Equal((expected.Region.Name, expected.Region.Notes, expected.Region.CoverImageUrl, expected.Region.DisplayOrder),
            (region.Name, region.Notes, region.CoverImageUrl, region.DisplayOrder));
        Assert.Equal((23d, 37d, 4326), (region.Center!.X, region.Center.Y, region.Center.SRID));
        Assert.Equal(expected.Region.Trip.UpdatedAt, region.Trip.UpdatedAt);
        Assert.Equal(2, await db.Regions.CountAsync(item => item.TripId == region.TripId));
        Assert.False(await db.Regions.AnyAsync(item => item.TripId == region.TripId && item.Name == "Unassigned Places"));
        var places = await db.Places.Where(item => item.Region.TripId == region.TripId).OrderBy(item => item.DisplayOrder).ToArrayAsync();
        Assert.Equal(new[] { expected.Place.Id, expected.Sibling.Id }, places.Select(item => item.Id));
        Assert.Equal(new int?[] { 1, 2 }, places.Select(item => item.DisplayOrder));
        Assert.All(places, item => Assert.Equal(region.Id, item.RegionId));
        var place = places[0];
        Assert.Equal((expected.Place.Name, expected.Place.Notes, expected.Place.IconName, expected.Place.MarkerColor),
            (place.Name, place.Notes, place.IconName, place.MarkerColor));
        Assert.Equal((23d, 37d, 4326), (place.Location!.X, place.Location.Y, place.Location.SRID));
        Assert.Equal(expected.Place.Address, place.Address);
        Assert.Equal(expected.Place.ResolvedFeatureName, place.ResolvedFeatureName);
        Assert.Equal(expected.Place.ResolvedFeatureType, place.ResolvedFeatureType);
        Assert.Equal(expected.Place.AddressEnrichmentProvider, place.AddressEnrichmentProvider);
        Assert.Equal(expected.Place.AddressEnrichmentStorageMode, place.AddressEnrichmentStorageMode);
        Assert.Equal(expected.Place.AddressEnrichedAt, place.AddressEnrichedAt);
        var segment = await db.Segments.Include(item => item.Waypoints).SingleAsync(item => item.Id == expected.Segment.Id);
        Assert.Equal(expected.Segment.RowVersion, segment.RowVersion);
        Assert.Equal(expected.Segment.RouteGeometry!.Coordinates, segment.RouteGeometry!.Coordinates);
        Assert.Equal(expected.Segment.EstimatedDistanceKm, segment.EstimatedDistanceKm);
        Assert.Equal(expected.Segment.EstimatedDuration, segment.EstimatedDuration);
        Assert.Equal((expected.Sibling.Id, expected.Sibling.Id), (segment.FromPlaceId, segment.ToPlaceId));
        var waypoint = Assert.Single(segment.Waypoints);
        Assert.Equal((expected.Place.Id, 0, 1), (waypoint.PlaceId, waypoint.Position, waypoint.RouteVertexIndex));
    }

    /// <summary>Retains only fixture-owned original entities needed to compare independent persisted reads.</summary>
    private sealed record CoordinateSeed(Region Region, Region Destination, Place Place, Place Sibling, Segment Segment);
}
