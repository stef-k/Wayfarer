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

/// <summary>Qualifies named-float MVC binding and rejection persistence without repeating the numeric policy matrix.</summary>
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
        using var placeDocument = JsonDocument.Parse(await placeResponse.Content.ReadAsStringAsync());
        Assert.True(placeDocument.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, placeDocument.RootElement.GetProperty("place").GetProperty("location").ValueKind);

        using var placeUpdate = await client.PutAsync($"/api/trips/places/{original.Place.Id}",
            new StringContent("""{"name":"Partial","latitude":null}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, placeUpdate.StatusCode);
        using var regionUpdate = await client.PutAsync($"/api/trips/regions/{original.Region.Id}",
            new StringContent("""{"notes":"Partial","centerLongitude":null}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, regionUpdate.StatusCode);
        await using var verify = fixture.CreateContext();
        var place = await verify.Places.SingleAsync(item => item.Id == original.Place.Id);
        var region = await verify.Regions.SingleAsync(item => item.Id == original.Region.Id);
        Assert.Equal("Partial", place.Name);
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
