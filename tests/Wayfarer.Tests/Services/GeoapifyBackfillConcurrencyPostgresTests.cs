using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using Npgsql;
using System.Data.Common;
using System.Net;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Services;

/// <summary>Proves one durable per-user backfill owner spans selection, admission, contact, and persistence.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed partial class GeoapifyBackfillConcurrencyPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Proves provider authority admitted before contact cannot authorize stale persistence.</summary>
    [PostgresTheory(Timeout = 30_000)]
    [InlineData(AuthorityMutation.ReplaceCredential)]
    [InlineData(AuthorityMutation.RevokeCredential)]
    [InlineData(AuthorityMutation.ChangeSelection)]
    [InlineData(AuthorityMutation.ChangeCapabilityGeneration)]
    [InlineData(AuthorityMutation.ChangeVerificationState)]
    [InlineData(AuthorityMutation.ChangeVerifiedCredentialBinding)]
    [InlineData(AuthorityMutation.ChangeVerifiedCapabilityBinding)]
    [InlineData(AuthorityMutation.ChangeProfileIdentity)]
    public async Task AuthorityMutationDuringContactDiscardsProviderResult(AuthorityMutation mutation)
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        var handler = new CoordinatedHandler(user.Id, null);
        await using var runDb = fixture.CreateContext();
        var run = Service(runDb, protection, handler).RunAsync(user.Id);
        await handler.FirstUserRequestEntered;

        await using (var mutate = fixture.CreateContext())
        {
            var profile = await mutate.PersonalLocationProviderProfiles
                .SingleAsync(item => item.UserId == user.Id && item.ProviderKey == "geoapify");
            var selection = await mutate.PersonalLocationProviderSelections.SingleAsync(item => item.UserId == user.Id);
            if (mutation == AuthorityMutation.ReplaceCredential)
                new PersonalProviderCredentialService(protection).Replace(profile, "replacement");
            else if (mutation == AuthorityMutation.RevokeCredential)
                new PersonalProviderCredentialService(protection).Revoke(profile);
            else if (mutation == AuthorityMutation.ChangeSelection)
                selection.Select(PersonalProviderCapability.Geocoding, null);
            else if (mutation == AuthorityMutation.ChangeCapabilityGeneration)
                profile.SetAuthorization(PersonalProviderCapability.Geocoding, false);
            else if (mutation == AuthorityMutation.ChangeVerificationState)
                profile.GeocodingVerification = PersonalProviderVerification.Failed;
            else if (mutation == AuthorityMutation.ChangeVerifiedCredentialBinding)
                profile.GeocodingVerifiedCredentialGeneration++;
            else if (mutation == AuthorityMutation.ChangeVerifiedCapabilityBinding)
                profile.GeocodingVerifiedConfigurationGeneration++;
            else
                await mutate.Database.ExecuteSqlInterpolatedAsync($$"""
                    UPDATE "PersonalLocationProviderProfiles" SET "Id" = {{Guid.NewGuid()}}
                    WHERE "UserId" = {{user.Id}} AND "ProviderKey" = 'geoapify'
                    """);
            await mutate.SaveChangesAsync();
        }

        handler.Release();
        var result = await run;
        await using var verify = fixture.CreateContext();
        Assert.Equal(0, result.Succeeded);
        Assert.True(GeoapifyLocationBackfillService.IsWhollyUnenriched(
            await verify.Locations.SingleAsync(item => item.UserId == user.Id)));
        Assert.Single(await verify.GeoapifyUsageAdmissions.Where(item => item.UserId == user.Id).ToListAsync());
        Assert.NotNull((await verify.LocationEnrichmentAttempts.SingleAsync(item => item.UserId == user.Id)).OperationId);
    }

    /// <summary>Proves invalidating admitted Mapbox Permanent consent discards the contacted result.</summary>
    [PostgresFact(Timeout = 30_000)]
    public async Task MapboxConsentInvalidationDuringContactDiscardsProviderResult()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedMapboxAsync(user.Id, protection);
        var handler = new CoordinatedHandler(user.Id, null);
        await using var runDb = fixture.CreateContext();
        var run = Service(runDb, protection, handler).RunAsync(user.Id);
        await handler.FirstUserRequestEntered;
        await using (var mutate = fixture.CreateContext())
        {
            var profile = await mutate.PersonalLocationProviderProfiles.SingleAsync(
                item => item.UserId == user.Id && item.ProviderKey == "mapbox");
            profile.ClearPermanentGeocodingConsent();
            await mutate.SaveChangesAsync();
        }
        handler.Release();
        var result = await run;

        await using var verify = fixture.CreateContext();
        Assert.Equal(0, result.Succeeded);
        Assert.True(GeoapifyLocationBackfillService.IsWhollyUnenriched(
            await verify.Locations.SingleAsync(item => item.UserId == user.Id)));
        Assert.Equal(1, (await verify.MapboxProductMeters.SingleAsync(item => item.UserId == user.Id)).AdmittedCount);
    }

    /// <summary>Proves a manual edit committed after response inspection wins over scheduled enrichment.</summary>
    [PostgresFact(Timeout = 30_000)]
    public async Task ManualEditDuringContactWinsAtomicLocationEligibility()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        var handler = new CoordinatedHandler(user.Id, null);
        await using var runDb = fixture.CreateContext();
        var run = Service(runDb, protection, handler).RunAsync(user.Id);
        await handler.FirstUserRequestEntered;
        await using (var edit = fixture.CreateContext())
        {
            var location = await edit.Locations.SingleAsync(item => item.UserId == user.Id);
            location.FullAddress = "Manual address";
            await edit.SaveChangesAsync();
        }
        handler.Release();
        var result = await run;

        await using var verify = fixture.CreateContext();
        var saved = await verify.Locations.SingleAsync(item => item.UserId == user.Id);
        Assert.Equal("Manual address", saved.FullAddress);
        Assert.Null(saved.ReverseGeocodingProvider);
        Assert.Equal(0, result.Succeeded);
        Assert.Equal(1, result.Skipped);
    }

    [PostgresFact(Timeout = 30_000)]
    public async Task ExplicitIncompleteRepairFillsOnlyMissingFields()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedIncompleteRepairAsync(user.Id, protection);
        var handler = new CoordinatedHandler(user.Id, null);
        var run = Service(protection, handler).RunAsync(user.Id);
        await handler.FirstUserRequestEntered.WaitAsync(TimeSpan.FromSeconds(10));
        handler.Release();

        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        await using var verify = fixture.CreateContext();
        var saved = await verify.Locations.SingleAsync(item => item.UserId == user.Id);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal("Keep this address", saved.Address);
        Assert.Equal("Alexandroupolis", saved.Place);
        Assert.Equal("Greece", saved.Country);
        Assert.Empty(await verify.LocationEnrichmentAttempts.Where(item => item.UserId == user.Id).ToListAsync());
    }

    [PostgresFact(Timeout = 30_000)]
    public async Task LocalityFreeRepairIsTerminalAndDoesNotContactAgain()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedIncompleteRepairAsync(user.Id, protection);
        var firstHandler = new CoordinatedHandler(user.Id, null, includeLocality: false);
        var firstRun = Service(protection, firstHandler).RunAsync(user.Id);
        await firstHandler.FirstUserRequestEntered.WaitAsync(TimeSpan.FromSeconds(10));
        firstHandler.Release();

        var first = await firstRun.WaitAsync(TimeSpan.FromSeconds(10));
        var secondHandler = new CoordinatedHandler(user.Id, null, includeLocality: false);
        var second = await Service(protection, secondHandler).RunAsync(user.Id)
            .WaitAsync(TimeSpan.FromSeconds(10));

        await using var verify = fixture.CreateContext();
        var attempt = await verify.LocationEnrichmentAttempts.SingleAsync(item => item.UserId == user.Id);
        Assert.Equal(1, first.NoResult);
        Assert.Equal(0, first.Skipped);
        Assert.Equal(LocationEnrichmentOutcome.NoResult, attempt.Outcome);
        Assert.Equal(0, secondHandler.RequestsFor(user.Id));
        Assert.Equal(0, second.Admitted);
        var status = await RepairStatusAsync(verify, user.Id);
        var progress = await new LocationEnrichmentProgressQuery(verify).ProjectAsync(user.Id, status.Binding, DateTime.UtcNow);
        Assert.Equal(1, progress.RepairsWithoutLocality);
        Assert.Equal((0, 0), (progress.RunnableRemaining, progress.FutureDue));
    }

    [PostgresFact(Timeout = 30_000)]
    public async Task ScheduledPoisonRowDoesNotStarveLaterCandidate()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        int laterId;
        int epoch;
        await using (var setup = fixture.CreateContext())
        {
            var oldest = await setup.Locations.SingleAsync(item => item.UserId == user.Id);
            oldest.Timestamp = DateTime.UtcNow.AddMinutes(-2);
            var later = new Location
            {
                UserId = user.Id, Timestamp = DateTime.UtcNow.AddMinutes(-1), LocalTimestamp = DateTime.UtcNow,
                TimeZoneId = "UTC", Coordinates = new Point(21, 11) { SRID = 4326 }
            };
            setup.Add(later);
            var workflow = LocationEnrichmentWorkflow.Create(user.Id, DateTime.UtcNow);
            workflow.Start(DateTime.UtcNow);
            epoch = workflow.Epoch;
            Assert.True(workflow.TryClaim(epoch, DateTime.UtcNow));
            setup.Add(workflow);
            var profile = await setup.PersonalLocationProviderProfiles.SingleAsync(
                item => item.UserId == user.Id && item.ProviderKey == "geoapify");
            setup.Add(new LocationEnrichmentAttempt
            {
                UserId = user.Id, Location = oldest, ProviderKey = "geoapify", CredentialGeneration = 2,
                ConfigurationGeneration = 1, SelectionGeneration = 1,
                ProviderProfileId = profile.Id, Capability = PersonalProviderCapability.Geocoding,
                Verification = profile.GeocodingVerification,
                VerificationCredentialGeneration = profile.GeocodingVerifiedCredentialGeneration,
                VerificationGeneration = profile.GeocodingVerifiedConfigurationGeneration,
                Outcome = LocationEnrichmentOutcome.NoResult, AdmittedAttemptCount = 1,
                LastAttemptAtUtc = DateTime.UtcNow.AddMinutes(-1)
            });
            await setup.SaveChangesAsync();
            laterId = later.Id;
        }
        var handler = new CoordinatedHandler(user.Id, null);
        await using var run = fixture.CreateContext();
        var task = Service(run, protection, handler).RunAsync(user.Id, epoch);
        var entered = await Task.WhenAny(handler.FirstUserRequestEntered, task);
        if (entered == task) await task;
        await handler.FirstUserRequestEntered;
        handler.Release();
        var result = await task;

        await using var verify = fixture.CreateContext();
        Assert.Equal("geoapify", (await verify.Locations.SingleAsync(item => item.Id == laterId)).ReverseGeocodingProvider);
        Assert.Single(await verify.LocationEnrichmentAttempts.Where(item => item.UserId == user.Id).ToListAsync());
        Assert.Equal(0, result.RemainingEstimate);
    }

    [PostgresFact(Timeout = 30_000)]
    public async Task ScheduledTransientFailurePersistsGenerationBoundAttemptAndAdmission()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        int epoch;
        await using (var setup = fixture.CreateContext())
        {
            var workflow = LocationEnrichmentWorkflow.Create(user.Id, DateTime.UtcNow);
            workflow.Start(DateTime.UtcNow); epoch = workflow.Epoch;
            Assert.True(workflow.TryClaim(epoch, DateTime.UtcNow));
            setup.Add(workflow); await setup.SaveChangesAsync();
        }
        var handler = new CoordinatedHandler(user.Id, null, ContactOutcome.ProviderFailure);
        await using var run = fixture.CreateContext();
        var task = Service(run, protection, handler).RunAsync(user.Id, epoch);
        await handler.FirstUserRequestEntered; handler.Release(); var result = await task;

        await using var verify = fixture.CreateContext();
        var attempt = await verify.LocationEnrichmentAttempts.SingleAsync(item => item.UserId == user.Id);
        Assert.Equal(LocationEnrichmentOutcome.RetryableFailure, attempt.Outcome);
        Assert.Equal(1, attempt.AdmittedAttemptCount);
        Assert.Equal("geoapify", attempt.ProviderKey);
        Assert.NotNull(attempt.NextAttemptAtUtc);
        Assert.Equal(1, result.RemainingEstimate);
        Assert.Equal(attempt.NextAttemptAtUtc, result.NextEligibleAt?.UtcDateTime);
        Assert.Single(await verify.GeoapifyUsageAdmissions.Where(item => item.UserId == user.Id).ToListAsync());
    }

    /// <summary>Proves cancellation before durable ownership/admission has no provider cost.</summary>
    [PostgresFact]
    public async Task CancellationBeforeAdmissionCostsNothing()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        var handler = new CoordinatedHandler(user.Id, null);
        await using var db = fixture.CreateContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service(db, protection, handler).RunAsync(user.Id, cancellation.Token));

        await using var verify = fixture.CreateContext();
        Assert.Equal(0, handler.RequestsFor(user.Id));
        Assert.Equal(0, await verify.Set<GeoapifyUsageAdmission>().CountAsync(item => item.UserId == user.Id));
    }

    /// <summary>Proves admitted timeout and provider failure remain charged and release ownership for retry.</summary>
    [PostgresFact]
    public async Task AdmittedFailuresRetainAdmissionAndDeferImmediateRetry()
    {
        foreach (var outcome in new[] { ContactOutcome.Timeout, ContactOutcome.ProviderFailure })
        {
            var user = await fixture.CreateUserAsync();
            var protection = new EphemeralDataProtectionProvider();
            await SeedAsync(user.Id, null, protection);
            var failedHandler = new CoordinatedHandler(user.Id, null, outcome);
            await using var failedDb = fixture.CreateContext();
            var failedRun = Service(failedDb, protection, failedHandler).RunAsync(user.Id);
            await failedHandler.FirstUserRequestEntered;
            failedHandler.Release();
            var failure = await failedRun;
            Assert.Equal(1, failure.Unavailable);

            var retryHandler = new CoordinatedHandler(user.Id, null);
            await using var retryDb = fixture.CreateContext();
            await Service(retryDb, protection, retryHandler).RunAsync(user.Id);

            await using var verify = fixture.CreateContext();
            Assert.Single(await verify.Set<GeoapifyUsageAdmission>().Where(item => item.UserId == user.Id).ToListAsync());
            Assert.Equal(0, retryHandler.RequestsFor(user.Id));
            Assert.True(GeoapifyLocationBackfillService.IsWhollyUnenriched(
                await verify.Locations.SingleAsync(item => item.UserId == user.Id)));
        }
    }

    /// <summary>Proves cancellation after contact retains admission and releases durable ownership.</summary>
    [PostgresFact]
    public async Task CancellationAfterContactRetainsAdmissionAndDefersRetry()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        var cancelledHandler = new CoordinatedHandler(user.Id, null);
        await using var cancelledDb = fixture.CreateContext();
        var cancelledService = Service(cancelledDb, protection, cancelledHandler);
        using var cancellation = new CancellationTokenSource();

        var cancelledRun = cancelledService.RunAsync(user.Id, cancellation.Token);
        await cancelledHandler.FirstUserRequestEntered;
        await using (var duringContact = fixture.CreateContext())
            Assert.Equal(1, await duringContact.Set<GeoapifyUsageAdmission>()
                .CountAsync(item => item.UserId == user.Id));
        cancellation.Cancel();
        var cancelled = await cancelledRun;
        Assert.Equal(1, cancelled.Unavailable);

        await using (var verify = fixture.CreateContext())
        {
            Assert.Equal(1, await verify.Set<GeoapifyUsageAdmission>()
                .CountAsync(item => item.UserId == user.Id));
            var location = await verify.Locations.SingleAsync(item => item.UserId == user.Id);
            Assert.True(GeoapifyLocationBackfillService.IsWhollyUnenriched(location));
            Assert.Equal(1, handlerRequests(cancelledHandler, user.Id));
        }

        var retryHandler = new CoordinatedHandler(user.Id, null);
        await using var retryDb = fixture.CreateContext();
        await Service(retryDb, protection, retryHandler).RunAsync(user.Id);

        await using var final = fixture.CreateContext();
        Assert.Single(await final.Set<GeoapifyUsageAdmission>().Where(item => item.UserId == user.Id).ToListAsync());
        Assert.Equal(0, retryHandler.RequestsFor(user.Id));
        Assert.True(GeoapifyLocationBackfillService.IsWhollyUnenriched(
            await final.Locations.SingleAsync(item => item.UserId == user.Id)));

        static int handlerRequests(CoordinatedHandler handler, string userId) => handler.RequestsFor(userId);
    }

    [PostgresFact]
    public async Task ConcurrentSameUserInvocationsContactOnceWhileAnotherUserRemainsIndependent()
    {
        var user = await fixture.CreateUserAsync();
        var other = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, other.Id, protection);
        var handler = new CoordinatedHandler(user.Id, other.Id);
        await using var firstDb = fixture.CreateContext();
        await using var secondDb = fixture.CreateContext();
        await using var otherDb = fixture.CreateContext();
        var first = Service(firstDb, protection, handler);
        var second = Service(secondDb, protection, handler);
        var independent = Service(otherDb, protection, handler);

        var firstRun = first.RunAsync(user.Id);
        Task<GeoapifyBackfillResult>? secondRun = null;
        Task<GeoapifyBackfillResult>? otherRun = null;
        try
        {
            await handler.FirstUserRequestEntered;
            await using (var duringContact = fixture.CreateContext())
                Assert.Equal(1, await duringContact.Set<GeoapifyUsageAdmission>()
                    .CountAsync(item => item.UserId == user.Id));
            secondRun = second.RunAsync(user.Id);
            otherRun = independent.RunAsync(other.Id);
            await handler.OtherUserRequestEntered;
            await secondRun;
            Assert.Equal(1, handler.RequestsFor(user.Id));
        }
        finally
        {
            handler.Release();
        }
        await Task.WhenAll(firstRun, secondRun!, otherRun!);

        await using var verify = fixture.CreateContext();
        var sameUserAdmissions = await verify.GeoapifyUsageAdmissions
            .Where(item => item.UserId == user.Id).ToListAsync();
        var admission = Assert.Single(sameUserAdmissions);
        Assert.Equal(1, admission.Credits);
        Assert.Equal(PersonalProviderProduct.Geocoding, admission.Product);
        Assert.Equal(1, handler.RequestsFor(user.Id));
        Assert.Equal(1, await verify.Locations.CountAsync(item => item.UserId == user.Id && item.ReverseGeocodingProvider == "geoapify"));
        Assert.Equal(1, handler.RequestsFor(other.Id));
    }

}
