using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Services.LocationProviders;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Locks Geoapify capability verification, shared credit, and provider-scoped mapping authority.</summary>
public sealed class GeoapifyStageOneContractTests
{
    [Fact]
    public async Task GeocodingAndRoutingVerification_AreSeparateAndShareOneCredentialAndPool()
    {
        await using var db = CreateDb(nameof(GeocodingAndRoutingVerification_AreSeparateAndShareOneCredentialAndPool));
        var credentials = new PersonalProviderCredentialService(new EphemeralDataProtectionProvider());
        var profile = PersonalLocationProviderProfile.Create("user", PersonalLocationProvider.Geoapify);
        credentials.Replace(profile, "secret-key");
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
        profile.SetAuthorization(PersonalProviderCapability.Routing, true);
        db.Add(profile);
        await db.SaveChangesAsync();
        var handler = new RecordingHandler(
            "{\"type\":\"FeatureCollection\",\"features\":[]}",
            ValidRouteJson);
        var service = CreateVerificationService(db, credentials, handler);

        var geocoding = await service.VerifyGeocodingAsync(profile.UserId);

        Assert.Equal(PersonalProviderVerification.Verified, geocoding);
        Assert.Equal(PersonalProviderVerification.Unverified, profile.RoutingVerification);
        Assert.Null((await db.Set<PersonalLocationProviderSelection>().SingleOrDefaultAsync())?.GeocodingProviderKey);

        var routing = await service.VerifyRoutingAsync(profile.UserId);

        Assert.Equal(PersonalProviderVerification.Verified, routing);
        Assert.Equal(PersonalProviderVerification.Verified, profile.GeocodingVerification);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, await db.GeoapifyUsageAdmissions.SumAsync(item => item.Credits));
        Assert.All(handler.Requests, request => Assert.DoesNotContain("secret-key", request.SafeDiagnostic, StringComparison.Ordinal));
        Assert.DoesNotContain("apiKey", service.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerificationFailure_IsCountedAndBoundToContactedCapabilityGeneration()
    {
        await using var db = CreateDb(nameof(VerificationFailure_IsCountedAndBoundToContactedCapabilityGeneration));
        var credentials = new PersonalProviderCredentialService(new EphemeralDataProtectionProvider());
        var profile = PersonalLocationProviderProfile.Create("user", PersonalLocationProvider.Geoapify);
        credentials.Replace(profile, "first-key");
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
        db.Add(profile);
        await db.SaveChangesAsync();
        var handler = new RecordingHandler("{\"type\":\"FeatureCollection\",\"features\":[]}")
        {
            BeforeResponse = () => credentials.Replace(profile, "replacement-key")
        };

        var result = await CreateVerificationService(db, credentials, handler).VerifyGeocodingAsync(profile.UserId);

        Assert.Equal(PersonalProviderVerification.Unavailable, result);
        Assert.Equal(PersonalProviderVerification.Unverified, profile.GeocodingVerification);
        Assert.Equal(1, await db.GeoapifyUsageAdmissions.SumAsync(item => item.Credits));
    }

    [Theory]
    [InlineData(GeoapifyRoutingMode.Walk, 2, 1)]
    [InlineData(GeoapifyRoutingMode.Bicycle, 3, 2)]
    [InlineData(GeoapifyRoutingMode.Motorcycle, 3, 42)]
    [InlineData(GeoapifyRoutingMode.Drive, 25, 504)]
    [InlineData(GeoapifyRoutingMode.Bus, 2, 21)]
    public void RouteCost_IsConservativeCheckedAndPairBased(GeoapifyRoutingMode mode, int waypointCount, int expected)
    {
        Assert.Equal(expected, GeoapifyRouteCost.Calculate(mode, waypointCount));
    }

    [Fact]
    public void RouteCost_RejectsUnsupportedBoundsAndOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GeoapifyRouteCost.Calculate((GeoapifyRoutingMode)999, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => GeoapifyRouteCost.Calculate(GeoapifyRoutingMode.Walk, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GeoapifyRouteCost.Calculate(GeoapifyRoutingMode.Walk, 26));
        Assert.Throws<OverflowException>(() => GeoapifyRouteCost.CalculatePairs(GeoapifyRoutingMode.Drive, int.MaxValue));
    }

    [Theory]
    [InlineData("WALK")]
    [InlineData("walk")]
    [InlineData("Walking")]
    [InlineData("Περπάτημα")]
    public void DisplayNameNeverCreatesAProviderMapping(string displayName)
    {
        var profile = new TransportProfile { Id = Guid.NewGuid(), Label = displayName };
        var configuration = new RoutingProviderConfiguration { Id = Guid.NewGuid(), AdapterType = RoutingAdapterType.Geoapify };

        var resolution = ProviderTransportProfileResolver.Resolve(configuration, profile);

        Assert.Equal(ProviderTransportProfileCategory.Unmapped, resolution.Category);
    }

    [Fact]
    public void ExplicitMappingSurvivesRenameAndIsIndependentPerProvider()
    {
        var profile = new TransportProfile { Id = Guid.NewGuid(), Label = "Family car" };
        var geoapify = Configuration(RoutingAdapterType.Geoapify, profile.Id, "drive");
        var mapbox = Configuration(RoutingAdapterType.MapboxDirections, profile.Id, "mapbox/driving-traffic");

        Assert.Equal("drive", ProviderTransportProfileResolver.Resolve(geoapify, profile).NativeMode);
        Assert.Equal("mapbox/driving-traffic", ProviderTransportProfileResolver.Resolve(mapbox, profile).NativeMode);

        profile.Label = "Voiture familiale";

        Assert.Equal("drive", ProviderTransportProfileResolver.Resolve(geoapify, profile).NativeMode);
        Assert.Equal("mapbox/driving-traffic", ProviderTransportProfileResolver.Resolve(mapbox, profile).NativeMode);
    }

    [Fact]
    public void MissingAndUnsupportedMappingsAreRejectedBeforeCreditOrHttp()
    {
        var profile = new TransportProfile { Id = Guid.NewGuid(), Label = "Custom" };
        var configuration = Configuration(RoutingAdapterType.Geoapify, profile.Id, "hovercraft");
        var ledger = new PersonalProviderUsageLedger();
        var handler = new RecordingHandler(ValidRouteJson);

        var unsupported = ProviderTransportProfileResolver.Resolve(configuration, profile);
        configuration.ProfileMappings.Clear();
        var unmapped = ProviderTransportProfileResolver.Resolve(configuration, profile);

        Assert.Equal(ProviderTransportProfileCategory.Unsupported, unsupported.Category);
        Assert.Equal(ProviderTransportProfileCategory.Unmapped, unmapped.Category);
        Assert.Equal(0, handler.Requests.Count);
        Assert.True(ledger.TryAdmitGeoapify(DateTimeOffset.UtcNow, 1, 1, PersonalProviderProduct.Routing));
    }

    [Fact]
    public void MappingVersionParticipatesInStableAuthority()
    {
        var profileId = Guid.NewGuid();
        var configuration = Configuration(RoutingAdapterType.Geoapify, profileId, "walk");
        var first = ProviderTransportProfileResolver.Resolve(configuration, new TransportProfile { Id = profileId, Label = "A" });

        configuration.ProfileMappings.Single().SetNativeMode("bicycle");
        configuration.MarkConfigurationChanged();
        var second = ProviderTransportProfileResolver.Resolve(configuration, new TransportProfile { Id = profileId, Label = "A" });

        Assert.NotEqual(first.Authority, second.Authority);
        Assert.Equal("bicycle", second.NativeMode);
    }

    private static GeoapifyVerificationService CreateVerificationService(ApplicationDbContext db,
        PersonalProviderCredentialService credentials, HttpMessageHandler handler)
    {
        var gate = new PersonalProviderContactGate(db, credentials,
            new LegacyMapboxMigrationService(db, credentials), new ConfigurationBuilder().Build());
        return new GeoapifyVerificationService(new HttpClient(handler), gate, db);
    }

    private static RoutingProviderConfiguration Configuration(RoutingAdapterType adapter, Guid profileId, string nativeMode)
    {
        var configuration = new RoutingProviderConfiguration { Id = Guid.NewGuid(), AdapterType = adapter };
        configuration.ProfileMappings.Add(new RoutingProviderProfileMapping
        {
            RoutingProviderConfigurationId = configuration.Id,
            TransportProfileId = profileId,
            ProviderNativeMode = nativeMode
        });
        return configuration;
    }

    private static ApplicationDbContext CreateDb(string name)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(name).Options;
        return new ApplicationDbContext(options, new ServiceCollection().BuildServiceProvider());
    }

    private const string ValidRouteJson = """
        {"results":[{"distance":1111,"time":900,"geometry":{"type":"LineString","coordinates":[[0,0],[0.01,0]]},
        "legs":[{"distance":1111,"time":900,"steps":[{"instruction":{"text":"Walk east","type":"Straight"},"from_index":0,"to_index":1,"distance":1111,"time":900}]}]}]}
        """;

    private sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
    {
        private int index;
        public Action? BeforeResponse { get; init; }
        public List<(string Method, string Host, string Path, string SafeDiagnostic)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.Host, request.RequestUri.AbsolutePath,
                $"{request.Method.Method} {request.RequestUri.Host}{request.RequestUri.AbsolutePath}"));
            BeforeResponse?.Invoke();
            var response = responses[Math.Min(index++, responses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) });
        }
    }
}
