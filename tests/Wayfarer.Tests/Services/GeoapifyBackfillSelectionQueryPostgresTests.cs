using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Services;

/// <summary>Proves PostgreSQL capability eligibility and candidate-selection query contracts.</summary>
public sealed partial class GeoapifyBackfillConcurrencyPostgresTests
{
    /// <summary>Proves invalid committed coordinates are classified without provider admission or contact.</summary>
    [PostgresTheory]
    [InlineData(20, double.NaN)]
    [InlineData(double.PositiveInfinity, 10)]
    public async Task InvalidCoordinatesAreClassifiedBeforeProviderAdmission(double longitude, double latitude)
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        double committedLongitude;
        double committedLatitude;
        await using (var arrange = fixture.CreateContext())
        {
            var location = await arrange.Locations.SingleAsync(item => item.UserId == user.Id);
            location.Coordinates = new Point(longitude, latitude) { SRID = 4326 };
            await arrange.SaveChangesAsync();
            await arrange.Entry(location).ReloadAsync();
            committedLongitude = location.Coordinates.X;
            committedLatitude = location.Coordinates.Y;
        }
        var handler = new CoordinatedHandler(user.Id, null);
        handler.Release();

        var result = await Service(protection, handler).RunAsync(user.Id);

        await using var verify = fixture.CreateContext();
        var attempt = await verify.LocationEnrichmentAttempts.SingleAsync(item => item.UserId == user.Id);
        Assert.Equal(LocationEnrichmentOutcome.InvalidCoordinates, attempt.Outcome);
        Assert.Null(attempt.LastAttemptAtUtc);
        Assert.Equal(0, result.Admitted);
        Assert.Equal(0, attempt.AdmittedAttemptCount);
        Assert.Null(attempt.NextAttemptAtUtc);
        Assert.Null(attempt.OperationId);
        Assert.Null(attempt.OperationLeaseId);
        Assert.Null(attempt.OperationFencingGeneration);
        Assert.Null(attempt.OperationStartedAtUtc);
        Assert.Null(attempt.OperationWorkflowEpoch);
        Assert.Null(attempt.OperationAttemptNumber);
        Assert.Empty(await verify.GeoapifyUsageAdmissions.Where(item => item.UserId == user.Id).ToListAsync());
        Assert.Empty(await verify.MapboxProductMeters.Where(item => item.UserId == user.Id).ToListAsync());
        Assert.Equal(0, handler.RequestsFor(user.Id));
        var preserved = (await verify.Locations.SingleAsync(item => item.UserId == user.Id)).Coordinates;
        Assert.Equal(committedLongitude, preserved.X);
        Assert.Equal(committedLatitude, preserved.Y);
    }

    /// <summary>Proves a coordinate mutation committed after discovery is authoritative before admission.</summary>
    [PostgresFact]
    public async Task CoordinatesInvalidatedAfterCandidateReadAreClassifiedBeforeAdmission()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        var gate = new CandidateReadGate();
        var handler = new CoordinatedHandler(user.Id, null);
        handler.Release();

        var run = Service(protection, handler, interceptors: gate).RunAsync(user.Id);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var mutation = fixture.CreateContext())
        {
            var location = await mutation.Locations.SingleAsync(item => item.UserId == user.Id);
            location.Coordinates = new Point(double.PositiveInfinity, 10) { SRID = 4326 };
            await mutation.SaveChangesAsync();
        }
        gate.Release();
        var result = await run;

        await using var verify = fixture.CreateContext();
        var attempt = await verify.LocationEnrichmentAttempts.SingleAsync(item => item.UserId == user.Id);
        Assert.Equal(LocationEnrichmentOutcome.InvalidCoordinates, attempt.Outcome);
        Assert.Equal(0, result.Admitted);
        Assert.Equal(0, attempt.AdmittedAttemptCount);
        Assert.Null(attempt.OperationId);
        Assert.Empty(await verify.GeoapifyUsageAdmissions.Where(item => item.UserId == user.Id).ToListAsync());
        Assert.Empty(await verify.MapboxProductMeters.Where(item => item.UserId == user.Id).ToListAsync());
        Assert.Equal(0, handler.RequestsFor(user.Id));
        var coordinates = (await verify.Locations.SingleAsync(item => item.UserId == user.Id)).Coordinates;
        Assert.True(double.IsPositiveInfinity(coordinates.X));
        Assert.Equal(10, coordinates.Y);
    }

    /// <summary>Proves local invalidity is classified without selected provider authority.</summary>
    [PostgresFact]
    public async Task InvalidCoordinatesAreClassifiedWhenProviderAuthorityIsUnavailable()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        await using (var arrange = fixture.CreateContext())
        {
            var location = await arrange.Locations.SingleAsync(item => item.UserId == user.Id);
            location.Coordinates = new Point(double.PositiveInfinity, 10) { SRID = 4326 };
            arrange.PersonalLocationProviderSelections.Remove(
                await arrange.PersonalLocationProviderSelections.SingleAsync(item => item.UserId == user.Id));
            await arrange.SaveChangesAsync();
        }
        var handler = new CoordinatedHandler(user.Id, null);

        var result = await Service(protection, handler).RunAsync(user.Id);

        await using var verify = fixture.CreateContext();
        Assert.Equal(LocationEnrichmentOutcome.InvalidCoordinates,
            (await verify.LocationEnrichmentAttempts.SingleAsync(item => item.UserId == user.Id)).Outcome);
        Assert.False(result.AuthorityUnavailable);
        Assert.Equal(0, result.Admitted);
        Assert.Empty(await verify.GeoapifyUsageAdmissions.Where(item => item.UserId == user.Id).ToListAsync());
        Assert.Equal(0, handler.RequestsFor(user.Id));
    }

    /// <summary>Proves an oldest invalid row does not consume budget or starve a later valid row.</summary>
    [PostgresFact]
    public async Task OldestInvalidCoordinatesDoNotStarveLaterValidCandidateAtBudgetBoundary()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        await using (var arrange = fixture.CreateContext())
        {
            var invalid = await arrange.Locations.SingleAsync(item => item.UserId == user.Id);
            invalid.Timestamp = DateTime.UtcNow.AddMinutes(-1);
            invalid.Coordinates = new Point(20, double.NaN) { SRID = 4326 };
            arrange.GeoapifyUsageGuards.Single(item => item.UserId == user.Id).CreditLimit = 1;
            arrange.Locations.Add(new Location
            {
                UserId = user.Id, Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow,
                TimeZoneId = "UTC", Coordinates = new Point(20, 10) { SRID = 4326 }
            });
            await arrange.SaveChangesAsync();
        }
        var handler = new CoordinatedHandler(user.Id, null);
        handler.Release();

        var result = await Service(protection, handler).RunAsync(user.Id);

        await using var verify = fixture.CreateContext();
        var invalidAttempt = await verify.LocationEnrichmentAttempts.SingleAsync(item => item.UserId == user.Id);
        Assert.Equal(LocationEnrichmentOutcome.InvalidCoordinates, invalidAttempt.Outcome);
        Assert.Equal(1, result.Admitted);
        Assert.Equal(1, result.Succeeded);
        Assert.Single(await verify.GeoapifyUsageAdmissions.Where(item => item.UserId == user.Id).ToListAsync());
        Assert.Equal(1, handler.RequestsFor(user.Id));
    }

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
