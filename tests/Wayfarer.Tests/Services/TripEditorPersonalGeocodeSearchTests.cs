using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Models.Options;
using Wayfarer.Services;
using Wayfarer.Services.LocationProviders;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves provider selection, shared admission, cache, and fail-closed orchestration.</summary>
public sealed class TripEditorPersonalGeocodeSearchTests
{
    private static readonly PersonalProviderAuthorityBinding Binding = new(
        "geoapify", Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 2, 3, 4,
        PersonalProviderVerification.Verified, 2, 3, null, null, null);

    [Fact]
    public async Task HealthyGeoapifyContactsAndAdmitsOnceThenUsesCurrentCache()
    {
        var status = new FakeStatusReader(Healthy());
        var gate = new FakeGate(Snapshot());
        var geoHandler = new CountingHandler("""{"results":[{"place_id":"1","formatted":"Athens, Greece","lat":37.98,"lon":23.72}]}""");
        var service = Build(status, gate, geoHandler, new CountingHandler("[]"));

        var first = await service.SearchAsync("user-1", " Athens   Greece ", 6, CancellationToken.None);
        var second = await service.SearchAsync("user-1", "athens greece", 6, CancellationToken.None);

        Assert.Equal(TripEditorGeocodeSearchStatus.Success, first.Status);
        Assert.Equal(TripEditorGeocodeSearchStatus.Success, second.Status);
        Assert.Equal(1, gate.Admissions);
        Assert.Equal(1, geoHandler.Calls);
        Assert.Equal("geoapify", Assert.Single(first.Response!.Results).Provider);
    }

    [Theory]
    [InlineData(null, PersonalProviderAdmissionCategory.NoProviderSelected, false)]
    [InlineData("mapbox", PersonalProviderAdmissionCategory.Unverified, false)]
    [InlineData("geoapify", PersonalProviderAdmissionCategory.Admitted, true)]
    public async Task PermittedInitialStatesUseNominatim(string? provider, PersonalProviderAdmissionCategory category, bool exhausted)
    {
        var nominatim = new CountingHandler("[]");
        var service = Build(new FakeStatusReader(new(category, provider, true, exhausted, null, null,
            category == PersonalProviderAdmissionCategory.Admitted ? Binding : null)), new FakeGate(Snapshot()), new CountingHandler("{}"), nominatim);

        var result = await service.SearchAsync("user-1", "athens", 6, CancellationToken.None);

        Assert.Equal(TripEditorGeocodeSearchStatus.Success, result.Status);
        Assert.Equal(1, nominatim.Calls);
    }

    [Fact]
    public async Task BrokenActiveGeoapifyFailsClosed()
    {
        var nominatim = new CountingHandler("[]");
        var service = Build(new FakeStatusReader(new(PersonalProviderAdmissionCategory.Unverified, "geoapify", true,
            false, null, null, null)), new FakeGate(Snapshot()), new CountingHandler("{}"), nominatim);

        var result = await service.SearchAsync("user-1", "athens", 6, CancellationToken.None);

        Assert.Equal(TripEditorGeocodeSearchStatus.ProviderUnavailable, result.Status);
        Assert.Equal(0, nominatim.Calls);
    }

    [Fact]
    public async Task AuthoritativeExhaustedRaceFallsBackBeforeContact()
    {
        var nominatim = new CountingHandler("[]");
        var gate = new FakeGate(Snapshot()) { AdmissionCategory = PersonalProviderAdmissionCategory.Exhausted };
        var geoapify = new CountingHandler("{}");
        var service = Build(new FakeStatusReader(Healthy()), gate, geoapify, nominatim);

        var result = await service.SearchAsync("user-1", "athens", 6, CancellationToken.None);

        Assert.Equal(TripEditorGeocodeSearchStatus.Success, result.Status);
        Assert.Equal(0, geoapify.Calls);
        Assert.Equal(1, nominatim.Calls);
    }

