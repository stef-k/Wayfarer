using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationProviders;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Locks the three bounded corrections discovered by the final #501 review.</summary>
public sealed class MapboxGeocodingCorrectionTests
{
    [Theory]
    [InlineData("{\"features\":[]}")]
    [InlineData("{\"type\":\"Other\",\"features\":[]}")]
    [InlineData("{\"type\":\"FeatureCollection\",\"features\":null}")]
    [InlineData("{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"id\":\"x\",\"properties\":null}]}")]
    [InlineData("{\"type\":\"FeatureCollection\",\"features\":[{\"properties\":{\"feature_type\":\"street\",\"full_address\":\"x\"}}]}")]
    public async Task StructurallyInvalidResponses_AreBoundedAndDiscarded(string json)
    {
        var service = CreateParser(json);

        var result = await service.GetReverseGeocodingDataAsync(10, 20, "credential");

        Assert.Equal(string.Empty, result.FullAddress);
        Assert.Equal(string.Empty, result.Address);
    }

    [Fact]
    public async Task ValidPartialOptionalContext_RemainsSupported()
    {
        const string json = """
            {"type":"FeatureCollection","features":[{"type":"Feature","id":"street.1","properties":
            {"mapbox_id":"street.1","feature_type":"street","full_address":"Main Street","context":null}}]}
            """;

        var result = await CreateParser(json).GetReverseGeocodingDataAsync(10, 20, "credential");

        Assert.Equal("Main Street", result.FullAddress);
    }

    [Fact]
    public async Task VerificationWrite_RejectsAReplacementCredentialSnapshot()
    {
        await using var db = CreateDb(nameof(VerificationWrite_RejectsAReplacementCredentialSnapshot));
        var credentialOwner = new PersonalProviderCredentialService(new EphemeralDataProtectionProvider());
        var profile = PersonalLocationProviderProfile.Create("user", PersonalLocationProvider.Mapbox);
        credentialOwner.Replace(profile, "generation-one");
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
        profile.GrantPermanentGeocodingConsent(DateTimeOffset.UtcNow);
        db.Add(profile);
        await db.SaveChangesAsync();
        var snapshot = new PersonalProviderAuthoritySnapshot("user", "mapbox", PersonalProviderCapability.Geocoding,
            "generation-one", profile.CredentialGeneration, profile.GeocodingGeneration, 0,
            profile.PermanentGeocodingConsentVersion, profile.PermanentGeocodingConsentedAt,
            profile.PermanentGeocodingConsentCredentialGeneration);

        credentialOwner.Replace(profile, "generation-two");
        profile.GrantPermanentGeocodingConsent(DateTimeOffset.UtcNow.AddMinutes(1));
        await db.SaveChangesAsync();
        var gate = new PersonalProviderContactGate(db, credentialOwner,
            new LegacyMapboxMigrationService(db, credentialOwner), new ConfigurationBuilder().Build());

        var updated = await gate.TryRecordMapboxPermanentVerificationAsync(
            snapshot, PersonalProviderVerification.Verified);

        Assert.False(updated);
        Assert.Equal(PersonalProviderVerification.Unverified, profile.GeocodingVerification);
        Assert.Null(profile.GeocodingVerifiedCredentialGeneration);
        Assert.Null(profile.GeocodingVerifiedConfigurationGeneration);
    }

    [Fact]
    public async Task VerificationWrite_AcceptsExactlyMatchingSnapshot()
    {
        await using var db = CreateDb(nameof(VerificationWrite_AcceptsExactlyMatchingSnapshot));
        var credentialOwner = new PersonalProviderCredentialService(new EphemeralDataProtectionProvider());
        var profile = PersonalLocationProviderProfile.Create("user", PersonalLocationProvider.Mapbox);
        credentialOwner.Replace(profile, "credential");
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
        profile.GrantPermanentGeocodingConsent(DateTimeOffset.UtcNow);
        db.Add(profile);
        await db.SaveChangesAsync();
        var snapshot = new PersonalProviderAuthoritySnapshot("user", "mapbox", PersonalProviderCapability.Geocoding,
            "credential", profile.CredentialGeneration, profile.GeocodingGeneration, 0,
            profile.PermanentGeocodingConsentVersion, profile.PermanentGeocodingConsentedAt,
            profile.PermanentGeocodingConsentCredentialGeneration);
        var gate = new PersonalProviderContactGate(db, credentialOwner,
            new LegacyMapboxMigrationService(db, credentialOwner), new ConfigurationBuilder().Build());

        var updated = await gate.TryRecordMapboxPermanentVerificationAsync(
            snapshot, PersonalProviderVerification.Verified);

        Assert.True(updated);
        Assert.Equal(PersonalProviderVerification.Verified, profile.GeocodingVerification);
        Assert.Equal(snapshot.CredentialGeneration, profile.GeocodingVerifiedCredentialGeneration);
        Assert.Equal(snapshot.CapabilityGeneration, profile.GeocodingVerifiedConfigurationGeneration);
    }

    [Fact]
    public void ApiCaptureFlows_RethrowRequestCancellationBeforeSaveFailureCatch()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Areas", "Api", "Controllers", "LocationController.cs"));

        Assert.Equal(3, source.Split("catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)").Length - 1);
    }

    private static ReverseGeocodingService CreateParser(string json) => new(
        new HttpClient(new JsonHandler(json)), NullLogger<BaseApiController>.Instance);

    private static ApplicationDbContext CreateDb(string name)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(name).Options;
        return new ApplicationDbContext(options, new ServiceCollection().BuildServiceProvider());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Wayfarer.csproj"))) directory = directory.Parent;
        return Assert.IsType<DirectoryInfo>(directory).FullName;
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}
