using Microsoft.AspNetCore.DataProtection;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Verifies server-authoritative generation and its no-mutation boundary.</summary>
public sealed class ExternalRouteProposalGeneratorTests : TestBase
{
    [Fact]
    public async Task Generate_ReloadsOwnedSegmentAndReturnsImmutableProposalWithoutMutation()
    {
        var db = CreateDbContext();
        var dataProtection = new EphemeralDataProtectionProvider();
        var fixture = AddFixture(db, enabled: true);
        var aggregateTokens = new SegmentAggregateTokenService(dataProtection);
        var token = aggregateTokens.Issue(fixture.UserId, fixture.TripId, fixture.Segment.Id, fixture.Segment.RowVersion);
        var client = new StubClient(fixture.Anchors);
        var validator = new StubValidator(fixture.Anchors);
        var generator = new ExternalRouteProposalGenerator(db, aggregateTokens, client, validator,
            new ExternalRouteProposalContextService(dataProtection), new RoutingRequestBudget());

        var result = await generator.GenerateAsync(fixture.UserId, fixture.TripId, fixture.Segment.Id, token, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Proposal);
        Assert.Equal(fixture.Segment.Id, result.Proposal.SegmentId);
        Assert.Null(fixture.Segment.RouteGeometry);
        Assert.All(fixture.Segment.Waypoints, waypoint => Assert.Null(waypoint.RouteVertexIndex));
        Assert.Equal(1, client.Requests);
    }

    [Fact]
    public async Task Generate_DisabledFeatureRejectsBeforeProviderContact()
    {
        var db = CreateDbContext();
        var dataProtection = new EphemeralDataProtectionProvider();
        var fixture = AddFixture(db, enabled: false);
        var aggregateTokens = new SegmentAggregateTokenService(dataProtection);
        var client = new StubClient(fixture.Anchors);
        var generator = new ExternalRouteProposalGenerator(db, aggregateTokens, client, new StubValidator(fixture.Anchors),
            new ExternalRouteProposalContextService(dataProtection), new RoutingRequestBudget());

        var result = await generator.GenerateAsync(fixture.UserId, fixture.TripId, fixture.Segment.Id,
            aggregateTokens.Issue(fixture.UserId, fixture.TripId, fixture.Segment.Id, fixture.Segment.RowVersion), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("external-routing-disabled", result.ErrorCode);
        Assert.Equal(0, client.Requests);
    }

    [Fact]
    public async Task Generate_RejectsStaleAggregateTokenBeforeProviderContact()
    {
        var db = CreateDbContext();
        var dataProtection = new EphemeralDataProtectionProvider();
        var fixture = AddFixture(db, enabled: true);
        var aggregateTokens = new SegmentAggregateTokenService(dataProtection);
        var client = new StubClient(fixture.Anchors);
        var generator = new ExternalRouteProposalGenerator(db, aggregateTokens, client, new StubValidator(fixture.Anchors),
            new ExternalRouteProposalContextService(dataProtection), new RoutingRequestBudget());

        var result = await generator.GenerateAsync(fixture.UserId, fixture.TripId, fixture.Segment.Id,
            aggregateTokens.Issue(fixture.UserId, fixture.TripId, fixture.Segment.Id, fixture.Segment.RowVersion + 1), CancellationToken.None);

        Assert.Equal("segment-aggregate-stale", result.ErrorCode);
        Assert.Equal(0, client.Requests);
    }

    private static Fixture AddFixture(ApplicationDbContext db, bool enabled)
    {
        const string userId = "owner";
        var tripId = Guid.NewGuid();
        var profile = db.Set<TransportProfile>().First();
        var from = new Place { Id = Guid.NewGuid(), UserId = userId, Name = "From", Location = Point(23.7, 37.9) };
        var via = new Place { Id = Guid.NewGuid(), UserId = userId, Name = "Via", Location = Point(23.75, 37.95) };
        var to = new Place { Id = Guid.NewGuid(), UserId = userId, Name = "To", Location = Point(23.8, 38.0) };
        var segment = new Segment
        {
            Id = Guid.NewGuid(), TripId = tripId, UserId = userId, FromPlace = from, FromPlaceId = from.Id,
            ToPlace = to, ToPlaceId = to.Id, TransportProfileId = profile.Id, Mode = profile.Key
        };
        segment.Waypoints.Add(new SegmentWaypoint { Segment = segment, SegmentId = segment.Id, Place = via, PlaceId = via.Id, Position = 0 });
        var provider = new RoutingProviderConfiguration
        {
            Id = Guid.NewGuid(), DisplayName = "OSRM", Enabled = true, BaseEndpoint = "https://routing.example",
            ConfigurationVersion = 1, VerifiedConfigurationVersion = 1
        };
        provider.ProfileMappings.Add(new RoutingProviderProfileMapping
        {
            RoutingProviderConfigurationId = provider.Id, TransportProfileId = profile.Id, OsrmProfile = "driving"
        });
        db.Set<Place>().AddRange(from, via, to);
        db.Set<Segment>().Add(segment);
        db.Set<RoutingProviderConfiguration>().Add(provider);
        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1, ExternalRouteGenerationEnabled = enabled, ActiveRoutingProviderConfigurationId = provider.Id
        });
        db.SaveChanges();
        return new Fixture(userId, tripId, segment, [new(23.7, 37.9), new(23.75, 37.95), new(23.8, 38.0)]);
    }

    private static Point Point(double longitude, double latitude) => new(longitude, latitude) { SRID = 4326 };

    private sealed record Fixture(string UserId, Guid TripId, Segment Segment, IReadOnlyList<RouteCoordinate> Anchors);

    private sealed class StubClient(IReadOnlyList<RouteCoordinate> anchors) : IOsrmRouteClient
    {
        public int Requests { get; private set; }
        public Task<OsrmRouteResult> RouteAsync(RoutingProviderConfiguration provider, string profile,
            IReadOnlyList<RouteCoordinate> requestedAnchors, RoutingBudgetLease budget, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new OsrmRouteResult(true, anchors, anchors, null));
        }
    }

    private sealed class StubValidator(IReadOnlyList<RouteCoordinate> anchors) : IProviderRouteGeometryValidator
    {
        public ProviderRouteValidationResult Validate(IReadOnlyList<RouteCoordinate> requestedAnchors,
            OsrmRouteResult providerRoute, CancellationToken cancellationToken) =>
            new(true, anchors, Enumerable.Range(0, anchors.Count).ToArray(), null);
    }
}
