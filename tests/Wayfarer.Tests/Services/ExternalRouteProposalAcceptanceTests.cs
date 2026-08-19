using Microsoft.AspNetCore.DataProtection;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Services.ExternalRouting;
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

    private Fixture CreateFixture()
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
            Id = Guid.NewGuid(), DisplayName = "OSRM", Enabled = true,
            ConfigurationVersion = 3, VerifiedConfigurationVersion = 3
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
        var binding = new ExternalRouteProposalBinding(
            proposalId, tripId, segment.Id, userId, ExternalRouteProposalContextService.GeometryHash(geometry, indices),
            ExternalRouteAnchorFingerprint.Compute(places, geometry), profile.Id, provider.Id, 3, 2, aggregateToken);
        var token = contexts.Issue(binding).Token;
        db.ChangeTracker.Clear();
        return new Fixture(db, new ExternalRouteProposalAcceptanceService(db, aggregateTokens, contexts), time,
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
