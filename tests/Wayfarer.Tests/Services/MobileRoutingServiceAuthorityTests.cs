using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Exercises the real mobile service and resolver authority boundary.</summary>
public sealed class MobileRoutingServiceAuthorityTests : TestBase
{
    [Fact]
    public async Task ServerDefaultOsrmIsUnavailableWithoutContactOrGeoapifyMetadata()
    {
        var db = CreateDbContext();
        var profile = db.Set<TransportProfile>().First();
        var provider = new RoutingProviderConfiguration
        {
            Id = Guid.NewGuid(), DisplayName = "OSRM", AdapterType = RoutingAdapterType.OsrmCompatible,
            Enabled = true, BaseEndpoint = "https://routing.example", ConfigurationVersion = 1,
            VerifiedConfigurationVersion = 1, CredentialRequired = false
        };
        provider.ProfileMappings.Add(new RoutingProviderProfileMapping
        {
            RoutingProviderConfigurationId = provider.Id, TransportProfileId = profile.Id, OsrmProfile = "driving"
        });
        db.Set<RoutingProviderConfiguration>().Add(provider);
        db.Set<UserRoutingConfiguration>().Add(UserRoutingConfiguration.CreateServerDefault("owner"));
        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1, ExternalRouteGenerationEnabled = true, ActiveRoutingProviderConfigurationId = provider.Id
        });
        await db.SaveChangesAsync();
        var protection = new EphemeralDataProtectionProvider();
        var resolver = new AuthoritativeRoutingProviderResolver(db, new(protection), new(protection));
        var client = new RecordingClient();
        var service = new MobileRoutingService(db, resolver, client, new AcceptingValidator(), new());

        var capability = await service.CapabilityAsync("owner", profile.Id, default);
        var route = await service.RouteAsync("owner", profile.Id, [new(20, 10), new(21, 11)], default);

        Assert.Equal("no-provider-selected", capability.Outcome);
        Assert.Null(capability.Provider);
        Assert.Null(capability.StorageMode);
        Assert.False(route.Succeeded);
        Assert.Null(route.Provider);
        Assert.Null(route.StorageMode);
        Assert.Equal(0, client.Requests);
    }

    [Fact]
    public async Task CurrentPersonallySelectedGeoapifyAuthorityRemainsAvailableAndRoutable()
    {
        var db = CreateDbContext();
        var transport = db.Set<TransportProfile>().First();
        var provider = new RoutingProviderConfiguration
        {
            Id = Guid.NewGuid(), DisplayName = "Geoapify", AdapterType = RoutingAdapterType.Geoapify,
            Enabled = true, BaseEndpoint = "https://api.geoapify.com/", ConfigurationVersion = 2,
            VerifiedConfigurationVersion = 2
        };
        provider.ProfileMappings.Add(new RoutingProviderProfileMapping
        {
            RoutingProviderConfigurationId = provider.Id, TransportProfileId = transport.Id, OsrmProfile = "walk"
        });
        var protection = new EphemeralDataProtectionProvider();
        var credentials = new PersonalProviderCredentialService(protection);
        var personal = PersonalLocationProviderProfile.Create("owner", PersonalLocationProvider.Geoapify);
        credentials.Replace(personal, "secret");
        personal.RoutingAuthorized = true;
        personal.RoutingVerification = PersonalProviderVerification.Verified;
        personal.RoutingVerifiedCredentialGeneration = personal.CredentialGeneration;
        personal.RoutingVerifiedConfigurationGeneration = personal.RoutingGeneration;
        db.AddRange(provider, personal,
            new PersonalLocationProviderSelection { UserId = "owner", RoutingProviderKey = "geoapify" });
        db.ApplicationSettings.Add(new ApplicationSettings { Id = 1, ExternalRouteGenerationEnabled = true });
        await db.SaveChangesAsync();
        var resolver = new AuthoritativeRoutingProviderResolver(db, new(protection), new(protection), credentials);
        var client = new RecordingClient();
        var service = new MobileRoutingService(db, resolver, client, new AcceptingValidator(), new());

        var capability = await service.CapabilityAsync("owner", transport.Id, default);
        var route = await service.RouteAsync("owner", transport.Id, [new(20, 10), new(21, 11)], default);

        Assert.Equal("available", capability.Outcome);
        Assert.Equal("geoapify", capability.Provider);
        Assert.Equal("persistent", capability.StorageMode);
        Assert.True(route.Succeeded);
        Assert.Equal("geoapify", route.Provider);
        Assert.Equal("persistent", route.StorageMode);
        Assert.Equal(1, client.Requests);
    }

    private sealed class RecordingClient : IOsrmRouteClient
    {
        public int Requests { get; private set; }
        public Task<OsrmRouteResult> RouteAsync(ResolvedRoutingProviderExecution execution,
            IReadOnlyList<RouteCoordinate> requestedAnchors, Func<CancellationToken, Task<bool>> validateAuthority,
            CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new OsrmRouteResult(true, requestedAnchors, requestedAnchors, null));
        }
    }

    private sealed class AcceptingValidator : IProviderRouteGeometryValidator
    {
        public ProviderRouteValidationResult Validate(IReadOnlyList<RouteCoordinate> requestedAnchors,
            OsrmRouteResult providerRoute, CancellationToken cancellationToken) =>
            new(true, requestedAnchors, Enumerable.Range(0, requestedAnchors.Count).ToArray(), null);
    }
}
