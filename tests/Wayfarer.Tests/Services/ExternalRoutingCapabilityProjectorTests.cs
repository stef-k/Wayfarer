using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Verifies independent safe routing capability projection.</summary>
public sealed class ExternalRoutingCapabilityProjectorTests : TestBase
{
    [Fact]
    public async Task DisabledFeatureProjectsNoProviderDetails()
    {
        var fixture = CreateFixture(enabled: false);

        var capability = (await new ExternalRoutingCapabilityProjector(fixture.Db)
            .ProjectAsync([fixture.Segment], CancellationToken.None))[fixture.Segment.Id];

        Assert.False(capability.Available);
        Assert.Null(capability.ProviderDisplayName);
        Assert.Null(capability.Disclosure);
        Assert.DoesNotContain("routing.example", capability.UnavailableReason ?? string.Empty);
    }

    [Fact]
    public async Task SupportedSegmentProjectsOnlySafeUxFields()
    {
        var fixture = CreateFixture(enabled: true);

        var capability = (await new ExternalRoutingCapabilityProjector(fixture.Db)
            .ProjectAsync([fixture.Segment], CancellationToken.None))[fixture.Segment.Id];

        Assert.True(capability.Available);
        Assert.Equal("OSRM instance", capability.ProviderDisplayName);
        Assert.Equal("Coordinates leave Wayfarer.", capability.Disclosure);
        Assert.DoesNotContain("routing.example", string.Join('|', capability.ProviderDisplayName,
            capability.MappedProfileLabel, capability.Disclosure, capability.Attribution));
    }

    private Fixture CreateFixture(bool enabled)
    {
        var db = CreateDbContext();
        var profile = db.Set<TransportProfile>().First();
        var from = new Place { Id = Guid.NewGuid(), Location = Point(1, 2) };
        var to = new Place { Id = Guid.NewGuid(), Location = Point(3, 4) };
        var segment = new Segment
        {
            Id = Guid.NewGuid(), FromPlace = from, FromPlaceId = from.Id, ToPlace = to, ToPlaceId = to.Id,
            TransportProfileId = profile.Id
        };
        var provider = new RoutingProviderConfiguration
        {
            Id = Guid.NewGuid(), DisplayName = "OSRM instance", BaseEndpoint = "https://routing.example",
            Enabled = true, ConfigurationVersion = 2, VerifiedConfigurationVersion = 2,
            ExternalCoordinateDisclosure = "Coordinates leave Wayfarer.", Attribution = "Routing data attribution"
        };
        provider.ProfileMappings.Add(new RoutingProviderProfileMapping
        {
            RoutingProviderConfigurationId = provider.Id, TransportProfileId = profile.Id, OsrmProfile = "driving"
        });
        db.Set<Place>().AddRange(from, to);
        db.Set<Segment>().Add(segment);
        db.Set<RoutingProviderConfiguration>().Add(provider);
        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1, ExternalRouteGenerationEnabled = enabled, ActiveRoutingProviderConfigurationId = provider.Id
        });
        db.SaveChanges();
        return new Fixture(db, segment);
    }

    private static Point Point(double longitude, double latitude) => new(longitude, latitude) { SRID = 4326 };
    private sealed record Fixture(ApplicationDbContext Db, Segment Segment);
}
