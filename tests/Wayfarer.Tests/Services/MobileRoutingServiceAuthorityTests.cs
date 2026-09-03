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
        var discovery = new MobileRoutingProfileDiscoveryService(db, new(protection), new(protection),
            new PersonalProviderCredentialService(protection));
        var service = new MobileRoutingService(db, resolver, client, new AcceptingValidator(), new(), discovery);

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
        var mapping = new RoutingProviderProfileMapping
        {
            RoutingProviderConfigurationId = provider.Id, TransportProfileId = transport.Id, OsrmProfile = "walk"
        };
        provider.ProfileMappings.Add(mapping);
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
        var discovery = new MobileRoutingProfileDiscoveryService(db, new(protection), new(protection), credentials);
        var service = new MobileRoutingService(db, resolver, client, new AcceptingValidator(), new(), discovery);

        var unsupported = await service.RouteAsync("owner", transport.Id, [new(20, 10), new(21, 11)], "walking",
            null, default);
        var capability = await service.CapabilityAsync("owner", transport.Id, "walk", null, default);
        var stale = await service.RouteAsync("owner", transport.Id, [new(20, 10), new(21, 11)],
            "v1.pSJHONZRBMqqqYGEUcFHN0YNg3aoeWUOeE4rNUA351o", default);
        Assert.Equal(0, client.Requests);
        var route = await service.RouteAsync("owner", transport.Id, [new(20, 10), new(21, 11)], "walk",
            capability.SelectedProfileAuthorityIdentity, default);

        Assert.Equal("available", capability.Outcome);
        Assert.False(unsupported.Succeeded);
        Assert.Equal(1, client.Requests);
        Assert.NotNull(capability.SelectedProfileAuthorityIdentity);
        Assert.Equal("authority-changed", stale.Outcome);
        Assert.Equal(capability.SelectedProfileAuthorityIdentity, route.SelectedProfileAuthorityIdentity);
        Assert.Equal("geoapify", capability.Provider);
        Assert.Equal("persistent", capability.StorageMode);
        Assert.True(route.Succeeded);
        Assert.Equal("geoapify", route.Provider);
        Assert.Equal("persistent", route.StorageMode);
        Assert.Equal([new(20, 10), new(20.5, 10.5), new(21, 11)], route.Geometry);
        Assert.Equal(42.5, route.DistanceMetres);
        Assert.Equal(12.25, route.DurationSeconds);
        Assert.Equal(new RouteInstruction("Continue", "Straight", 0, 2, 42.5, 12.25),
            Assert.Single(route.Instructions!));
        Assert.Equal([new(20, 10), new(21, 11)], route.MatchPoints);
        Assert.NotNull(route.GeneratedAt);
        Assert.NotEqual(provider.Id, route.ProviderConfigurationId);
        Assert.Equal(transport.Id, route.TransportProfileId);
        Assert.EndsWith($":{transport.Id:N}", route.MappingIdentity);
        Assert.Equal("walk", route.ProviderMode);
        Assert.Equal(["Powered by Geoapify", "© OpenStreetMap contributors"],
            route.Attribution!.Select(item => item.Text));
        Assert.Equal(1, client.Requests);

        client.BeforeValidationAsync = async token =>
        {
            mapping.OsrmProfile = "bicycle";
            db.Update(mapping);
            await db.SaveChangesAsync(token);
        };
        var duringContact = await service.RouteAsync("owner", transport.Id, [new(20, 10), new(21, 11)], "walk",
            capability.SelectedProfileAuthorityIdentity, default);
        Assert.True(duringContact.Succeeded);
        Assert.Equal(2, client.Requests);

        client.BeforeValidationAsync = _ => Task.CompletedTask;
        var currentCapability = await service.CapabilityAsync("owner", transport.Id, "walk", null, default);
        service.BeforeRoutePublicationAsync = async token =>
        {
            provider.ConfigurationVersion++;
            provider.VerifiedConfigurationVersion = provider.ConfigurationVersion;
            db.Update(provider);
            await db.SaveChangesAsync(token);
        };
        var beforePublication = await service.RouteAsync("owner", transport.Id, [new(20, 10), new(21, 11)], "walk",
            currentCapability.SelectedProfileAuthorityIdentity, default);
        Assert.True(beforePublication.Succeeded);
        Assert.Equal(3, client.Requests);
    }

    [Fact]
    public async Task UnrelatedProfilePresentationDriftDoesNotInvalidateSelectedProfile()
    {
        var db = CreateDbContext();
        var profiles = db.Set<TransportProfile>().Take(2).ToArray();
        var provider = new RoutingProviderConfiguration
        {
            Id = Guid.NewGuid(), DisplayName = "Geoapify", AdapterType = RoutingAdapterType.Geoapify,
            Enabled = true, BaseEndpoint = "https://api.geoapify.com/", ConfigurationVersion = 2,
            VerifiedConfigurationVersion = 2
        };
        foreach (var profile in profiles)
            provider.ProfileMappings.Add(new RoutingProviderProfileMapping
            {
                RoutingProviderConfigurationId = provider.Id, TransportProfileId = profile.Id, OsrmProfile = "walk"
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
        var discovery = new MobileRoutingProfileDiscoveryService(db, new(protection), new(protection), credentials);
        var client = new RecordingClient();
        var service = new MobileRoutingService(db, resolver, client, new AcceptingValidator(), new(), discovery);
        var catalog = await discovery.DiscoverAsync("owner", default);

        profiles[1].Label = "Stale choice";
        db.Update(profiles[1]);
        await db.SaveChangesAsync();
        var staleCapability = await service.CapabilityAsync(
            "owner", profiles[0].Id, catalog.DiscoveryCatalogIdentity, default);
        Assert.Equal("catalog-changed", staleCapability.Outcome);
        Assert.Null(staleCapability.DiscoveryCatalogIdentity);
        Assert.Null(staleCapability.SelectedProfileAuthorityIdentity);

        catalog = await discovery.DiscoverAsync("owner", default);
        var capability = await service.CapabilityAsync(
            "owner", profiles[0].Id, catalog.DiscoveryCatalogIdentity, default);

        profiles[1].Label = "Changed";
        profiles[1].SortOrder--;
        db.Update(profiles[1]);
        await db.SaveChangesAsync();

        var route = await service.RouteAsync("owner", profiles[0].Id, [new(20, 10), new(21, 11)],
            capability.SelectedProfileAuthorityIdentity, default);

        Assert.Equal(catalog.DiscoveryCatalogIdentity, capability.DiscoveryCatalogIdentity);
        Assert.NotNull(capability.SelectedProfileAuthorityIdentity);
        Assert.True(route.Succeeded);
        Assert.Equal("available", route.Outcome);
        Assert.Equal(1, client.Requests);
    }

    private sealed class RecordingClient : IOsrmRouteClient
    {
        public int Requests { get; private set; }
        public Func<CancellationToken, Task> BeforeValidationAsync { get; set; } = _ => Task.CompletedTask;
        public async Task<OsrmRouteResult> RouteAsync(ResolvedRoutingProviderExecution execution,
            IReadOnlyList<RouteCoordinate> requestedAnchors, Func<CancellationToken, Task<bool>> validateAuthority,
            CancellationToken cancellationToken)
        {
            Requests++;
            await BeforeValidationAsync(cancellationToken);
            if (!await validateAuthority(cancellationToken)) return OsrmRouteResult.Invalid("configuration-changed");
            return new OsrmRouteResult(true,
                [requestedAnchors[0], new(20.5, 10.5), requestedAnchors[1]], requestedAnchors, null,
                42.5, 12.25, [new("Continue", "Straight", 0, 2, 42.5, 12.25)]);
        }
    }

    private sealed class AcceptingValidator : IProviderRouteGeometryValidator
    {
        public ProviderRouteValidationResult Validate(IReadOnlyList<RouteCoordinate> requestedAnchors,
            OsrmRouteResult providerRoute, CancellationToken cancellationToken) =>
            new(true, providerRoute.Geometry, [0, providerRoute.Geometry.Count - 1], null);
    }

    private sealed class RevalidatingClient(Func<CancellationToken, Task> beforeValidation) : IOsrmRouteClient
    {
        public int Requests { get; private set; }

        public async Task<OsrmRouteResult> RouteAsync(ResolvedRoutingProviderExecution execution,
            IReadOnlyList<RouteCoordinate> requestedAnchors, Func<CancellationToken, Task<bool>> validateAuthority,
            CancellationToken cancellationToken)
        {
            Requests++;
            await beforeValidation(cancellationToken);
            if (!await validateAuthority(cancellationToken)) return OsrmRouteResult.Invalid("configuration-changed");
            return new(true, [requestedAnchors[0], requestedAnchors[1]], requestedAnchors, null,
                42.5, 12.25, [new("Continue", "Straight", 0, 1, 42.5, 12.25)]);
        }
    }
}
