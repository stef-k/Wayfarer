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

namespace Wayfarer.Tests.Services;

/// <summary>Proves final atomic persistence and complete operation ownership against PostgreSQL.</summary>
public sealed partial class GeoapifyBackfillConcurrencyPostgresTests
{
    private sealed class LeaseInspectingHandler(PostgresImportTestFixture fixture, string userId) : HttpMessageHandler
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<TimeSpan> remaining = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => entered.Task;
        public Task<TimeSpan> RemainingLease => remaining.Task;
        public void Release() => release.TrySetResult();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await using var db = fixture.CreateContext();
            var now = await db.Database.SqlQuery<DateTime>($"SELECT (clock_timestamp() AT TIME ZONE 'UTC') AS \"Value\"")
                .SingleAsync(cancellationToken);
            var expiry = await db.LocationEnrichmentWorkflows.Where(item => item.UserId == userId)
                .Select(item => item.ExecutionLeaseExpiresAtUtc).SingleAsync(cancellationToken);
            remaining.TrySetResult(expiry!.Value - DateTime.SpecifyKind(now, DateTimeKind.Utc));
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"type":"FeatureCollection","features":[]}""") };
        }
    }

    /// <summary>Proves PostgreSQL evaluates final Location eligibility after a concurrent manual commit.</summary>
    [PostgresFact(Timeout = 30_000)]
    public async Task ManualEditAfterCompletionInspectionWinsConditionalLocationUpdate()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        var updateGate = new LocationUpdateGate();
        var handler = new CoordinatedHandler(user.Id, null);
        var run = Service(protection, handler, updateGate).RunAsync(user.Id);
        await handler.FirstUserRequestEntered.WaitAsync(TimeSpan.FromSeconds(10));
        handler.Release();
        await updateGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        await using (var edit = fixture.CreateContext())
        {
            var location = await edit.Locations.SingleAsync(item => item.UserId == user.Id);
            location.Address = "Manual line";
            location.FullAddress = "Manual address";
            location.ReverseGeocodingProvider = "manual";
            location.ReverseGeocodedAt = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
            await edit.SaveChangesAsync();
        }
        updateGate.Release();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        await using var verify = fixture.CreateContext();
        var saved = await verify.Locations.SingleAsync(item => item.UserId == user.Id);
        Assert.Equal("Manual line", saved.Address);
        Assert.Equal("Manual address", saved.FullAddress);
        Assert.Equal("manual", saved.ReverseGeocodingProvider);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero), saved.ReverseGeocodedAt);
        Assert.Equal(0, result.Succeeded);
        Assert.Equal(1, result.Admitted);
        Assert.Single(await verify.LocationEnrichmentAttempts.Where(item => item.UserId == user.Id).ToListAsync());
    }

    /// <summary>Proves the handler sees a freshly validated database-clock lease margin.</summary>
    [PostgresFact(Timeout = 30_000)]
    public async Task HandlerEntryHasAtLeastMinimumContactLeaseLifetime()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        var handler = new LeaseInspectingHandler(fixture, user.Id);
        var service = Service(protection, handler);
        var run = service.RunAsync(user.Id);

        await handler.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        var remaining = await handler.RemainingLease;
        handler.Release();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(remaining >= TimeSpan.FromSeconds(25), $"Handler lease margin was {remaining}.");
    }

    /// <summary>Proves completion requires every durable operation ownership binding.</summary>
    [PostgresTheory(Timeout = 30_000)]
    [InlineData(OperationMutation.LeaseId)]
    [InlineData(OperationMutation.WorkflowEpoch)]
    [InlineData(OperationMutation.AttemptNumber)]
    [InlineData(OperationMutation.Capability)]
    [InlineData(OperationMutation.VerificationCredential)]
    [InlineData(OperationMutation.VerificationCapability)]
    public async Task CompletionRejectsMismatchedOperationOwnership(OperationMutation mutation)
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        var handler = new CoordinatedHandler(user.Id, null);
        var run = Service(protection, handler).RunAsync(user.Id);
        await handler.FirstUserRequestEntered.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var mutate = fixture.CreateContext())
        {
            var attempt = await mutate.LocationEnrichmentAttempts.SingleAsync(item => item.UserId == user.Id);
            if (mutation == OperationMutation.LeaseId) attempt.OperationLeaseId = Guid.NewGuid();
            else if (mutation == OperationMutation.WorkflowEpoch) attempt.OperationWorkflowEpoch++;
            else if (mutation == OperationMutation.AttemptNumber)
            {
                attempt.OperationAttemptNumber++;
                await Assert.ThrowsAsync<DbUpdateException>(() => mutate.SaveChangesAsync());
                handler.Release(); await run.WaitAsync(TimeSpan.FromSeconds(10)); return;
            }
            else if (mutation == OperationMutation.Capability)
            {
                attempt.Capability = PersonalProviderCapability.Routing;
                await Assert.ThrowsAsync<DbUpdateException>(() => mutate.SaveChangesAsync());
                handler.Release(); await run.WaitAsync(TimeSpan.FromSeconds(10)); return;
            }
            else if (mutation == OperationMutation.VerificationCredential) attempt.VerificationCredentialGeneration++;
            else attempt.VerificationGeneration++;
            await mutate.SaveChangesAsync();
        }
        handler.Release();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        await using var verify = fixture.CreateContext();
        Assert.Equal(0, result.Succeeded);
        Assert.True(GeoapifyLocationBackfillService.IsWhollyUnenriched(
            await verify.Locations.SingleAsync(item => item.UserId == user.Id)));
        Assert.Single(await verify.GeoapifyUsageAdmissions.Where(item => item.UserId == user.Id).ToListAsync());
        Assert.NotNull((await verify.LocationEnrichmentAttempts.SingleAsync(item => item.UserId == user.Id)).OperationId);
    }

    /// <summary>Proves PostgreSQL rejects an active operation missing any durable ownership field.</summary>
    [PostgresFact]
    public async Task PostgreSqlRejectsIncompleteActiveOperation()
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
            Capability = PersonalProviderCapability.Geocoding, CredentialGeneration = 1, ConfigurationGeneration = 1,
            SelectionGeneration = 1, Verification = PersonalProviderVerification.Verified,
            VerificationCredentialGeneration = 1, VerificationGeneration = 1,
            Outcome = LocationEnrichmentOutcome.RetryableFailure, AdmittedAttemptCount = 1,
            LastAttemptAtUtc = DateTime.UtcNow, NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(1),
            OperationId = Guid.NewGuid(), OperationLeaseId = null, OperationFencingGeneration = 1,
            OperationWorkflowEpoch = 1, OperationAttemptNumber = 1, OperationStartedAtUtc = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
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
