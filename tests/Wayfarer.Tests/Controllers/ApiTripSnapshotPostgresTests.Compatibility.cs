using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class ApiTripSnapshotPostgresTests
{
    /// <summary>Freezes the existing public/private, bearer/cookie and absent-resource status boundaries.</summary>
    [PostgresTheory]
    [InlineData(true, "invalid", false, 200)]
    [InlineData(true, "none", true, 200)]
    [InlineData(false, "owner", false, 200)]
    [InlineData(false, "owner", true, 200)]
    [InlineData(false, "other", true, 401)]
    [InlineData(false, "none", true, 401)]
    [InlineData(false, "invalid", false, 401)]
    [InlineData(false, "revoked", false, 401)]
    [InlineData(false, "inactive", false, 401)]
    [InlineData(false, "absent", false, 404)]
    public async Task HttpAuthority_IsCompatible(bool isPublic, string bearer, bool cookie, int status)
    {
        var trip = await SeedAsync(isPublic);
        var other = await fixture.CreateUserAsync();
        await TokenAsync(other.Id);
        await using var db = fixture.CreateContext();
        if (bearer == "revoked") await db.ApiTokens.Where(row => row.UserId == trip.UserId).ExecuteDeleteAsync();
        if (bearer == "inactive") await db.Users.Where(row => row.Id == trip.UserId).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        var gate = new ReadGate(pause: false);
        await using var app = await StartAsync(db, gate);
        using var client = app.GetTestClient();
        if (cookie) UserConnectionTokenHttpTests.Cookie(client, app.Services, bearer == "owner" ? other.Id : trip.UserId);
        if (bearer != "none") client.DefaultRequestHeaders.Authorization = new("Bearer",
            bearer is "owner" or "revoked" or "inactive" ? trip.UserId : bearer == "other" ? other.Id : "invalid");
        using var response = await client.GetAsync($"/api/trips/{(bearer == "absent" ? Guid.NewGuid() : trip.Id)}");
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        if (status == 200)
        {
            using var json = await ReadJsonAsync(response);
            Assert.Equal(trip.Id, json.RootElement.GetProperty("id").GetGuid());
        }
        else Assert.DoesNotContain(Place(trip).Id.ToString(), await response.Content.ReadAsStringAsync());
        if (isPublic || bearer == "absent")
            Assert.DoesNotContain(gate.Commands, sql => sql.Contains("ApiTokens") || sql.Contains("AspNetUsers"));
        AssertNoWrites(gate);
    }

    /// <summary>All four independent center states retain complete sparse, nullable, holed and cross-Region wire content.</summary>
    [PostgresTheory]
    [InlineData(null, null)]
    [InlineData(40d, null)]
    [InlineData(null, 23d)]
    [InlineData(40d, 23d)]
    public async Task CompleteGraph_PreservesWireShapeAndLegacyValues(double? latitude, double? longitude)
    {
        var trip = await SeedAsync();
        var anchor = Place(trip);
        var destination = trip.Regions.Single(region => region.Id != anchor.RegionId);
        var via = new Place { Id = Guid.NewGuid(), RegionId = destination.Id, UserId = trip.UserId, Name = "Via",
            Location = new Point(24, 38) { SRID = 4326 }, DisplayOrder = null };
        var unlocated = new Place { Id = Guid.NewGuid(), RegionId = destination.Id, UserId = trip.UserId, Name = "Unlocated", DisplayOrder = 44 };
        var tag = new Tag { Id = Guid.NewGuid(), Name = "Snapshot tag", Slug = "snapshot-" + trip.Id.ToString("N") };
        fixture.RegisterTag(tag);
        var area = new Area
        {
            Id = Guid.NewGuid(), RegionId = destination.Id, Name = "Hole", DisplayOrder = null,
            Geometry = new Polygon(new LinearRing([new(20, 30), new(28, 30), new(28, 40), new(20, 40), new(20, 30)]),
                [new LinearRing([new(21, 31), new(21, 32), new(22, 32), new(22, 31), new(21, 31)])]) { SRID = 4326 }
        };
        var historicalProfile = Guid.NewGuid();
        await using var db = fixture.CreateContext();
        var stored = await db.Trips.Include(item => item.Tags).SingleAsync(item => item.Id == trip.Id);
        stored.CenterLat = latitude;
        stored.CenterLon = longitude;
        stored.CoverImageUrl = "https://example.invalid/metadata-only.jpg";
        stored.Notes = "<p>Safe</p><script>bad()</script>";
        db.Tags.Add(tag);
        stored.Tags.Add(tag);
        db.Places.AddRange(via, unlocated);
        db.Areas.Add(area);
        db.Regions.Add(new Region { Id = Guid.NewGuid(), TripId = trip.Id, UserId = trip.UserId, Name = "Empty", DisplayOrder = 9 });
        var custom = await db.Segments.SingleAsync(item => item.TripId == trip.Id);
        custom.Waypoints.Add(new SegmentWaypoint { Place = via, Position = 0, RouteVertexIndex = 1 });
        custom.EstimatedDuration = TimeSpan.FromSeconds(81.25);
        custom.EstimatedDistanceKm = 1.23456789;
        custom.RouteProvider = "geoapify";
        custom.RouteTransportProfileId = historicalProfile;
        custom.RouteInstructionsJson = "{\"steps\":[]}";
        custom.RouteAttribution = "Retained attribution";
        custom.RouteStorageMode = "retained";
        db.Segments.Add(new Segment { Id = Guid.NewGuid(), TripId = trip.Id, UserId = trip.UserId,
            Mode = "walk", DisplayOrder = 17, FromPlaceId = anchor.Id, ToPlaceId = anchor.Id,
            Waypoints = [new SegmentWaypoint { Place = via, Position = 0 }] });
        db.Segments.Add(new Segment { Id = Guid.NewGuid(), TripId = trip.Id, UserId = trip.UserId, Mode = "legacy", DisplayOrder = 17 });
        await db.SaveChangesAsync();
        var before = stored.UpdatedAt;
        var gate = new ReadGate(pause: false);
        await using var app = await StartAsync(db, gate);
        using var client = app.GetTestClient();
        using var response = await client.GetAsync($"/api/trips/{trip.Id}");
        using var document = await ReadJsonAsync(response);
        var json = document.RootElement;
        Assert.Equal(new[] { "id", "name", "notes", "isPublic", "centerLat", "centerLon", "zoom", "coverImageUrl", "updatedAt", "regions", "segments", "tags" }.Order(),
            json.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(latitude.HasValue ? JsonValueKind.Number : JsonValueKind.Null, json.GetProperty("centerLat").ValueKind);
        Assert.Equal(longitude.HasValue ? JsonValueKind.Number : JsonValueKind.Null, json.GetProperty("centerLon").ValueKind);
        Assert.Equal(latitude, json.GetProperty("centerLat").Deserialize<double?>());
        Assert.Equal(longitude, json.GetProperty("centerLon").Deserialize<double?>());
        Assert.Equal("<p>Safe</p>", json.GetProperty("notes").GetString());
        Assert.Equal(stored.CoverImageUrl, json.GetProperty("coverImageUrl").GetString());
        Assert.Equal(tag.Id, json.GetProperty("tags")[0].GetProperty("id").GetGuid());
        var regions = json.GetProperty("regions").EnumerateArray().ToArray();
        var empty = regions.Single(region => region.GetProperty("name").GetString() == "Empty");
        Assert.Equal(0, empty.GetProperty("places").GetArrayLength());
        Assert.Equal(0, empty.GetProperty("areas").GetArrayLength());
        var to = regions.Single(region => region.GetProperty("id").GetGuid() == destination.Id);
        Assert.Equal(JsonValueKind.Null, to.GetProperty("places")[0].GetProperty("displayOrder").ValueKind);
        Assert.Equal(JsonValueKind.Null, to.GetProperty("places")[1].GetProperty("location").ValueKind);
        using var polygon = JsonDocument.Parse(to.GetProperty("areas")[0].GetProperty("geometryGeoJson").GetString()!);
        Assert.Equal("Polygon", polygon.RootElement.GetProperty("type").GetString());
        Assert.Equal(2, polygon.RootElement.GetProperty("coordinates").GetArrayLength());
        Assert.Equal(5, polygon.RootElement.GetProperty("coordinates")[1].GetArrayLength());
        Assert.Equal("#3388FF", to.GetProperty("areas")[0].GetProperty("fillHex").GetString());
        var segments = json.GetProperty("segments").EnumerateArray().ToArray();
        Assert.Equal(3, segments.Length);
        var route = segments.Single(segment => segment.GetProperty("id").GetGuid() == custom.Id);
        Assert.Equal(81.25 / 60, route.GetProperty("estimatedDurationMinutes").GetDouble(), 10);
        Assert.Equal(1.23456789, route.GetProperty("estimatedDistanceKm").GetDouble());
        Assert.True(route.GetProperty("hasCustomRoute").GetBoolean());
        Assert.Equal(historicalProfile, route.GetProperty("routeTransportProfileId").GetGuid());
        Assert.Equal(JsonValueKind.Null, route.GetProperty("transportProfileId").ValueKind);
        Assert.Equal(via.Id, route.GetProperty("waypoints")[0].GetProperty("placeId").GetGuid());
        Assert.Equal(1, route.GetProperty("waypoints")[0].GetProperty("routeVertexIndex").GetInt32());
        var fallback = segments.Single(segment => segment.GetProperty("waypoints").GetArrayLength() == 1 && !segment.GetProperty("hasCustomRoute").GetBoolean());
        using var fallbackLine = JsonDocument.Parse(fallback.GetProperty("routeJson").GetString()!);
        Assert.Equal(3, fallbackLine.RootElement.GetProperty("coordinates").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, fallback.GetProperty("waypoints")[0].GetProperty("routeVertexIndex").ValueKind);
        var legacy = segments.Single(segment => segment.GetProperty("mode").GetString() == "legacy");
        foreach (var property in new[] { "fromPlaceId", "toPlaceId", "routeJson", "routeProvider", "routeTransportProfileId", "routeInstructionsJson" })
            Assert.Equal(JsonValueKind.Null, legacy.GetProperty(property).ValueKind);
        AssertNoWrites(gate);
        await using var verify = fixture.CreateContext();
        Assert.Equal(before, (await verify.Trips.SingleAsync(item => item.Id == trip.Id)).UpdatedAt);
        Assert.Contains("<script>", (await verify.Trips.SingleAsync(item => item.Id == trip.Id)).Notes);
        Assert.Equal(4326, (await verify.Areas.SingleAsync(item => item.Id == area.Id)).Geometry.SRID);
    }

    /// <summary>Legitimately storable missing Area content and contradictory custom/foreign routes fail the whole read.</summary>
    [PostgresTheory]
    [InlineData("area")]
    [InlineData("route")]
    [InlineData("foreign")]
    public async Task InvalidRequiredContent_ReturnsBoundedWholeReadFailure(string failure)
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        if (failure == "area") db.Areas.Add(new Area { Id = Guid.NewGuid(), RegionId = Place(trip).RegionId,
            Name = "PRIVATE-AREA-852", Geometry = new Polygon(new LinearRing([])) { SRID = 4326 } });
        else
        {
            var segment = await db.Segments.SingleAsync(item => item.TripId == trip.Id);
            if (failure == "route") segment.RouteGeometry = new LineString([new(1, 1), new(2, 2)]) { SRID = 4326 };
            else
            {
                var other = await SeedAsync(false);
                segment.FromPlaceId = Place(other).Id;
            }
        }
        await db.SaveChangesAsync();
        var before = (await db.Trips.AsNoTracking().SingleAsync(item => item.Id == trip.Id)).UpdatedAt;
        var gate = new ReadGate(pause: false);
        await using var app = await StartAsync(db, gate);
        using var client = app.GetTestClient();
        using var response = await client.GetAsync($"/api/trips/{trip.Id}");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "message", "success" }, json.RootElement.EnumerateObject().Select(item => item.Name).Order());
        Assert.Equal("The trip could not be loaded.", json.RootElement.GetProperty("message").GetString());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        AssertNoWrites(gate);
        await using var verify = fixture.CreateContext();
        Assert.Equal(before, (await verify.Trips.SingleAsync(item => item.Id == trip.Id)).UpdatedAt);
    }

    /// <summary>A failure in the actual configured MVC converter cannot publish a partial successful JSON body.</summary>
    [PostgresFact]
    public async Task JsonPreparationFailure_ReturnsFailureBeforeAnySuccessPayload()
    {
        var trip = await SeedAsync();
        var converter = new FailingTripConverter();
        var gate = new ReadGate(pause: false);
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, gate, services => services.Configure<JsonOptions>(options =>
            options.JsonSerializerOptions.Converters.Add(converter)));
        using var client = app.GetTestClient();
        using var response = await client.GetAsync($"/api/trips/{trip.Id}");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("partial-private", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, converter.Passes);
        AssertNoWrites(gate);
    }

    /// <summary>Models a deterministic serializer failure at the existing MVC configuration seam.</summary>
    private sealed class FailingTripConverter : JsonConverter<ApiTripDto>
    {
        internal int Passes;
        public override ApiTripDto Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, ApiTripDto value, JsonSerializerOptions options)
        {
            Passes++;
            writer.WriteStartObject();
            writer.WriteString("name", "partial-private");
            throw new JsonException("Test-owned deterministic projection failure.");
        }
    }
}
