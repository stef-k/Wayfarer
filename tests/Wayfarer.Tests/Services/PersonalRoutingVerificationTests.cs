using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Wayfarer.Models;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves personal verification uses bounded shared provider execution authority.</summary>
public sealed class PersonalRoutingVerificationTests : TestBase
{
    [Fact]
    public async Task VerifyProbesEveryMappedProfileUsingOnlyThePersonalCredential()
    {
        const string userId = "owner";
        var db = CreateDbContext();
        var profiles = db.Set<TransportProfile>().Take(2).ToArray();
        var protection = new EphemeralDataProtectionProvider();
        var credentials = new UserRoutingCredentialService(protection);
        var provider = Provider(profiles);
        var configuration = UserRoutingConfiguration.CreateServerDefault(userId);
        configuration.SelectPersonalProvider(provider.Id);
        credentials.Replace(configuration, provider.Id, "personal-secret");
        db.Set<RoutingProviderConfiguration>().Add(provider);
        db.Set<UserRoutingConfiguration>().Add(configuration);
        db.ApplicationSettings.Add(new ApplicationSettings { Id = 1, ExternalRouteGenerationEnabled = true });
        db.SaveChanges();
        var transport = new ProbeTransport();
        var executor = new RoutingBoundedExecutor(new PublicResolver(),
            new RoutingEndpointPolicy(Options.Create(new RoutingOutboundOptions())), transport);
        var service = new PersonalRoutingVerificationService(db, executor, credentials,
            new RoutingAttemptCoordinator(new RoutingProviderPacer(TimeProvider.System), new RoutingRequestBudget()));

        var result = await service.VerifyAsync(userId, configuration.RowVersion, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, transport.Credentials.Count);
        Assert.All(transport.Credentials, value => Assert.Equal("personal-secret", value));
        Assert.Equal(configuration.ConfigurationVersion, configuration.VerifiedUserConfigurationVersion);
        Assert.Equal(provider.ConfigurationVersion, configuration.VerifiedProviderConfigurationVersion);
    }

    private static RoutingProviderConfiguration Provider(IReadOnlyList<TransportProfile> profiles)
    {
        var provider = new RoutingProviderConfiguration
        {
            Id = Guid.NewGuid(), DisplayName = "Approved", Enabled = true,
            BaseEndpoint = "https://routing.example", PersonalRoutingAccess = PersonalRoutingAccess.CredentialRequired,
            ConfigurationVersion = 3, VerifiedConfigurationVersion = 3,
            Attribution = "Attribution", ExternalCoordinateDisclosure = "Coordinates leave Wayfarer.",
            VerificationFromLongitude = 23.7, VerificationFromLatitude = 37.9,
            VerificationToLongitude = 23.8, VerificationToLatitude = 38.0,
            MinimumIntervalMilliseconds = 0
        };
        foreach (var (profile, osrm) in profiles.Zip(new[] { "driving", "walking" }))
            provider.ProfileMappings.Add(new RoutingProviderProfileMapping
            {
                RoutingProviderConfigurationId = provider.Id, TransportProfileId = profile.Id,
                TransportProfile = profile, OsrmProfile = osrm
            });
        return provider;
    }

    private sealed class PublicResolver : IRoutingDnsResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("8.8.8.8")]);
    }

    private sealed class ProbeTransport : IRoutingPinnedTransport
    {
        public List<string?> Credentials { get; } = [];
        public Task<HttpResponseMessage> SendAsync(
            Uri requestUri, IPAddress selectedAddress, string? bearerCredential, CancellationToken cancellationToken)
        {
            Credentials.Add(bearerCredential);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"code\":\"Ok\",\"routes\":[{\"geometry\":{\"type\":\"LineString\",\"coordinates\":[[23.7,37.9],[23.8,38.0]]}}],\"waypoints\":[{\"location\":[23.7,37.9]},{\"location\":[23.8,38.0]}]}",
                    Encoding.UTF8, "application/json")
            });
        }
    }
}
