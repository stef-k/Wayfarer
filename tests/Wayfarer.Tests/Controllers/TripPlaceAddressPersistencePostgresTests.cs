using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Proves editor metadata, routes, retries and recovery in the existing disposable PostgreSQL fixture.</summary>
[Collection(PostgresMigrationTestCollection.Name)]
public sealed class TripPlaceAddressPersistencePostgresTests(PostgresMigrationTestFixture fixture)
    : TripEditorPlaceControllerTestBase
{
    private static readonly DateTimeOffset OriginalEnrichedAt = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly ResolvedFeatureTuple OriginalMetadata = new("Old feature", "building", "geoapify", "persistent", OriginalEnrichedAt);

    /// <summary>Commits invalidation, waypoint movement, measurements and Region ordering together.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualOrUnavailableUpdateCommitsMetadataAndRouteTogether(bool reverseGeocode)
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        var controller = BuildController(db);
        ConfigureControllerWithUserRole(controller, seed.UserId);

        var envelope = AssertMutation<EditorPlaceDto>(await SendJson(controller,
            c => c.UpdatePlace(seed.TripId, seed.PlaceId, CancellationToken.None),
            Body(seed.TargetRegionId, "Manual replacement", 14, 22, reverseGeocode)));

        await using var verify = fixture.CreateContext();
        var stored = await verify.Places.AsNoTracking().SingleAsync(place => place.Id == seed.PlaceId);
        Assert.Equal("Manual replacement", stored.Address);
        Assert.Equal(default, Metadata(stored));
        Assert.Equal(stored.Address, envelope.Data.Address);
        Assert.Equal(stored.ResolvedFeatureName, envelope.Data.ResolvedFeatureName);
        Assert.Equal(stored.ResolvedFeatureType, envelope.Data.ResolvedFeatureType);
        Assert.Equal(seed.TargetRegionId, stored.RegionId);
        Assert.Equal(1, stored.DisplayOrder);
        Assert.Equal(new[] { 1, 2 }, await verify.Places.Where(place => place.RegionId == seed.RegionId)
            .OrderBy(place => place.DisplayOrder).Select(place => place.DisplayOrder!.Value).ToArrayAsync());
        await AssertMovedRouteAsync(verify, seed);
        Assert.Equal(reverseGeocode ? 1 : 0, envelope.Warnings.Count);
        if (reverseGeocode) Assert.Equal("reverse-geocode-unavailable", envelope.Warnings.Single().Code);
    }

    /// <summary>A real lifecycle retry replaces the complete tuple without a second provider contact or timestamp.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulReplacementSurvivesSerializationRetry(bool retry)
    {
        var seed = await SeedAsync();
        var credentials = await SeedProviderAsync(seed.UserId);
        var original = retry ? new DbUpdateException("Injected serialization failure",
            new PostgresException("serialization", "ERROR", "ERROR", PostgresErrorCodes.SerializationFailure)) : null;
        var probe = new PlaceSaveProbe(original);
        await using var db = fixture.CreateContext(probe);
        using var handler = new ProviderHandler();
        using var client = new HttpClient(handler);
        var controller = BuildController(db, Reverse(db, credentials, client));
        ConfigureControllerWithUserRole(controller, seed.UserId);

        var envelope = AssertMutation<EditorPlaceDto>(await SendJson(controller,
            c => c.UpdatePlace(seed.TripId, seed.PlaceId, CancellationToken.None),
            Body(seed.RegionId, "Fallback", 14, 22, true)));

        await using var verify = fixture.CreateContext();
        var stored = await verify.Places.AsNoTracking().SingleAsync(place => place.Id == seed.PlaceId);
        Assert.Equal("Provider replacement", stored.Address);
        Assert.Equal("New feature", stored.ResolvedFeatureName);
        Assert.Equal("amenity", stored.ResolvedFeatureType);
        Assert.Equal("geoapify", stored.AddressEnrichmentProvider);
        Assert.Equal("persistent", stored.AddressEnrichmentStorageMode);
        Assert.NotEqual(OriginalEnrichedAt, stored.AddressEnrichedAt);
        Assert.Equal(retry ? 2 : 1, probe.Attempts.Count);
        Assert.All(probe.Attempts, tuple => Assert.Equal(probe.Attempts[0], tuple));
        // PostgreSQL timestamps retain microseconds; both attempts still use the identical .NET instant.
        Assert.Equal(probe.Attempts[0].EnrichedAt!.Value.UtcTicks / 10, stored.AddressEnrichedAt!.Value.UtcTicks / 10);
        Assert.Equal(stored.Address, envelope.Data.Address);
        Assert.Equal(stored.ResolvedFeatureName, envelope.Data.ResolvedFeatureName);
        Assert.Empty(envelope.Warnings);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(1, await verify.GeoapifyUsageAdmissions.CountAsync(admission => admission.UserId == seed.UserId));
        await AssertMovedRouteAsync(verify, seed);
    }

    /// <summary>Compatibility uses the concurrent canonical tuple rather than an already tracked editor snapshot.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleTrackedEditorUsesFreshCanonicalMetadata(bool compatible)
    {
        var seed = await SeedAsync();
        await using var stale = fixture.CreateContext();
        await stale.Trips.Include(trip => trip.Regions).ThenInclude(region => region.Places)
            .SingleAsync(trip => trip.Id == seed.TripId);
        var concurrentAt = OriginalEnrichedAt.AddDays(1);
        await using (var writer = fixture.CreateContext())
        {
            var place = await writer.Places.SingleAsync(item => item.Id == seed.PlaceId);
            place.Address = "Concurrent address";
            place.ResolvedFeatureName = "Concurrent feature";
            place.ResolvedFeatureType = "city";
            place.AddressEnrichedAt = concurrentAt;
            await writer.SaveChangesAsync();
        }
        var controller = BuildController(stale);
        ConfigureControllerWithUserRole(controller, seed.UserId);
        var submitted = compatible ? "Concurrent address" : "Old address";

        var envelope = AssertMutation<EditorPlaceDto>(await SendJson(controller,
            c => c.UpdatePlace(seed.TripId, seed.PlaceId, CancellationToken.None), Body(seed.RegionId, submitted, 11, 21, false)));

        await using var verify = fixture.CreateContext();
        var stored = await verify.Places.AsNoTracking().SingleAsync(place => place.Id == seed.PlaceId);
        Assert.Equal(submitted, stored.Address);
        var expected = compatible ? new ResolvedFeatureTuple("Concurrent feature", "city", "geoapify", "persistent", concurrentAt) : default;
        Assert.Equal(expected, Metadata(stored));
        Assert.Equal(stored.ResolvedFeatureName, envelope.Data.ResolvedFeatureName);
    }

    /// <summary>Failure after SQL persistence rolls back the entire aggregate and cannot leak through a later save.</summary>
    [PostgresTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedPersistenceRestoresReusableContext(bool replace, bool cancel)
    {
        var seed = await SeedAsync();
        using var cancellation = new CancellationTokenSource();
        Exception original = cancel ? new OperationCanceledException("Persistence cancelled", cancellation.Token)
            : new InvalidOperationException("Persistence failed after SQL");
        var probe = new PlaceSaveProbe(original, cancel ? cancellation.Cancel : null);
        var credentials = replace ? await SeedProviderAsync(seed.UserId) : null;
        await using var db = fixture.CreateContext(probe);
        using var handler = new ProviderHandler();
        using var client = new HttpClient(handler);
        var controller = BuildController(db, credentials == null ? null : Reverse(db, credentials, client));
        ConfigureControllerWithUserRole(controller, seed.UserId);

        var thrown = await Record.ExceptionAsync(() => SendJson(controller,
            c => c.UpdatePlace(seed.TripId, seed.PlaceId, cancellation.Token),
            Body(seed.TargetRegionId, "Rejected fallback", 14, 22, replace)));

        Assert.Same(original, thrown);
        Assert.Single(probe.Attempts);
        await AssertOriginalStoredAsync(seed);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        await db.SaveChangesAsync(CancellationToken.None);
        await AssertOriginalStoredAsync(seed);
        Assert.Equal(replace ? 1 : 0, handler.Requests);
        await using var verify = fixture.CreateContext();
        Assert.Equal(replace ? 1 : 0, await verify.GeoapifyUsageAdmissions.CountAsync(admission => admission.UserId == seed.UserId));
    }

    /// <summary>Observed provider-handoff cancellation leaves the Place untouched while retaining consumed admission.</summary>
    [PostgresFact]
    public async Task ProviderHandoffCancellationDoesNotMutateTrackedPlace()
    {
        var seed = await SeedAsync();
        var credentials = await SeedProviderAsync(seed.UserId);
        using var cancellation = new CancellationTokenSource();
        await using var db = fixture.CreateContext();
        using var handler = new ProviderHandler(cancellation.Cancel);
        using var client = new HttpClient(handler);
        var controller = BuildController(db, Reverse(db, credentials, client));
        ConfigureControllerWithUserRole(controller, seed.UserId);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SendJson(controller,
            c => c.UpdatePlace(seed.TripId, seed.PlaceId, cancellation.Token), Body(seed.RegionId, "Rejected", 14, 22, true)));

        Assert.Equal(1, handler.Requests);
        await db.SaveChangesAsync(CancellationToken.None);
        await AssertOriginalStoredAsync(seed);
        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.GeoapifyUsageAdmissions.CountAsync(admission => admission.UserId == seed.UserId));
    }

    /// <summary>Seeds a waypoint route and coherent metadata inside the fixture-owned disposable database.</summary>
    private async Task<Seed> SeedAsync()
    {
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Address persistence" };
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id, Name = "Source", DisplayOrder = 1 };
        var target = new Region { Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id, Name = "Target", DisplayOrder = 2 };
        var from = Place(region, "From", 20, 10);
        var waypoint = Place(region, "Waypoint", 21, 11);
        var to = Place(region, "To", 23, 12);
        waypoint.Address = "Old address";
        waypoint.ResolvedFeatureName = OriginalMetadata.Name;
        waypoint.ResolvedFeatureType = OriginalMetadata.Type;
        waypoint.AddressEnrichmentProvider = OriginalMetadata.Provider;
        waypoint.AddressEnrichmentStorageMode = OriginalMetadata.StorageMode;
        waypoint.AddressEnrichedAt = OriginalMetadata.EnrichedAt;
        trip.Regions.Add(region);
        trip.Regions.Add(target);
        var segment = new Segment
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = user.Id,
            FromPlaceId = from.Id, ToPlaceId = to.Id, DisplayOrder = 1,
            RouteGeometry = new LineString([new(20, 10), new(21, 11), new(23, 12)]) { SRID = 4326 },
            EstimatedDistanceKm = 999, EstimatedDuration = TimeSpan.FromMinutes(15), EstimatedDurationSource = EstimatedDurationSource.Manual
        };
        segment.Waypoints.Add(new SegmentWaypoint { Segment = segment, SegmentId = segment.Id, Place = waypoint, PlaceId = waypoint.Id, Position = 0, RouteVertexIndex = 1 });
        trip.Segments.Add(segment);
        await using var db = fixture.CreateContext();
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        var updatedAt = await db.Trips.AsNoTracking().Where(item => item.Id == trip.Id).Select(item => item.UpdatedAt).SingleAsync();
        return new(user.Id, trip.Id, region.Id, target.Id, waypoint.Id, from.Id, to.Id, segment.Id, updatedAt);
    }

    /// <summary>Uses the real authority/admission owner with only synthetic credentials and transport.</summary>
    private async Task<PersonalProviderCredentialService> SeedProviderAsync(string userId)
    {
        var credentials = CredentialTestFactory.Create(new EphemeralDataProtectionProvider());
        var profile = PersonalLocationProviderProfile.Create(userId, PersonalLocationProvider.Geoapify);
        credentials.Replace(profile, "synthetic");
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
        credentials.RecordVerification(profile, PersonalProviderCapability.Geocoding, PersonalProviderVerification.Verified);
        await using var db = fixture.CreateContext();
        db.Add(profile);
        db.Add(new PersonalLocationProviderSelection { UserId = userId, GeocodingProviderKey = "geoapify" });
        db.Add(new GeoapifyUsageGuard { UserId = userId });
        await db.SaveChangesAsync();
        return credentials;
    }

    /// <summary>Reads the complete stored tuple without tracker state.</summary>
    private static ResolvedFeatureTuple Metadata(Place place) => new(place.ResolvedFeatureName, place.ResolvedFeatureType,
        place.AddressEnrichmentProvider, place.AddressEnrichmentStorageMode, place.AddressEnrichedAt);

    /// <summary>Checks waypoint route reconciliation and preservation of manual duration.</summary>
    private static async Task AssertMovedRouteAsync(ApplicationDbContext db, Seed seed)
    {
        var stored = await db.Places.AsNoTracking().SingleAsync(place => place.Id == seed.PlaceId);
        var segment = await db.Segments.AsNoTracking().SingleAsync(item => item.Id == seed.SegmentId);
        Assert.Equal(new Coordinate(22, 14), stored.Location!.Coordinate);
        Assert.Equal(new Coordinate(22, 14), segment.RouteGeometry!.Coordinates[1]);
        Assert.InRange(segment.EstimatedDistanceKm!.Value, 0.01, 998);
        Assert.Equal(TimeSpan.FromMinutes(15), segment.EstimatedDuration);
        var waypoint = await db.Set<SegmentWaypoint>().SingleAsync(item => item.SegmentId == seed.SegmentId);
        Assert.Equal((0, 1), (waypoint.Position, waypoint.RouteVertexIndex));
    }

    /// <summary>Checks rollback of scalars, tuple, route, measurements, orders and Trip timestamp.</summary>
    private async Task AssertOriginalStoredAsync(Seed seed)
    {
        await using var db = fixture.CreateContext();
        var place = await db.Places.AsNoTracking().SingleAsync(item => item.Id == seed.PlaceId);
        Assert.Equal("Old address", place.Address);
        Assert.Equal("Waypoint", place.Name);
        Assert.Equal("Original notes", place.Notes);
        Assert.Equal(OriginalMetadata, Metadata(place));
        Assert.Equal(new Coordinate(21, 11), place.Location!.Coordinate);
        Assert.Equal(seed.RegionId, place.RegionId);
        Assert.Equal(new[] { seed.FromPlaceId, seed.PlaceId, seed.ToPlaceId }, await db.Places.Where(item => item.RegionId == seed.RegionId)
            .OrderBy(item => item.DisplayOrder).Select(item => item.Id).ToArrayAsync());
        var segment = await db.Segments.AsNoTracking().SingleAsync(item => item.Id == seed.SegmentId);
        Assert.Equal(new[] { new Coordinate(20, 10), new Coordinate(21, 11), new Coordinate(23, 12) }, segment.RouteGeometry!.Coordinates);
        Assert.Equal(999, segment.EstimatedDistanceKm);
        Assert.Equal(TimeSpan.FromMinutes(15), segment.EstimatedDuration);
        Assert.Equal(1, segment.DisplayOrder);
        var waypoint = await db.Set<SegmentWaypoint>().SingleAsync(item => item.SegmentId == seed.SegmentId);
        Assert.Equal((0, 1), (waypoint.Position, waypoint.RouteVertexIndex));
        Assert.Equal(seed.UpdatedAt, await db.Trips.Where(item => item.Id == seed.TripId).Select(item => item.UpdatedAt).SingleAsync());
    }

    /// <summary>Creates a fixture-owned Place with deterministic route order.</summary>
    private static Place Place(Region region, string name, double longitude, double latitude)
    {
        var place = new Place { Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = region.UserId,
            Name = name, Notes = "Original notes", DisplayOrder = region.Places.Count + 1,
            Location = new Point(longitude, latitude) { SRID = 4326 }, IconName = "marker", MarkerColor = "bg-blue" };
        region.Places.Add(place);
        return place;
    }

    /// <summary>Builds the unchanged public editor draft.</summary>
    private static string Body(Guid regionId, string address, double latitude, double longitude, bool reverseGeocode) =>
        JsonSerializer.Serialize(new { regionId, name = "Updated", notesHtml = "<p>Updated notes</p>", address,
            location = new { latitude, longitude }, iconName = "marker", markerColor = "bg-blue", reverseGeocode });

    /// <summary>Connects the actual provider gate to a local synthetic handler.</summary>
    private static ReverseGeocodingService Reverse(ApplicationDbContext db, PersonalProviderCredentialService credentials, HttpClient client) =>
        new(client, NullLogger<BaseApiController>.Instance, new PersonalProviderContactGate(db, credentials,
            new LegacyMapboxMigrationService(db, credentials), new ConfigurationBuilder().Build()), db);

    /// <summary>Identifies only data inside this fixture's disposable database.</summary>
    private sealed record Seed(string UserId, Guid TripId, Guid RegionId, Guid TargetRegionId, Guid PlaceId,
        Guid FromPlaceId, Guid ToPlaceId, Guid SegmentId, DateTime UpdatedAt);

    /// <summary>Counts real admitted transport calls and optionally cancels at provider handoff.</summary>
    private sealed class ProviderHandler(Action? onContact = null) : HttpMessageHandler
    {
        /// <summary>Gets the number of provider contacts.</summary>
        public int Requests { get; private set; }

        /// <summary>Returns normalized provider data without external HTTP.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            onContact?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                """{"type":"FeatureCollection","features":[{"properties":{"formatted":"Provider replacement","name":"New feature","result_type":"amenity"}}]}""") });
        }
    }

    /// <summary>Records lifecycle tuples and injects one failure after SQL but before the transaction commits.</summary>
    private sealed class PlaceSaveProbe(Exception? failure, Action? onPersistence = null) : SaveChangesInterceptor
    {
        private bool _placeSave;
        /// <summary>Gets the tuple proposed by each actual lifecycle save attempt.</summary>
        public List<ResolvedFeatureTuple> Attempts { get; } = [];

        /// <summary>Ignores admission saves and records only canonical Place mutations.</summary>
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var place = eventData.Context!.ChangeTracker.Entries<Place>().FirstOrDefault(entry => entry.State == EntityState.Modified);
            _placeSave = place != null;
            if (place != null) Attempts.Add(Metadata(place.Entity));
            return ValueTask.FromResult(result);
        }

        /// <summary>Exercises rollback after writes and EF acceptance rather than failing only before persistence.</summary>
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (_placeSave && Attempts.Count == 1 && failure != null)
            {
                _placeSave = false;
                onPersistence?.Invoke();
                throw failure;
            }
            return ValueTask.FromResult(result);
        }
    }
}