    [Theory]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 1)]
    public async Task AuthorityDriftFailsClosedWithoutFallback(bool currentBeforeContact, bool currentBeforePublication, int geoapifyCalls)
    {
        var nominatim = new CountingHandler("[]");
        var gate = new FakeGate(Snapshot(), currentBeforeContact, currentBeforePublication);
        var geoapify = new CountingHandler("""{"results":[{"place_id":"1","formatted":"Athens","lat":1,"lon":2}]}""");
        var service = Build(new FakeStatusReader(Healthy()), gate, geoapify, nominatim);

        var result = await service.SearchAsync("user-1", "athens", 6, CancellationToken.None);

        Assert.Equal(TripEditorGeocodeSearchStatus.ProviderUnavailable, result.Status);
        Assert.Equal(geoapifyCalls, geoapify.Calls);
        Assert.Equal(0, nominatim.Calls);
        Assert.Equal(1, gate.Admissions);
    }

    [Fact]
    public async Task CancellationAfterAdmissionRemainsCancellationWithoutFallback()
    {
        using var cancellation = new CancellationTokenSource();
        var nominatim = new CountingHandler("[]");
        var gate = new FakeGate(Snapshot());
        var service = Build(new FakeStatusReader(Healthy()), gate, new CancelingHandler(cancellation), nominatim);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SearchAsync("user-1", "athens", 6, cancellation.Token));

        Assert.Equal(1, gate.Admissions);
        Assert.Equal(0, nominatim.Calls);
    }

    private static TripEditorGeocodeSearchService Build(FakeStatusReader status, FakeGate gate,
        HttpMessageHandler geoapify, HttpMessageHandler nominatim) => new(
        new NominatimTripEditorGeocodeProvider(new HttpClient(nominatim), Options.Create(new TripEditorGeocodeOptions())),
        new GeoapifyTripEditorGeocodeProvider(new HttpClient(geoapify)), status, gate,
        new MemoryCache(new MemoryCacheOptions()), new TripEditorGeocodeRateLimiter(new FixedClock()),
        Options.Create(new TripEditorGeocodeOptions()));

    private static PersonalProviderInspection Healthy() => new(
        PersonalProviderAdmissionCategory.Admitted, "geoapify", true, false, null, null, Binding);

    private static PersonalProviderAuthoritySnapshot Snapshot() => new(
        "user-1", "geoapify", PersonalProviderCapability.Geocoding, "secret", 2, 3, 4,
        profileId: Binding.ProfileId, verification: PersonalProviderVerification.Verified,
        verifiedCredentialGeneration: 2, verifiedCapabilityGeneration: 3);

    private sealed class FakeStatusReader(PersonalProviderInspection inspection) : IPersonalProviderStatusReader
    {
        public Task<PersonalProviderInspection> InspectPersistentGeocodingAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(inspection);
    }

    private sealed class FakeGate : IPersonalProviderContactGate
    {
        private readonly PersonalProviderAuthoritySnapshot snapshot;
        private readonly Queue<bool> current;

        public FakeGate(PersonalProviderAuthoritySnapshot snapshot, params bool[] current)
        { this.snapshot = snapshot; this.current = new Queue<bool>(current); }

        public int Admissions { get; private set; }
        public PersonalProviderAdmissionCategory AdmissionCategory { get; init; } = PersonalProviderAdmissionCategory.Admitted;
        public Task<PersonalProviderAdmission> AdmitAsync(string userId, PersonalProviderCapability capability,
            PersonalProviderProduct product, int cost, CancellationToken cancellationToken = default)
        { Admissions++; return Task.FromResult(AdmissionCategory == PersonalProviderAdmissionCategory.Admitted
            ? new PersonalProviderAdmission(AdmissionCategory, snapshot, null)
            : PersonalProviderAdmission.Rejected(AdmissionCategory)); }
        public Task<bool> IsCurrentAsync(PersonalProviderAuthoritySnapshot authority, CancellationToken cancellationToken = default) =>
            Task.FromResult(current.Count == 0 || current.Dequeue());
    }

    private sealed class CountingHandler(string response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) }); }
    }

    private sealed class CancelingHandler(CancellationTokenSource cancellation) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
    }

    private sealed class FixedClock : ITripEditorGeocodeClock
    { public DateTimeOffset UtcNow => new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero); }
}
