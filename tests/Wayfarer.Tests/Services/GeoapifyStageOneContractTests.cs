using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Areas.User.Controllers;
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
            ValidGeocodingJson,
            ValidRouteJson);
        var service = CreateVerificationService(db, credentials, handler);

        var geocoding = await service.VerifyGeocodingAsync(profile.UserId);

        Assert.Equal(PersonalProviderVerification.Verified, geocoding.Verification);
        Assert.Equal(PersonalProviderVerification.Unverified, profile.RoutingVerification);
        Assert.Null((await db.Set<PersonalLocationProviderSelection>().SingleOrDefaultAsync())?.GeocodingProviderKey);

        var routing = await service.VerifyRoutingAsync(profile.UserId);

        Assert.Equal(PersonalProviderVerification.Verified, routing.Verification);
        Assert.Equal(PersonalProviderVerification.Verified, profile.GeocodingVerification);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, await db.GeoapifyUsageAdmissions.SumAsync(item => item.Credits));
        Assert.Equal("lat=48.856614&lon=2.3522219&format=geojson&lang=en&limit=1", handler.Requests[0].SafeQuery);
        Assert.Equal("waypoints=48.856614,2.3522219%7C48.856817,2.353222&mode=walk&format=json&lang=en&details=instruction_details&type=balanced&traffic=free_flow", handler.Requests[1].SafeQuery);
        Assert.All(handler.Requests, request => Assert.DoesNotContain("secret-key", request.SafeDiagnostic, StringComparison.Ordinal));
        Assert.DoesNotContain("apiKey", service.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyGeocodingResultIsActionableInvalidResponse()
    {
        await using var scenario = await VerificationScenario.CreateAsync(nameof(EmptyGeocodingResultIsActionableInvalidResponse));

        var result = await scenario.Service(new RecordingHandler("{\"type\":\"FeatureCollection\",\"features\":[]}"))
            .VerifyGeocodingAsync(scenario.Profile.UserId);

        Assert.Equal(GeoapifyVerificationCategory.InvalidResponse, result.Category);
        Assert.Equal(PersonalProviderVerification.Unavailable, result.Verification);
        Assert.Equal(1, await scenario.Db.GeoapifyUsageAdmissions.SumAsync(item => item.Credits));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, GeoapifyVerificationCategory.ProviderRejected, PersonalProviderVerification.Failed)]
    [InlineData(HttpStatusCode.Forbidden, GeoapifyVerificationCategory.ProviderRejected, PersonalProviderVerification.Failed)]
    [InlineData(HttpStatusCode.TooManyRequests, GeoapifyVerificationCategory.RateLimited, PersonalProviderVerification.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, GeoapifyVerificationCategory.TemporaryFailure, PersonalProviderVerification.Unavailable)]
    public async Task ProviderFailuresRemainSafeAndAdmitted(HttpStatusCode status,
        GeoapifyVerificationCategory category, PersonalProviderVerification persisted)
    {
        await using var scenario = await VerificationScenario.CreateAsync($"provider-{(int)status}");
        var handler = new ResponseHandler(_ => new(status) { Content = new StringContent("sensitive provider body") });

        var result = await scenario.Service(handler).VerifyGeocodingAsync(scenario.Profile.UserId);

        Assert.Equal(category, result.Category);
        Assert.Equal(persisted, result.Verification);
        Assert.Equal(1, await scenario.Db.GeoapifyUsageAdmissions.SumAsync(item => item.Credits));
    }

    [Fact]
    public async Task KnownOversizeIsRejectedBeforeContentRead()
    {
        await using var scenario = await VerificationScenario.CreateAsync(nameof(KnownOversizeIsRejectedBeforeContentRead));
        var content = new NeverReadContent(262_145);

        var result = await scenario.Service(new ResponseHandler(_ => new(HttpStatusCode.OK) { Content = content }))
            .VerifyGeocodingAsync(scenario.Profile.UserId);

        Assert.Equal(GeoapifyVerificationCategory.InvalidResponse, result.Category);
        Assert.False(content.ReadAttempted);
    }

    [Theory]
    [InlineData(false, GeoapifyVerificationCategory.Verified)]
    [InlineData(true, GeoapifyVerificationCategory.InvalidResponse)]
    public async Task UnknownLengthResponseIsHardBounded(bool overflow, GeoapifyVerificationCategory expected)
    {
        await using var scenario = await VerificationScenario.CreateAsync($"unknown-{overflow}");
        var bytes = overflow ? new byte[262_145] : Encoding.UTF8.GetBytes(ValidGeocodingJson);
        var content = new StreamContent(new NonSeekableStream(bytes));
        Assert.Null(content.Headers.ContentLength);

        var result = await scenario.Service(new ResponseHandler(_ => new(HttpStatusCode.OK) { Content = content }))
            .VerifyGeocodingAsync(scenario.Profile.UserId);

        Assert.Equal(expected, result.Category);
    }

    [Theory]
    [InlineData(false, false, GeoapifyVerificationCategory.AuthorizationDisabled)]
    [InlineData(true, false, GeoapifyVerificationCategory.CredentialUnavailable)]
    public async Task PreContactRejectionIsActionableAndFree(bool authorized, bool readable,
        GeoapifyVerificationCategory expected)
    {
        await using var db = CreateDb($"pre-{authorized}-{readable}");
        var credentials = new PersonalProviderCredentialService(new EphemeralDataProtectionProvider());
        var profile = PersonalLocationProviderProfile.Create("user", PersonalLocationProvider.Geoapify);
        if (readable) credentials.Replace(profile, "key");
        else profile.ProtectedCredential = "unreadable";
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, authorized);
        db.Add(profile);
        await db.SaveChangesAsync();
        var handler = new RecordingHandler(ValidGeocodingJson);

        var result = await CreateVerificationService(db, credentials, handler).VerifyGeocodingAsync(profile.UserId);

        Assert.Equal(expected, result.Category);
        Assert.Empty(handler.Requests);
        Assert.Empty(db.GeoapifyUsageAdmissions);
    }

    [Fact]
    public async Task ExhaustedGuardRejectsBeforeContactWithoutAnotherCredit()
    {
        await using var scenario = await VerificationScenario.CreateAsync(nameof(ExhaustedGuardRejectsBeforeContactWithoutAnotherCredit));
        scenario.Db.Add(new GeoapifyUsageGuard { UserId = scenario.Profile.UserId, Enabled = true, CreditLimit = 0 });
        await scenario.Db.SaveChangesAsync();
        var handler = new RecordingHandler(ValidGeocodingJson);

        var result = await scenario.Service(handler).VerifyGeocodingAsync(scenario.Profile.UserId);

        Assert.Equal(GeoapifyVerificationCategory.GuardExhausted, result.Category);
        Assert.Empty(handler.Requests);
        Assert.Empty(scenario.Db.GeoapifyUsageAdmissions);
    }

    [Fact]
    public async Task RevokedCredentialIsDistinguishedBeforeContact()
    {
        await using var scenario = await VerificationScenario.CreateAsync(nameof(RevokedCredentialIsDistinguishedBeforeContact));
        scenario.RevokeCredential();
        await scenario.Db.SaveChangesAsync();
        var handler = new RecordingHandler(ValidGeocodingJson);

        var result = await scenario.Service(handler).VerifyGeocodingAsync(scenario.Profile.UserId);

        Assert.Equal(GeoapifyVerificationCategory.CredentialUnavailable, result.Category);
        Assert.Empty(handler.Requests);
        Assert.Empty(scenario.Db.GeoapifyUsageAdmissions);
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellationAfterAdmission()
    {
        await using var scenario = await VerificationScenario.CreateAsync(nameof(CallerCancellationRemainsCancellationAfterAdmission));
        using var cancellation = new CancellationTokenSource();
        var handler = new CancellationHandler(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scenario.Service(handler).VerifyGeocodingAsync(scenario.Profile.UserId, cancellation.Token));

        Assert.Equal(1, await scenario.Db.GeoapifyUsageAdmissions.SumAsync(item => item.Credits));
    }

    [Fact]
    public async Task ProviderTimeoutIsActionableAndRemainsAdmitted()
    {
        await using var scenario = await VerificationScenario.CreateAsync(nameof(ProviderTimeoutIsActionableAndRemainsAdmitted));

        var result = await scenario.Service(new TimeoutHandler()).VerifyRoutingAsync(scenario.Profile.UserId);

        Assert.Equal(GeoapifyVerificationCategory.TemporaryFailure, result.Category);
        Assert.Equal(1, await scenario.Db.GeoapifyUsageAdmissions.SumAsync(item => item.Credits));
    }

    [Fact]
    public async Task IncompatibleRoutingGeometryIsRejectedThroughProductionParser()
    {
        await using var scenario = await VerificationScenario.CreateAsync(nameof(IncompatibleRoutingGeometryIsRejectedThroughProductionParser));
        var incompatible = ValidRouteJson.Replace("[[[2.3522219,48.856614]", "{\"type\":\"LineString\",\"coordinates\":[[2.3522219,48.856614]", StringComparison.Ordinal);

        var result = await scenario.Service(new RecordingHandler(incompatible)).VerifyRoutingAsync(scenario.Profile.UserId);

        Assert.Equal(GeoapifyVerificationCategory.InvalidResponse, result.Category);
    }

    [Fact]
    public void PresentationMapsOnlyBoundedOutcomeDetail()
    {
        var message = LocationProviderSettingsController.GeoapifyVerificationMessage(
            PersonalProviderCapability.Routing,
            new(PersonalProviderVerification.Unavailable, GeoapifyVerificationCategory.InvalidResponse));

        Assert.Equal("Geoapify routing verification the provider response was invalid or incompatible. No provider was selected automatically.", message);
        Assert.DoesNotContain("apiKey", message, StringComparison.OrdinalIgnoreCase);
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

        Assert.Equal(GeoapifyVerificationCategory.AuthorityChanged, result.Category);
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
        Assert.Empty(handler.Requests);
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

    private const string ValidGeocodingJson = """
        {"type":"FeatureCollection","features":[{"type":"Feature","properties":{
        "formatted":"Place de l'Hotel de Ville, 75004 Paris, France","address_line1":"Place de l'Hotel de Ville"}}]}
        """;

    private const string ValidRouteJson = """
        {"results":[{"distance":78,"time":56,"distance_units":"meters",
        "geometry":[[[2.3522219,48.856614],[2.3527,48.8567],[2.353222,48.856817]]],
        "legs":[{"distance":78,"time":56,"steps":[{"instruction":{"text":"Walk east","type":"Straight"},
        "from_index":0,"to_index":2,"distance":78,"time":56}]}]}]}
        """;

    private sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
    {
        private int index;
        public Action? BeforeResponse { get; init; }
        public List<(string Method, string Host, string Path, string SafeQuery, string SafeDiagnostic)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var safeQuery = request.RequestUri!.Query.TrimStart('?').Split("&apiKey=", 2)[0];
            Requests.Add((request.Method.Method, request.RequestUri.Host, request.RequestUri.AbsolutePath, safeQuery,
                $"{request.Method.Method} {request.RequestUri.Host}{request.RequestUri.AbsolutePath}"));
            BeforeResponse?.Invoke();
            var response = responses[Math.Min(index++, responses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) });
        }
    }

    /// <summary>Returns caller-selected fake HTTP responses without provider contact.</summary>
    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    /// <summary>Cancels an admitted fake request without contacting a provider.</summary>
    private sealed class CancellationHandler(CancellationTokenSource source) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            source.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
        }
    }

    /// <summary>Models a provider timeout while the caller remains connected.</summary>
    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("fake timeout"));
    }

    /// <summary>Proves advertised oversize rejection without allowing a content read.</summary>
    private sealed class NeverReadContent(long length) : HttpContent
    {
        public bool ReadAttempted { get; private set; }
        protected override bool TryComputeLength(out long computed) { computed = length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { ReadAttempted = true; throw new InvalidOperationException("Content must not be read."); }
    }

    /// <summary>Models chunked content whose final length is not advertised.</summary>
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }

    /// <summary>Owns a valid independently-authorized in-memory verification scenario.</summary>
    private sealed class VerificationScenario : IAsyncDisposable
    {
        private readonly PersonalProviderCredentialService credentials;
        public ApplicationDbContext Db { get; }
        public PersonalLocationProviderProfile Profile { get; }
        private VerificationScenario(ApplicationDbContext db, PersonalProviderCredentialService credentials,
            PersonalLocationProviderProfile profile) => (Db, this.credentials, Profile) = (db, credentials, profile);
        public GeoapifyVerificationService Service(HttpMessageHandler handler) => CreateVerificationService(Db, credentials, handler);
        public void RevokeCredential() => credentials.Revoke(Profile);
        public static async Task<VerificationScenario> CreateAsync(string name)
        {
            var db = CreateDb(name);
            var credentials = new PersonalProviderCredentialService(new EphemeralDataProtectionProvider());
            var profile = PersonalLocationProviderProfile.Create("user", PersonalLocationProvider.Geoapify);
            credentials.Replace(profile, "fake-key");
            profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
            profile.SetAuthorization(PersonalProviderCapability.Routing, true);
            db.Add(profile);
            await db.SaveChangesAsync();
            return new(db, credentials, profile);
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
