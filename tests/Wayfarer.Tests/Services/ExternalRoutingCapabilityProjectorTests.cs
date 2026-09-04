using NetTopologySuite.Geometries;
using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Verifies independent safe routing capability projection.</summary>
public sealed class ExternalRoutingCapabilityProjectorTests : TestBase
{
    [Fact]
    public async Task DisabledLegacyFeatureDoesNotVetoPersonalProviderModes()
    {
        var fixture = CreateFixture();

        var capability = (await CreateProjector(fixture)
            .ProjectAsync(fixture.UserId, [fixture.Segment], CancellationToken.None))[fixture.Segment.Id];

        Assert.True(capability.Available);
        Assert.Equal("Geoapify", capability.ProviderDisplayName);
        Assert.Equal(5, capability.Modes.Count);
    }

    [Fact]
    public async Task SupportedSegmentProjectsOnlySafeUxFields()
    {
        var fixture = CreateFixture();

        var capability = (await CreateProjector(fixture)
            .ProjectAsync(fixture.UserId, [fixture.Segment], CancellationToken.None))[fixture.Segment.Id];

        Assert.True(capability.Available);
        Assert.Equal("Geoapify", capability.ProviderDisplayName);
        Assert.Equal(["walk", "bicycle", "motorcycle", "drive", "bus"],
            capability.Modes.Select(item => item.Key));
        Assert.Null(capability.MappedProfileLabel);
        Assert.DoesNotContain("routing.example", string.Join('|', capability.ProviderDisplayName,
            capability.MappedProfileLabel, capability.Disclosure, capability.Attribution));
    }

    private Fixture CreateFixture()
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
        db.Set<Place>().AddRange(from, to);
        db.Set<Segment>().Add(segment);
        var protection = new EphemeralDataProtectionProvider();
        var personalCredentials = new PersonalProviderCredentialService(protection);
        var personal = PersonalLocationProviderProfile.Create("owner", PersonalLocationProvider.Geoapify);
        personalCredentials.Replace(personal, "personal-key");
        personal.RoutingAuthorized = true;
        personal.RoutingVerification = PersonalProviderVerification.Verified;
        personal.RoutingVerifiedCredentialGeneration = personal.CredentialGeneration;
        personal.RoutingVerifiedConfigurationGeneration = personal.RoutingGeneration;
        db.AddRange(personal, new PersonalLocationProviderSelection
        { UserId = "owner", RoutingProviderKey = "geoapify" });
        db.SaveChanges();
        return new Fixture(db, segment, "owner", protection);
    }

    private static ExternalRoutingCapabilityProjector CreateProjector(Fixture fixture)
    {
        var resolver = new AuthoritativeRoutingProviderResolver(fixture.Db,
            new PersonalProviderCredentialService(fixture.Protection));
        return new ExternalRoutingCapabilityProjector(resolver);
    }

    private static Point Point(double longitude, double latitude) => new(longitude, latitude) { SRID = 4326 };
    private sealed record Fixture(ApplicationDbContext Db, Segment Segment, string UserId,
        IDataProtectionProvider Protection);
}
