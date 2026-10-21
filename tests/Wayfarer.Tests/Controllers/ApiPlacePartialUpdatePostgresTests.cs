using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Parsers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Controllers;

/// <summary>Proves legacy partial Place intent against locked canonical PostgreSQL state and routed Web writers.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class ApiPlacePartialUpdatePostgresTests(PostgresImportTestFixture fixture, ITestOutputHelper output) : TestBase
{
    /// <summary>Omitted fields and dependencies survive a Web commit after the bearer read or lock discovery.</summary>
    [PostgresTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NotesOnlyPutPreservesConcurrentWebEdit(bool move, bool afterDiscovery)
    {
        var seed = await SeedAsync();
        var gate = new ReadGate(afterDiscovery);
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, gate);
        using var web = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(web, app.Services, seed.UserId);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(web);
        using var bearer = app.GetTestClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", seed.UserId);
        var pending = bearer.PutAsJsonAsync($"/api/trips/places/{seed.Id}", new { notes = "Phone notes" });
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
            using var webResponse = await web.PutAsJsonAsync($"/api/trips/{seed.Region.TripId}/editor/places/{seed.Id}", new
            {
                regionId = move ? seed.Region.Trip.Regions.Single(region => region.Name == "Target").Id : seed.RegionId,
                name = "Web name", notesHtml = "Original notes", address = "Web address",
                location = new { latitude = 14, longitude = 22 }, iconName = "marker", markerColor = "bg-blue",
                reverseGeocode = false
            });
            Assert.True(webResponse.StatusCode == HttpStatusCode.OK, await webResponse.Content.ReadAsStringAsync());
            var expected = await ReadAsync(seed);
            Assert.Equal("Web name", expected.Place.Name);
            Assert.Equal("POINT (22 14)", expected.Place.Location);
            gate.Release.TrySetResult();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var actual = await ReadAsync(seed);
            output.WriteLine($"Web state: {expected.Place}; bearer state: {actual.Place}; Place lock attempts: {gate.PlaceLocks}");
            output.WriteLine($"Web dependencies: {expected.Dependencies}\nBearer dependencies: {actual.Dependencies}");
            Assert.Equal(expected.Place with { Notes = "Phone notes" }, actual.Place);
            Assert.Equal(expected.Dependencies, actual.Dependencies);
            Assert.True(actual.UpdatedAt >= expected.UpdatedAt);
            if (afterDiscovery) Assert.Equal(2, gate.PlaceLocks);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(new[] { "place", "success" }, body.RootElement.EnumerateObject().Select(field => field.Name).Order());
            Assert.True(body.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("Web name", body.RootElement.GetProperty("place").GetProperty("name").GetString());
        }
        finally
        {
            gate.Release.TrySetResult();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    /// <summary>Null and omitted optional fields preserve nullable and empty persisted scalars, including Address.</summary>
    [PostgresTheory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("Authored", false)]
    [InlineData("Authored", true)]
    public async Task OmittedOrNullValuesRemainUnchanged(string? stored, bool explicitNull)
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        var place = await db.Places.SingleAsync(item => item.Id == seed.Id);
        place.Notes = place.Address = place.IconName = place.MarkerColor = stored;
        place.Location = null;
        // A missing location is valid here because this control intentionally has no route dependencies.
        await db.Segments.Where(item => item.TripId == seed.Region.TripId).ExecuteDeleteAsync();
        await db.SaveChangesAsync();
        var expected = await ReadAsync(seed);
        await using var app = await StartAsync(db);
        using var bearer = app.GetTestClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", seed.UserId);
        object payload = explicitNull
            ? new { name = " ", notes = (string?)null, iconName = (string?)null, markerColor = (string?)null,
                latitude = (double?)null, longitude = (double?)null, regionId = (Guid?)null, displayOrder = (int?)null }
            : new { };

        using var response = await bearer.PutAsJsonAsync($"/api/trips/places/{seed.Id}", payload);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var actual = await ReadAsync(seed);
        Assert.Equal(expected.Place, actual.Place);
        Assert.Equal(expected.Dependencies, actual.Dependencies);
    }

    /// <summary>Explicit Set, Clear, marker resets, coordinate reconciliation, moves and order keep legacy behavior.</summary>
    [PostgresFact]
    public async Task ExplicitUpdatesRetainLegacySemantics()
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        var controller = Controller(db, seed.UserId);
        Assert.IsType<OkObjectResult>(await controller.UpdatePlace(seed.Id, new PlaceUpdateRequestDto
        {
            Name = "Explicit name", Notes = "<p>Set</p>", IconName = "star", MarkerColor = "bg-red",
            Latitude = 14, Longitude = 22,
            RegionId = seed.Region.Trip.Regions.Single(region => region.Name == "Target").Id, DisplayOrder = 1
        }));
        var changed = await ReadAsync(seed);
        Assert.Equal(("Explicit name", "<p>Set</p>", "star", "bg-red", "POINT (22 14)", 1),
            (changed.Place.Name, changed.Place.Notes, changed.Place.Icon, changed.Place.Color, changed.Place.Location, changed.Place.Order));
        Assert.NotEqual(seed.RegionId, changed.Place.RegionId);
        Assert.Equal("Old address", changed.Place.Address);
        Assert.Equal(Metadata(seed), changed.Place.Metadata);
        await using (var verify = fixture.CreateContext())
        {
            var segment = await verify.Segments.SingleAsync(item => item.TripId == seed.Region.TripId);
            Assert.Equal(new Coordinate(22, 14), segment.RouteGeometry!.Coordinates[1]);
            Assert.Null(segment.RouteProvider);
            Assert.NotEqual(999, segment.EstimatedDistanceKm);
            Assert.Equal(TimeSpan.FromMinutes(15), segment.EstimatedDuration);
        }
        Assert.IsType<OkObjectResult>(await controller.UpdatePlace(seed.Id, new PlaceUpdateRequestDto
        { Notes = "", IconName = " ", MarkerColor = "" }));
        var cleared = await ReadAsync(seed);
        Assert.Equal(changed.Place with { Notes = "", Icon = "marker", Color = "bg-blue" }, cleared.Place);
        Assert.Equal(changed.Dependencies, cleared.Dependencies);
        Assert.IsType<OkObjectResult>(await controller.UpdatePlace(seed.Id, new PlaceUpdateRequestDto
        { IconName = "star", MarkerColor = "bg-red", ClearIcon = true, ClearMarkerColor = true }));
        Assert.Equal(cleared.Place, (await ReadAsync(seed)).Place);
    }

    /// <summary>Failure after accepted SQL rolls back and restores tracker state; serialization retries resolve intent again.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSaveDoesNotLeakPartialIntent(bool retry)
    {
        var seed = await SeedAsync();
        var expected = await ReadAsync(seed);
        var failure = new FailAfterSave(retry);
        await using var db = fixture.CreateContext(failure);
        var operation = () => Controller(db, seed.UserId).UpdatePlace(seed.Id, new PlaceUpdateRequestDto { Notes = "Phone notes" });
        if (retry)
        {
            Assert.IsType<OkObjectResult>(await operation());
            Assert.Equal(2, failure.Saves);
            expected = expected with { Place = expected.Place with { Notes = "Phone notes" } };
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(operation);
            Assert.DoesNotContain(db.ChangeTracker.Entries(), entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
            await db.SaveChangesAsync();
        }
        var actual = await ReadAsync(seed);
        Assert.Equal(expected.Place, actual.Place);
        Assert.Equal(expected.Dependencies, actual.Dependencies);
        if (!retry) Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
    }

    /// <summary>Builds a fixture-owned waypoint route with complete retained metadata and sibling orders.</summary>
    private async Task<Place> SeedAsync()
    {
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "853 Slice 2" };
        fixture.RegisterTrip(trip.Id);
        var source = new Region { Id = Guid.NewGuid(), Trip = trip, UserId = user.Id, Name = "Source", DisplayOrder = 1 };
        var target = new Region { Id = Guid.NewGuid(), Trip = trip, UserId = user.Id, Name = "Target", DisplayOrder = 2 };
        trip.Regions.Add(source);
        trip.Regions.Add(target);
        var places = new[] { "From", "Waypoint", "To" }.Select((name, index) => new Place
        {
            Id = Guid.NewGuid(), Region = source, UserId = user.Id, Name = name, DisplayOrder = index + 1,
            Location = new Point(20 + index, 10 + index) { SRID = 4326 }
        }).ToArray();
        foreach (var place in places) source.Places.Add(place);
        var moving = places[1];
        moving.Notes = "Original notes";
        moving.Address = "Old address";
        moving.IconName = "star";
        moving.MarkerColor = "bg-red";
        moving.ResolvedFeatureName = "Retained feature";
        moving.ResolvedFeatureType = "building";
        moving.AddressEnrichmentProvider = "geoapify";
        moving.AddressEnrichmentStorageMode = "persistent";
        moving.AddressEnrichedAt = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var segment = new Segment
        {
            Id = Guid.NewGuid(), Trip = trip, UserId = user.Id, FromPlaceId = places[0].Id, ToPlaceId = places[2].Id,
            RouteGeometry = new LineString(places.Select(place => place.Location!.Coordinate.Copy()).ToArray()) { SRID = 4326 },
            EstimatedDistanceKm = 999, EstimatedDuration = TimeSpan.FromMinutes(15), EstimatedDurationSource = EstimatedDurationSource.Manual,
            DisplayOrder = 1, RouteProvider = "geoapify", RouteProviderConfigurationId = Guid.NewGuid(),
            RouteProviderConfigurationVersion = 1, RouteMappingMode = "walk", RouteGeneratedAt = moving.AddressEnrichedAt,
            RouteAttribution = "Fixture attribution", RouteStorageMode = "persistent", RouteInstructionsJson = "[]"
        };
        segment.Waypoints.Add(new SegmentWaypoint { Place = moving, Position = 0, RouteVertexIndex = 1 });
        trip.Segments.Add(segment);
        await using var db = fixture.CreateContext();
        db.Trips.Add(trip);
        db.ApiTokens.Add(new ApiToken { UserId = user.Id, User = null!, Name = "853 Slice 2 fixture", TokenHash = ApiTokenService.HashToken(user.Id) });
        await db.SaveChangesAsync();
        return moving;
    }

    /// <summary>Uses independent request contexts and only replaces existing external-service seams.</summary>
    private Task<WebApplication> StartAsync(ApplicationDbContext db, ReadGate? gate = null) => IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
    {
        services.AddHttpContextAccessor();
        services.AddScoped(provider => fixture.CreateContext(gate != null &&
            provider.GetRequiredService<IHttpContextAccessor>().HttpContext?.Request.Headers.Authorization.ToString().StartsWith("Bearer ") == true ? [gate] : []));
        services.AddSingleton(Mock.Of<ICacheWarmupScheduler>());
        services.AddSingleton(Mock.Of<ITripMapThumbnailGenerator>());
        services.AddSingleton(Mock.Of<ITripEditorGeocodeSearchService>());
        // Supply the existing parser's finite icon/color catalog without depending on the test runner's web root.
        var webRoot = CreateTestDirectory();
        var icons = Path.Combine(webRoot, "icons", "wayfarer-map-icons", "dist", "marker");
        Directory.CreateDirectory(icons);
        File.WriteAllText(Path.Combine(icons, "marker.svg"), "<svg></svg>");
        services.AddSingleton(Mock.Of<IWebHostEnvironment>(environment => environment.WebRootPath == webRoot));
        var colors = new Mock<IIconColorProvider>();
        colors.Setup(provider => provider.GetAvailableColors()).Returns(new IconColorClasses(["bg-blue", "bg-red"], []));
        services.AddSingleton(colors.Object);
    });

    /// <summary>Reads canonical scalars, order, route, waypoint, measurement and provenance without inherited tracking.</summary>
    private async Task<StoredState> ReadAsync(Place seed)
    {
        await using var db = fixture.CreateContext();
        var place = await db.Places.AsNoTracking().SingleAsync(item => item.Id == seed.Id);
        var segments = await db.Segments.AsNoTracking().Where(item => item.TripId == seed.Region.TripId).ToArrayAsync();
        var dependencies = JsonSerializer.Serialize(new
        {
            orders = await db.Places.Where(item => item.Region.TripId == seed.Region.TripId).OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.RegionId, item.DisplayOrder }).ToArrayAsync(),
            segments = segments.Select(segment => new
            {
                segment.Id, segment.RowVersion, segment.FromPlaceId, segment.ToPlaceId, segment.Mode, segment.TransportProfileId,
                route = segment.RouteGeometry?.AsText(), segment.EstimatedDistanceKm, segment.EstimatedDuration, segment.EstimatedDurationSource,
                segment.RouteInstructionsJson, segment.RouteProvider, segment.RouteProviderConfigurationId, segment.RouteProviderConfigurationVersion,
                segment.RouteTransportProfileId, segment.RouteMappingMode, segment.RouteGeneratedAt, segment.RouteAttribution, segment.RouteStorageMode,
                segment.DisplayOrder, segment.Notes
            }),
            waypoints = await db.Set<SegmentWaypoint>().Where(item => item.Segment.TripId == seed.Region.TripId)
                .OrderBy(item => item.SegmentId).ThenBy(item => item.Position)
                .Select(item => new { item.SegmentId, item.PlaceId, item.Position, item.RouteVertexIndex }).ToArrayAsync()
        });
        return new(new(place.Name, place.Notes, place.RegionId, place.DisplayOrder, place.IconName, place.MarkerColor, place.Address,
            place.Location?.AsText(), Metadata(place)), dependencies,
            await db.Trips.Where(item => item.Id == seed.Region.TripId).Select(item => item.UpdatedAt).SingleAsync());
    }

    /// <summary>Extracts all five enrichment fields independently of editable Address.</summary>
    private static ResolvedFeatureTuple Metadata(Place place) => new(place.ResolvedFeatureName, place.ResolvedFeatureType,
        place.AddressEnrichmentProvider, place.AddressEnrichmentStorageMode, place.AddressEnrichedAt);

    /// <summary>Runs the existing bearer action with the fixture's credential and database.</summary>
    private static TripsController Controller(ApplicationDbContext db, string token) => new(db, NullLogger<BaseApiController>.Instance,
        Mock.Of<ITripTagService>(), Mock.Of<IApplicationSettingsService>(), Mock.Of<ICacheWarmupScheduler>())
    { ControllerContext = new ControllerContext { HttpContext = CreateHttpContext(token) } };

    /// <summary>Persisted Place values whose omissions must survive unchanged.</summary>
    private sealed record PlaceState(string Name, string? Notes, Guid RegionId, int? Order, string? Icon, string? Color,
        string? Address, string? Location, ResolvedFeatureTuple Metadata);

    /// <summary>Canonical state read in a third context, including the advisory Trip timestamp.</summary>
    private sealed record StoredState(PlaceState Place, string Dependencies, DateTime UpdatedAt);

    /// <summary>Pauses after the initial tracked read, or after discovery but before the first database lock.</summary>
    private sealed class ReadGate(bool afterDiscovery) : DbCommandInterceptor
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int PlaceLocks { get; private set; }

        /// <summary>Stops the controller's Region validation after the early Place graph has been materialized.</summary>
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!afterDiscovery && command.CommandText.StartsWith("SELECT EXISTS", StringComparison.Ordinal)
                && command.CommandText.Contains("\"Regions\"", StringComparison.Ordinal)
                && eventData.Context!.ChangeTracker.Entries<Place>().Any() && !Reached.Task.IsCompleted)
                await PauseAsync(cancellationToken);
            return result;
        }

        /// <summary>Counts Place locks across rediscovery and gates before any transactional snapshot is acquired.</summary>
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
            {
                if (command.CommandText.Contains("public.\"Places\"", StringComparison.Ordinal)) PlaceLocks++;
                if (afterDiscovery && !Reached.Task.IsCompleted) await PauseAsync(cancellationToken);
            }
            return result;
        }

        /// <summary>Every wait has a deadline and is released by the owning test's finally block.</summary>
        private async Task PauseAsync(CancellationToken cancellationToken)
        {
            Reached.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
    }

    /// <summary>Fails once after SQL acceptance inside the lifecycle-owned transaction.</summary>
    private sealed class FailAfterSave(bool retry) : SaveChangesInterceptor
    {
        internal int Saves { get; private set; }

        /// <summary>Exercises real rollback/recovery, then optionally permits the existing finite retry.</summary>
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (++Saves == 1)
            {
                if (retry) throw new PostgresException("Injected serialization failure", "ERROR", "ERROR", PostgresErrorCodes.SerializationFailure);
                throw new InvalidOperationException("Injected failure after accepted SQL");
            }
            return ValueTask.FromResult(result);
        }
    }
}
