using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Wayfarer.Models;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Verifies bounded profile probes and singleton-owned atomic activation.</summary>
public sealed class RoutingVerificationActivationTests : TestBase
{
    [Fact]
    public async Task Verification_ProbesEachDistinctProfileAndMarksOnlyCurrentVersion()
    {
        var db = CreateDbContext();
        var profiles = db.Set<TransportProfile>().Take(2).ToArray();
        var provider = CompleteProvider(profiles[0], "driving");
        provider.ProfileMappings.Add(new RoutingProviderProfileMapping
        {
            RoutingProviderConfigurationId = provider.Id, TransportProfileId = profiles[1].Id,
            TransportProfile = profiles[1], OsrmProfile = "walking"
        });
        db.Set<RoutingProviderConfiguration>().Add(provider);
        db.SaveChanges();
        var transport = new ProbeTransport(ValidResponse());
        var verifier = new RoutingProviderVerifier(db, Executor(transport),
            new RoutingProviderCredentialService(new EphemeralDataProtectionProvider()));

        var result = await verifier.VerifyAsync(provider.Id, provider.ConfigurationVersion, provider.RowVersion, "admin", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, transport.Requests);
        Assert.Equal(provider.ConfigurationVersion, provider.VerifiedConfigurationVersion);
        var audit = Assert.Single(db.AuditLogs.Where(item => item.Action == "RoutingProviderVerification"));
        Assert.Equal("admin", audit.UserId);
        Assert.Contains("Category=success", audit.Details);
    }

    [Fact]
    public async Task Activation_SelectsCandidateOnlyAfterLockedRechecks()
    {
        var db = CreateDbContext();
        var profile = db.Set<TransportProfile>().First();
        var candidate = CompleteProvider(profile, "driving");
        db.Set<RoutingProviderConfiguration>().Add(candidate);
        db.ApplicationSettings.Add(new ApplicationSettings { Id = 1 });
        db.SaveChanges();
        var service = new RoutingProviderActivationService(db, new MarkVerifiedVerifier(db));

        var result = await service.VerifyAndActivateAsync(candidate.Id, 1, candidate.RowVersion, 0, "admin", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(candidate.Id, db.ApplicationSettings.Single().ActiveRoutingProviderConfigurationId);
        Assert.Contains(db.AuditLogs, item => item.Action == "RoutingProviderActivation"
            && item.UserId == "admin" && item.Details.Contains("Category=success"));
    }

    [Fact]
    public async Task FailedReplacement_PreservesPreviousSelection()
    {
        var db = CreateDbContext();
        var profile = db.Set<TransportProfile>().First();
        var previous = CompleteProvider(profile, "driving");
        var candidate = CompleteProvider(profile, "walking");
        db.Set<RoutingProviderConfiguration>().AddRange(previous, candidate);
        db.ApplicationSettings.Add(new ApplicationSettings { Id = 1, ActiveRoutingProviderConfigurationId = previous.Id });
        db.SaveChanges();
        var service = new RoutingProviderActivationService(db, new FailingVerifier());

        var result = await service.VerifyAndActivateAsync(candidate.Id, 1, candidate.RowVersion, 0, "admin", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(previous.Id, db.ApplicationSettings.Single().ActiveRoutingProviderConfigurationId);
        Assert.Contains(db.AuditLogs, item => item.Action == "RoutingProviderActivation"
            && item.UserId == "admin" && item.Details.Contains("Category=verification-failed"));
    }

    private static RoutingProviderConfiguration CompleteProvider(TransportProfile profile, string osrmProfile)
    {
        var provider = new RoutingProviderConfiguration
        {
            Id = Guid.NewGuid(), DisplayName = "OSRM", Enabled = true, BaseEndpoint = "https://routing.example",
            VerificationFromLongitude = 23.7, VerificationFromLatitude = 37.9,
            VerificationToLongitude = 23.8, VerificationToLatitude = 38.0
        };
        provider.ProfileMappings.Add(new RoutingProviderProfileMapping
        {
            RoutingProviderConfigurationId = provider.Id, TransportProfileId = profile.Id,
            TransportProfile = profile, OsrmProfile = osrmProfile
        });
        return provider;
    }

    private static RoutingBoundedExecutor Executor(IRoutingPinnedTransport transport) => new(
        new PublicResolver(), new RoutingEndpointPolicy(Options.Create(new RoutingOutboundOptions())), transport);

    private static HttpResponseMessage ValidResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"code\":\"Ok\",\"routes\":[{\"geometry\":{\"type\":\"LineString\",\"coordinates\":[[23.7,37.9],[23.8,38.0]]}}],\"waypoints\":[{\"location\":[23.7,37.9]},{\"location\":[23.8,38.0]}]}",
            Encoding.UTF8, "application/json")
    };

    private sealed class PublicResolver : IRoutingDnsResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("8.8.8.8")]);
    }

    private sealed class ProbeTransport(HttpResponseMessage template) : IRoutingPinnedTransport
    {
        public int Requests { get; private set; }
        public async Task<HttpResponseMessage> SendAsync(Uri requestUri, IPAddress selectedAddress, string? bearerCredential, CancellationToken cancellationToken)
        {
            Requests++;
            return new HttpResponseMessage(template.StatusCode)
            { Content = new StringContent(await template.Content.ReadAsStringAsync(cancellationToken), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class MarkVerifiedVerifier(ApplicationDbContext db) : IRoutingProviderVerifier
    {
        public async Task<RoutingVerificationResult> VerifyAsync(Guid providerId, int expectedVersion, uint expectedRowVersion, string administratorId, CancellationToken cancellationToken)
        {
            var provider = db.Set<RoutingProviderConfiguration>().Single(item => item.Id == providerId);
            provider.VerifiedConfigurationVersion = expectedVersion;
            await db.SaveChangesAsync(cancellationToken);
            return new RoutingVerificationResult(true, null, expectedVersion, provider.RowVersion);
        }
    }

    private sealed class FailingVerifier : IRoutingProviderVerifier
    {
        public Task<RoutingVerificationResult> VerifyAsync(Guid providerId, int expectedVersion, uint expectedRowVersion, string administratorId, CancellationToken cancellationToken) =>
            Task.FromResult(RoutingVerificationResult.Failure("provider-verification-invalid"));
    }
}
