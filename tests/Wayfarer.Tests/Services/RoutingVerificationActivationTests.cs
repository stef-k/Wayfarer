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
            new RoutingProviderCredentialService(new EphemeralDataProtectionProvider()), new RoutingRequestBudget());

        var result = await verifier.VerifyAsync(provider.Id, provider.ConfigurationVersion, provider.RowVersion, "admin", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, transport.Requests);
        Assert.Equal(provider.ConfigurationVersion, provider.VerifiedConfigurationVersion);
        var audit = Assert.Single(db.AuditLogs.Where(item => item.Action == "RoutingProviderVerification"));
        Assert.Equal("admin", audit.UserId);
        Assert.Contains("Category=success", audit.Details);
    }

    [Fact]
    public async Task Verification_SuccessChargesOneUpstreamContact()
    {
        var (db, provider) = ProviderFixture(requestsPerMinute: 1);
        var budgets = new RoutingRequestBudget();
        var verifier = Verifier(db, new ProbeTransport(ValidResponse()), budgets);

        var result = await verifier.VerifyAsync(
            provider.Id, provider.ConfigurationVersion, provider.RowVersion, "admin", CancellationToken.None);
        using var lease = await budgets.AcquireProviderAsync(provider.Id, 1, provider.MaxConcurrency, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(lease);
        Assert.False(lease.TryAdmitProviderAttempt());
    }

    [Fact]
    public async Task Verification_Transient503RetryChargesTwoContacts()
    {
        var (db, provider) = ProviderFixture(requestsPerMinute: 2);
        var budgets = new RoutingRequestBudget();
        var transport = new ProbeSequenceTransport(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), ValidResponse());
        var verifier = Verifier(db, transport, budgets);

        var result = await verifier.VerifyAsync(
            provider.Id, provider.ConfigurationVersion, provider.RowVersion, "admin", CancellationToken.None);
        using var lease = await budgets.AcquireProviderAsync(provider.Id, 2, provider.MaxConcurrency, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, transport.Requests);
        Assert.NotNull(lease);
        Assert.False(lease.TryAdmitProviderAttempt());
    }

    [Fact]
    public async Task Verification_ExhaustedBudgetPreventsTransientRetry()
    {
        var (db, provider) = ProviderFixture(requestsPerMinute: 1);
        var transport = new ProbeSequenceTransport(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), ValidResponse());
        var verifier = Verifier(db, transport, new RoutingRequestBudget());

        var result = await verifier.VerifyAsync(
            provider.Id, provider.ConfigurationVersion, provider.RowVersion, "admin", CancellationToken.None);

        Assert.Equal("routing-budget-exhausted", result.ErrorCode);
        Assert.Equal(1, transport.Requests);
    }

    [Fact]
    public async Task Verification_MappedProfilesAndRetryShareOneProviderBudget()
    {
        var (db, provider) = ProviderFixture(requestsPerMinute: 2);
        var secondProfile = db.Set<TransportProfile>().First(item => item.Id != provider.ProfileMappings.Single().TransportProfileId);
        provider.ProfileMappings.Add(new RoutingProviderProfileMapping
        {
            RoutingProviderConfigurationId = provider.Id, TransportProfileId = secondProfile.Id,
            TransportProfile = secondProfile, OsrmProfile = "walking"
        });
        db.SaveChanges();
        var transport = new ProbeSequenceTransport(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), ValidResponse(), ValidResponse());
        var verifier = Verifier(db, transport, new RoutingRequestBudget());

        var result = await verifier.VerifyAsync(
            provider.Id, provider.ConfigurationVersion, provider.RowVersion, "admin", CancellationToken.None);

        Assert.Equal("routing-budget-exhausted", result.ErrorCode);
        Assert.Equal(2, transport.Requests);
    }

    [Fact]
    public async Task Verification_CancellationReturnsBoundedResultAndReleasesConcurrency()
    {
        var (db, provider) = ProviderFixture(requestsPerMinute: 10);
        provider.MaxConcurrency = 1;
        db.SaveChanges();
        var budgets = new RoutingRequestBudget();
        var transport = new BlockingProbeTransport();
        var verifier = Verifier(db, transport, budgets);
        using var cancellation = new CancellationTokenSource();
        var pending = verifier.VerifyAsync(
            provider.Id, provider.ConfigurationVersion, provider.RowVersion, "admin", cancellation.Token);
        await transport.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        var result = await pending;
        using var lease = await budgets.AcquireProviderAsync(provider.Id, 10, 1, CancellationToken.None);

        Assert.Equal("request-cancelled", result.ErrorCode);
        Assert.NotNull(lease);
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

    private (ApplicationDbContext Db, RoutingProviderConfiguration Provider) ProviderFixture(int requestsPerMinute)
    {
        var db = CreateDbContext();
        var provider = CompleteProvider(db.Set<TransportProfile>().First(), "driving");
        provider.RequestsPerMinute = requestsPerMinute;
        db.Set<RoutingProviderConfiguration>().Add(provider);
        db.SaveChanges();
        return (db, provider);
    }

    private static RoutingProviderVerifier Verifier(
        ApplicationDbContext db, IRoutingPinnedTransport transport, RoutingRequestBudget budgets) =>
        new(db, Executor(transport), new RoutingProviderCredentialService(new EphemeralDataProtectionProvider()), budgets);

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

    private sealed class ProbeSequenceTransport(params HttpResponseMessage[] responses) : IRoutingPinnedTransport
    {
        public int Requests { get; private set; }
        public async Task<HttpResponseMessage> SendAsync(
            Uri requestUri, IPAddress selectedAddress, string? bearerCredential, CancellationToken cancellationToken)
        {
            var template = responses[Requests++];
            var response = new HttpResponseMessage(template.StatusCode);
            if (template.Content != null)
                response.Content = new StringContent(
                    await template.Content.ReadAsStringAsync(cancellationToken), Encoding.UTF8, "application/json");
            return response;
        }
    }

    private sealed class BlockingProbeTransport : IRoutingPinnedTransport
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task;
        public async Task<HttpResponseMessage> SendAsync(
            Uri requestUri, IPAddress selectedAddress, string? bearerCredential, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancelled verification transport continued.");
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
