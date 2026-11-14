using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Npgsql;
using JobBuilder = Quartz.JobBuilder;
using IJobExecutionContext = Quartz.IJobExecutionContext;
using Wayfarer.Jobs;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class ApiTripSnapshotPostgresTests
{
    /// <summary>Editor and cache snapshots retain every child without joining independent collections into a product.</summary>
    [PostgresFact]
    public async Task EditorAndWarmup_PreserveMultiChildGraphWithSnapshotSplitQueries()
    {
        var trip = await SeedAsync(false);
        var urls = new HashSet<string>();
        await using var db = fixture.CreateContext();
        var stored = await db.Trips.SingleAsync(t => t.Id == trip.Id);
        stored.Notes = Image("trip");
        foreach (var region in trip.Regions)
        {
            var tracked = await db.Regions.SingleAsync(r => r.Id == region.Id);
            tracked.Notes = Image(region.Id.ToString());
            for (var index = 0; index < 2; index++)
            {
                db.Places.Add(new Place { Id = Guid.NewGuid(), RegionId = region.Id, UserId = trip.UserId,
                    Name = $"Child {index}", Location = new Point(24 + index, 38) { SRID = 4326 },
                    Notes = Image($"place-{region.Id}-{index}") });
                db.Areas.Add(new Area { Id = Guid.NewGuid(), RegionId = region.Id, Name = $"Area {index}",
                    Notes = Image($"area-{region.Id}-{index}"),
                    Geometry = new Polygon(new LinearRing([new(20, 30), new(21, 30), new(21, 31), new(20, 30)])) { SRID = 4326 } });
            }
        }
        for (var index = 0; index < 2; index++)
        {
            var tag = new Tag { Id = Guid.NewGuid(), Name = $"Graph {index}", Slug = $"graph-{trip.Id}-{index}" };
            fixture.RegisterTag(tag);
            db.Tags.Add(tag);
            stored.Tags.Add(tag);
        }
        await db.SaveChangesAsync();
        var places = await db.Places.Where(p => p.Region.TripId == trip.Id).OrderBy(p => p.Id).ToArrayAsync();
        var segment = new Segment { Id = Guid.NewGuid(), TripId = trip.Id, UserId = trip.UserId,
            Mode = "walk", Title = "Complete route", FromPlaceId = places[0].Id, ToPlaceId = places[1].Id,
            Waypoints = [new() { PlaceId = places[2].Id, Position = 0 }, new() { PlaceId = places[3].Id, Position = 1 }] };
        db.Segments.Add(segment); await db.SaveChangesAsync();
        var gate = new ReadGate();
        await using var app = await StartAsync(db, gate);
        using var client = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(client, app.Services, trip.UserId);
        var pending = client.GetAsync($"/api/trips/{trip.Id}/editor");
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(System.Data.IsolationLevel.RepeatableRead, gate.Isolation);
            // Commit a child change after the root read; the editor must still see the captured graph.
            await db.Places.Where(p => p.Id == places[2].Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "After snapshot"));
        }
        finally { gate.Release.TrySetResult(); }
        using var response = await pending;
        using var json = await ReadJsonAsync(response);
        var state = json.RootElement;
        Assert.Equal(2, state.GetProperty("regionsById").EnumerateObject().Count());
        Assert.Equal(5, state.GetProperty("placesById").EnumerateObject().Count());
        Assert.Equal(4, state.GetProperty("areasById").EnumerateObject().Count());
        Assert.Equal(2, state.GetProperty("segmentsById").EnumerateObject().Count());
        Assert.Equal(2, state.GetProperty("tagsBySlug").EnumerateObject().Count());
        Assert.Equal(places[2].Name, state.GetProperty("placesById").GetProperty(places[2].Id.ToString()).GetProperty("name").GetString());
        var route = state.GetProperty("segmentsById").GetProperty(segment.Id.ToString());
        Assert.Equal(segment.FromPlaceId, route.GetProperty("fromPlaceId").GetGuid());
        Assert.Equal(segment.ToPlaceId, route.GetProperty("toPlaceId").GetGuid());
        Assert.Equal(new[] { places[2].Id, places[3].Id }, route.GetProperty("waypointPlaceIds").EnumerateArray().Select(p => p.GetGuid()));
        Assert.Equal(7, gate.Commands.Count(sql => sql.Contains("FROM \"Trips\"")));

        var cacheGate = new ReadGate();
        await using var cacheDb = fixture.CreateContext(cacheGate);
        var fetched = new HashSet<string>();
        var proxy = new Mock<IImageProxyService>();
        proxy.Setup(p => p.FetchAndCacheAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((url, _) => { Assert.Null(cacheDb.Database.CurrentTransaction); fetched.Add(url); })
            .ReturnsAsync(true);
        var detail = JobBuilder.Create<CacheWarmupJob>().UsingJobData("tripId", trip.Id.ToString()).Build();
        var context = new Mock<IJobExecutionContext>(); context.SetupGet(c => c.JobDetail).Returns(detail);
        var warmup = new CacheWarmupJob(cacheDb, proxy.Object, NullLogger<CacheWarmupJob>.Instance).Execute(context.Object);
        try
        {
            await cacheGate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(System.Data.IsolationLevel.RepeatableRead, cacheGate.Isolation);
        }
        finally { cacheGate.Release.TrySetResult(); }
        await warmup;
        Assert.True(urls.SetEquals(fetched));
        Assert.Equal(4, cacheGate.Commands.Count(sql => sql.Contains("FROM \"Trips\"")));

        string Image(string key)
        {
            var url = $"https://example.invalid/{key}.png"; urls.Add(url);
            return $"<img src=\"{url}\">";
        }
    }

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
            // Cookie validation now reads the current account before the snapshot; bearer authority remains unresolved.
            Assert.DoesNotContain(gate.Commands, sql => sql.Contains("ApiTokens") || !cookie && sql.Contains("AspNetUsers"));
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
        var tag = new Tag { Id = Guid.NewGuid(), Name = "Snapshot " + trip.Id.ToString("N"), Slug = "snapshot-" + trip.Id.ToString("N") };
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
        await db.Entry(stored).ReloadAsync();
        var before = stored.UpdatedAt;
        var gate = new ReadGate(pause: false);
        await using var app = await StartAsync(db, gate);
        using var client = app.GetTestClient();
        using var response = await client.GetAsync($"/api/trips/{trip.Id}");
        using var document = await ReadJsonAsync(response);
        var json = document.RootElement;
        Assert.Equal(new[] { "id", "name", "notes", "isPublic", "centerLat", "centerLon", "zoom", "coverImageUrl", "updatedAt", "regions", "segments", "tags", "contributionPolicy", "credits" }.Order(),
            json.EnumerateObject().Select(property => property.Name).Order());
        // Additive portable fields retain legacy absence without manufacturing a reuse grant.
        Assert.Equal(JsonValueKind.Null, json.GetProperty("contributionPolicy").ValueKind);
        Assert.Empty(json.GetProperty("credits").EnumerateArray());
        Assert.Equal(latitude.HasValue ? JsonValueKind.Number : JsonValueKind.Null, json.GetProperty("centerLat").ValueKind);
        Assert.Equal(longitude.HasValue ? JsonValueKind.Number : JsonValueKind.Null, json.GetProperty("centerLon").ValueKind);
        Assert.Equal(latitude, json.GetProperty("centerLat").Deserialize<double?>());
        Assert.Equal(longitude, json.GetProperty("centerLon").Deserialize<double?>());
        Assert.Equal("<p>Safe</p>", json.GetProperty("notes").GetString());
        Assert.Equal(stored.CoverImageUrl, json.GetProperty("coverImageUrl").GetString());
        Assert.Equal(tag.Id, json.GetProperty("tags")[0].GetProperty("id").GetGuid());
        Assert.Equal(new[] { "id", "name", "slug" }, json.GetProperty("tags")[0].EnumerateObject().Select(p => p.Name).Order());
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
        Assert.Equal(custom.TransportProfileId, route.GetProperty("transportProfileId").GetGuid());
        Assert.NotEqual(historicalProfile, custom.TransportProfileId);
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
