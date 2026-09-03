using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using System.Text.Json;
using Wayfarer.Areas.Admin.Models;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Verifies proposal alteration, expiry, staleness, and no-persistence acceptance.</summary>
public sealed class ExternalRouteProposalAcceptanceTests : TestBase
{
    [Fact]
    public async Task Accept_ValidCurrentProposalReturnsDraftValueWithoutPersistence()
    {
        var fixture = CreateFixture();

        var result = await fixture.Service.AcceptAsync(fixture.UserId, fixture.TripId, fixture.Segment.Id,
            fixture.ProposalId, fixture.Geometry, fixture.Indices, fixture.Token, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(fixture.Geometry, result.Proposal!.Geometry);
        Assert.Null(fixture.Segment.RouteGeometry);
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task Accept_ValidGeoapifyProposalPersistsInstructionsAndPlanningProvenance()
    {
        RouteInstruction[] instructions = [new("Turn right", "right", 0, 1, 120, 30)];
        var fixture = CreateFixture(geoapifyInstructions: instructions);

        var result = await fixture.Service.AcceptAsync(fixture.UserId, fixture.TripId, fixture.Segment.Id,
            fixture.ProposalId, fixture.Geometry, fixture.Indices, fixture.Token, CancellationToken.None);

        Assert.True(result.Succeeded);
        var segment = await fixture.Db.Set<Segment>().SingleAsync(item => item.Id == fixture.Segment.Id);
        Assert.Equal(instructions, JsonSerializer.Deserialize<RouteInstruction[]>(segment.RouteInstructionsJson!));
        Assert.Equal(fixture.Segment.TransportProfileId, segment.RouteTransportProfileId);
        Assert.Equal("geoapify", segment.RouteProvider);
        Assert.Equal("walk", segment.RouteMappingMode);
        Assert.Equal("persistent", segment.RouteStorageMode);
    }

    [Fact]
    public async Task Accept_ValidGeoapifyProposalPreservesEmptyInstructionList()
    {
        var fixture = CreateFixture(geoapifyInstructions: []);

        var result = await fixture.Service.AcceptAsync(fixture.UserId, fixture.TripId, fixture.Segment.Id,
            fixture.ProposalId, fixture.Geometry, fixture.Indices, fixture.Token, CancellationToken.None);

        Assert.True(result.Succeeded);
        var segment = await fixture.Db.Set<Segment>().SingleAsync(item => item.Id == fixture.Segment.Id);
        Assert.Empty(JsonSerializer.Deserialize<RouteInstruction[]>(segment.RouteInstructionsJson!)!);
    }

    [Fact]
    public async Task Accept_RejectsAlteredGeometryHash()
    {
        var fixture = CreateFixture();
        var altered = fixture.Geometry.ToArray();
        altered[1] = new RouteCoordinate(25, 39);

        var result = await fixture.Service.AcceptAsync(fixture.UserId, fixture.TripId, fixture.Segment.Id,
            fixture.ProposalId, altered, fixture.Indices, fixture.Token, CancellationToken.None);

        Assert.Equal("route-proposal-altered", result.ErrorCode);
        Assert.Null(fixture.Segment.RouteGeometry);
    }

    [Fact]
    public async Task Accept_RejectsExpiredProtectedContext()
    {
        var fixture = CreateFixture();
        fixture.Time.Advance(TimeSpan.FromMinutes(11));

        var result = await fixture.Service.AcceptAsync(fixture.UserId, fixture.TripId, fixture.Segment.Id,
            fixture.ProposalId, fixture.Geometry, fixture.Indices, fixture.Token, CancellationToken.None);

        Assert.Equal("route-proposal-invalid-or-expired", result.ErrorCode);
    }

    [Fact]
    public async Task Accept_RejectsProviderSwitchWithoutContactingProvider()
    {
        var fixture = CreateFixture();
        fixture.Db.ApplicationSettings.Single().ActiveRoutingProviderConfigurationId = Guid.NewGuid();
        fixture.Db.SaveChanges();

        var result = await fixture.Service.AcceptAsync(fixture.UserId, fixture.TripId, fixture.Segment.Id,
            fixture.ProposalId, fixture.Geometry, fixture.Indices, fixture.Token, CancellationToken.None);

        Assert.Equal("route-proposal-stale", result.ErrorCode);
        Assert.Null(fixture.Segment.RouteGeometry);
    }

    [Fact]
    public async Task Accept_RejectsInactiveBoundTransportProfile()
    {
        var fixture = CreateFixture();
        fixture.Db.Set<TransportProfile>().Single(item => item.Id == fixture.Segment.TransportProfileId).IsActive = false;
        fixture.Db.SaveChanges();

        var result = await fixture.Service.AcceptAsync(fixture.UserId, fixture.TripId, fixture.Segment.Id,
            fixture.ProposalId, fixture.Geometry, fixture.Indices, fixture.Token, CancellationToken.None);

        Assert.Equal("route-proposal-stale", result.ErrorCode);
    }

    [Fact]
    public async Task Accept_RejectsRemovedActiveProviderMapping()
    {
        var fixture = CreateFixture();
        fixture.Db.RemoveRange(fixture.Db.Set<RoutingProviderProfileMapping>());
        fixture.Db.SaveChanges();

        var result = await fixture.Service.AcceptAsync(fixture.UserId, fixture.TripId, fixture.Segment.Id,
            fixture.ProposalId, fixture.Geometry, fixture.Indices, fixture.Token, CancellationToken.None);

        Assert.Equal("route-proposal-stale", result.ErrorCode);
    }

    [Fact]
    public async Task Accept_RejectsSelectionModeAndUserConfigurationVersionChange()
    {
        var fixture = CreateFixture();
        var configuration = fixture.Db.Set<UserRoutingConfiguration>().Single();
        configuration.SelectPersonalProvider(fixture.Db.Set<RoutingProviderConfiguration>().Single().Id);
        fixture.Db.SaveChanges();

        var result = await fixture.Service.AcceptAsync(fixture.UserId, fixture.TripId, fixture.Segment.Id,
            fixture.ProposalId, fixture.Geometry, fixture.Indices, fixture.Token, CancellationToken.None);

        Assert.Equal("route-proposal-stale", result.ErrorCode);
    }

    [Fact]
    public async Task Accept_RejectsPersonalProposalAfterProviderDisableAndReenable()
    {
        var fixture = CreateFixture(personal: true);
        var provider = fixture.Db.Set<RoutingProviderConfiguration>().Single();
        var administration = new RoutingProviderAdministrationService(fixture.Db,
            new RoutingProviderCredentialService(new EphemeralDataProtectionProvider()),
            new RoutingProviderPacer(TimeProvider.System));
        var disable = ProviderModel(provider);
        disable.Enabled = false;
        Assert.True((await administration.SaveAsync(disable, "admin", CancellationToken.None)).Succeeded);
        var reenable = ProviderModel(provider);
        reenable.Enabled = true;
        Assert.True((await administration.SaveAsync(reenable, "admin", CancellationToken.None)).Succeeded);

        var result = await fixture.Service.AcceptAsync(fixture.UserId, fixture.TripId, fixture.Segment.Id,
            fixture.ProposalId, fixture.Geometry, fixture.Indices, fixture.Token, CancellationToken.None);

        Assert.Equal("route-proposal-stale", result.ErrorCode);
    }

    private static RoutingProviderEditViewModel ProviderModel(RoutingProviderConfiguration provider) => new()
    {
        Id = provider.Id, DisplayName = provider.DisplayName, BaseEndpoint = provider.BaseEndpoint!,
        CredentialRequired = provider.CredentialRequired, CredentialPresent = provider.CredentialPresent,
        PersonalRoutingAccess = provider.PersonalRoutingAccess, Enabled = provider.Enabled,
        Attribution = provider.Attribution, ExternalCoordinateDisclosure = provider.ExternalCoordinateDisclosure!,
        VerificationFromLongitude = provider.VerificationFromLongitude,
        VerificationFromLatitude = provider.VerificationFromLatitude,
        VerificationToLongitude = provider.VerificationToLongitude,
        VerificationToLatitude = provider.VerificationToLatitude,
        GenerationTimeoutSeconds = provider.GenerationTimeoutSeconds,
        ResponseSizeLimitBytes = provider.ResponseSizeLimitBytes,
        RequestsPerMinute = provider.RequestsPerMinute, MaxConcurrency = provider.MaxConcurrency,
        MinimumIntervalSeconds = RoutingMinimumIntervalConverter.Format(provider.MinimumIntervalMilliseconds),
        RowVersion = provider.RowVersion, ConfigurationVersion = provider.ConfigurationVersion,
        Mappings = provider.ProfileMappings.Select(item => new RoutingProviderMappingViewModel
            { TransportProfileId = item.TransportProfileId, OsrmProfile = item.OsrmProfile }).ToList()
    };

    private Fixture CreateFixture(bool personal = false, IReadOnlyList<RouteInstruction>? geoapifyInstructions = null)
    {
        const string userId = "owner";
        var db = CreateDbContext();
        var dataProtection = new EphemeralDataProtectionProvider();
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-18T12:00:00Z"));
        var contexts = new ExternalRouteProposalContextService(dataProtection, time);
        var aggregateTokens = new SegmentAggregateTokenService(dataProtection);
        var profile = db.Set<TransportProfile>().First();
        var tripId = Guid.NewGuid();
        var from = new Place { Id = Guid.NewGuid(), UserId = userId, Location = Point(23.7, 37.9) };
        var via = new Place { Id = Guid.NewGuid(), UserId = userId, Location = Point(23.75, 37.95) };
        var to = new Place { Id = Guid.NewGuid(), UserId = userId, Location = Point(23.8, 38.0) };
        var segment = new Segment
        {
            Id = Guid.NewGuid(), TripId = tripId, UserId = userId, FromPlace = from, FromPlaceId = from.Id,
            ToPlace = to, ToPlaceId = to.Id, TransportProfileId = profile.Id, Mode = profile.Key
        };
        segment.Waypoints.Add(new SegmentWaypoint { Segment = segment, SegmentId = segment.Id, Place = via, PlaceId = via.Id });
        var provider = new RoutingProviderConfiguration
        {
            Id = Guid.NewGuid(), DisplayName = "OSRM", Enabled = true, BaseEndpoint = "https://routing.example",
            ConfigurationVersion = 3, VerifiedConfigurationVersion = 3,
            PersonalRoutingAccess = personal ? PersonalRoutingAccess.CredentialFree : PersonalRoutingAccess.Disabled,
            Attribution = "Attribution", ExternalCoordinateDisclosure = "Coordinates leave Wayfarer."
        };
        provider.ProfileMappings.Add(new RoutingProviderProfileMapping
        {
            RoutingProviderConfigurationId = provider.Id,
            TransportProfileId = profile.Id,
            OsrmProfile = "walking"
        });
        db.Set<Place>().AddRange(from, via, to);
        db.Set<Segment>().Add(segment);
        db.Set<RoutingProviderConfiguration>().Add(provider);
        var userConfiguration = UserRoutingConfiguration.CreateServerDefault(userId);
        if (personal) userConfiguration.SelectPersonalProvider(provider.Id);
        db.Set<UserRoutingConfiguration>().Add(userConfiguration);
        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1, ExternalRouteGenerationEnabled = true, ExternalRouteGenerationVersion = 2,
            ActiveRoutingProviderConfigurationId = provider.Id
        });
        db.SaveChanges();
        RouteCoordinate[] geometry = [new(23.7, 37.9), new(23.75, 37.95), new(23.8, 38.0)];
        int[] indices = [0, 1, 2];
        var proposalId = Guid.NewGuid();
        var aggregateToken = aggregateTokens.Issue(userId, tripId, segment.Id, segment.RowVersion);
        var places = new Place?[] { from, via, to };
        PersonalProviderCredentialService? personalCredentials = null;
        PersonalLocationProviderProfile? personalProfile = null;
        PersonalLocationProviderSelection? personalSelection = null;
        if (geoapifyInstructions != null)
        {
            personalCredentials = new PersonalProviderCredentialService(dataProtection);
            personalProfile = PersonalLocationProviderProfile.Create(userId, PersonalLocationProvider.Geoapify);
            personalCredentials.Replace(personalProfile, "geoapify-secret");
            personalProfile.SetAuthorization(PersonalProviderCapability.Routing, true);
            personalCredentials.RecordVerification(personalProfile, PersonalProviderCapability.Routing,
                PersonalProviderVerification.Verified);
            personalSelection = PersonalLocationProviderSelection.Create(userId);
            personalSelection.Select(PersonalProviderCapability.Routing, PersonalLocationProvider.Geoapify);
            db.AddRange(personalProfile, personalSelection);
            db.SaveChanges();
        }
        var binding = new ExternalRouteProposalBinding(
            proposalId, tripId, segment.Id, userId, ExternalRouteProposalContextService.GeometryHash(geometry, indices),
            ExternalRouteAnchorFingerprint.Compute(places, geometry), profile.Id, provider.Id, 3, 2, aggregateToken,
            personal ? RoutingProviderSelectionMode.Personal : RoutingProviderSelectionMode.ServerDefault,
            userConfiguration.ConfigurationVersion);
        if (geoapifyInstructions != null)
            binding = binding with
            {
                ProviderId = Guid.Parse("5bde15a4-984c-4daa-912d-9fa59a166ec3"),
                ProviderConfigurationVersion = 1,
                FeatureStateGeneration = ProviderDirectionsCatalog.AuthorityVersion,
                ProviderSelectionMode = RoutingProviderSelectionMode.Personal,
                UserRoutingConfigurationVersion = personalProfile!.RoutingGeneration,
                Instructions = geoapifyInstructions,
                ProviderKey = "geoapify",
                MappingMode = "walk",
                Attribution = "Powered by Geoapify|© OpenStreetMap contributors",
                StorageMode = "persistent"
            };
        var token = contexts.Issue(binding).Token;
        db.ChangeTracker.Clear();
        var resolver = new AuthoritativeRoutingProviderResolver(db,
            new RoutingProviderCredentialService(dataProtection), new UserRoutingCredentialService(dataProtection),
            personalCredentials);
        return new Fixture(db, new ExternalRouteProposalAcceptanceService(db, aggregateTokens, contexts, resolver), time,
            userId, tripId, segment, proposalId, geometry, indices, token);
    }

    private static Point Point(double longitude, double latitude) => new(longitude, latitude) { SRID = 4326 };

    private sealed record Fixture(
        ApplicationDbContext Db, ExternalRouteProposalAcceptanceService Service, MutableTimeProvider Time,
        string UserId, Guid TripId, Segment Segment, Guid ProposalId, IReadOnlyList<RouteCoordinate> Geometry,
        IReadOnlyList<int> Indices, string Token);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
