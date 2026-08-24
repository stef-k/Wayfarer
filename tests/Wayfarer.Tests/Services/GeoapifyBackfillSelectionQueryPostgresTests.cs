using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves PostgreSQL capability eligibility and candidate-selection query contracts.</summary>
public sealed partial class GeoapifyBackfillConcurrencyPostgresTests
{
    /// <summary>Proves superseded provider-dependent attempts are reconsidered without reviving permanent same-generation rows.</summary>
    [PostgresFact]
    public async Task SupersededAuthorityAttemptIsEligibleButSameGenerationPermanentAttemptIsNot()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        await using var db = fixture.CreateContext();
        var location = await db.Locations.SingleAsync(item => item.UserId == user.Id);
        var workflow = LocationEnrichmentWorkflow.Create(user.Id, DateTime.UtcNow);
        workflow.Start(DateTime.UtcNow);
        db.Add(workflow);
        db.Add(new LocationEnrichmentAttempt
        {
            UserId = user.Id, LocationId = location.Id, ProviderKey = "geoapify",
            Capability = PersonalProviderCapability.Geocoding,
            CredentialGeneration = 1, ConfigurationGeneration = 1, SelectionGeneration = 1,
            Outcome = LocationEnrichmentOutcome.NoResult, AdmittedAttemptCount = 1,
            LastAttemptAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var superseded = await GeoapifyLocationBackfillService.LoadCandidateIdsAsync(db, user.Id,
            new("geoapify", PersonalProviderCapability.Geocoding, 2, 1, 1), 10);
        var sameGeneration = await GeoapifyLocationBackfillService.LoadCandidateIdsAsync(db, user.Id,
            new("geoapify", PersonalProviderCapability.Geocoding, 1, 1, 1), 10);

        Assert.Contains(location.Id, superseded);
        Assert.DoesNotContain(location.Id, sameGeneration);
    }

    /// <summary>Proves capability participates in provider-dependent supersession.</summary>
    [PostgresTheory]
    [InlineData(null, true)]
    [InlineData(PersonalProviderCapability.Routing, true)]
    [InlineData(PersonalProviderCapability.Geocoding, false)]
    public async Task CapabilityAuthorityControlsPermanentDeferredEligibility(
        PersonalProviderCapability? storedCapability, bool expectedEligible)
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        await using var db = fixture.CreateContext();
        var location = await db.Locations.SingleAsync(item => item.UserId == user.Id);
        var profile = await db.PersonalLocationProviderProfiles.SingleAsync(item => item.UserId == user.Id);
        db.Add(LocationEnrichmentWorkflow.Create(user.Id, DateTime.UtcNow));
        db.Add(new LocationEnrichmentAttempt
        {
            UserId = user.Id, LocationId = location.Id, ProviderKey = "geoapify", ProviderProfileId = profile.Id,
            Capability = storedCapability, CredentialGeneration = profile.CredentialGeneration,
            ConfigurationGeneration = profile.GeocodingGeneration, SelectionGeneration = 1,
            Verification = profile.GeocodingVerification,
            VerificationCredentialGeneration = profile.GeocodingVerifiedCredentialGeneration,
            VerificationGeneration = profile.GeocodingVerifiedConfigurationGeneration,
            Outcome = LocationEnrichmentOutcome.NoResult, AdmittedAttemptCount = 1, LastAttemptAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var ids = await GeoapifyLocationBackfillService.LoadCandidateIdsAsync(db, user.Id,
            new("geoapify", PersonalProviderCapability.Geocoding, profile.CredentialGeneration,
                profile.GeocodingGeneration, 1, profile.Id, profile.GeocodingVerification,
                profile.GeocodingVerifiedCredentialGeneration, profile.GeocodingVerifiedConfigurationGeneration,
                null, null, null), 10);
        Assert.Equal(expectedEligible, ids.Contains(location.Id));
    }
}
