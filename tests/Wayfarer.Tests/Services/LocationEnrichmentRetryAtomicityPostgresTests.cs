using Microsoft.EntityFrameworkCore;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves deferred Retry owns workflow validation and attempt reset atomically in PostgreSQL.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationEnrichmentRetryAtomicityPostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresTheory]
    [InlineData(LocationEnrichmentState.Running)]
    [InlineData(LocationEnrichmentState.Scheduled)]
    public async Task RetryRejectedByActiveStateChangesNoWorkflowOrAttemptField(LocationEnrichmentState state)
    {
        fixture.RequireAvailable();
        var scenario = await SeedAsync(state, LocationEnrichmentOutcome.NoResult);
        var before = await SnapshotAsync(scenario.UserId);

        var result = await Command(scenario).RetryDeferredAsync(scenario.UserId);

        Assert.Equal(LocationEnrichmentCommandResult.Conflict, result.Classification);
        Assert.Equal("invalid-state", result.Code);
        Assert.Equal(before, await SnapshotAsync(scenario.UserId));
        scenario.Projection.Verify(x => x.ProjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [PostgresTheory]
    [InlineData(LocationEnrichmentOutcome.InvalidCoordinates)]
    [InlineData(LocationEnrichmentOutcome.RetryableFailure)]
    public async Task RetryWithNoPolicyEligibleCurrentAuthorityAttemptIsMutationFree(LocationEnrichmentOutcome outcome)
    {
        fixture.RequireAvailable();
        var scenario = await SeedAsync(LocationEnrichmentState.Completed, outcome);
        var before = await SnapshotAsync(scenario.UserId);

        var result = await Command(scenario).RetryDeferredAsync(scenario.UserId);

        Assert.Equal(LocationEnrichmentCommandResult.Applied, result.Classification);
        Assert.Equal("nothing-to-retry", result.Code);
        Assert.Equal(before, await SnapshotAsync(scenario.UserId));
        scenario.Projection.Verify(x => x.ProjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [PostgresFact]
    public async Task ValidRetryResetsEligibleAttemptAndAdvancesWorkflowExactlyOnce()
    {
        fixture.RequireAvailable();
        var scenario = await SeedAsync(LocationEnrichmentState.Completed, LocationEnrichmentOutcome.AttemptLimit);
        var before = await SnapshotAsync(scenario.UserId);

        var result = await Command(scenario).RetryDeferredAsync(scenario.UserId);
        var after = await SnapshotAsync(scenario.UserId);

        Assert.Equal(LocationEnrichmentCommandResult.Applied, result.Classification);
        Assert.Equal("scheduled", result.Code);
        Assert.Equal(before.Epoch + 1, after.Epoch);
        Assert.Equal(LocationEnrichmentState.Scheduled, after.State);
        Assert.True(after.IntentEnabled);
        Assert.Equal(LocationEnrichmentOutcome.None, after.AttemptOutcome);
        Assert.Equal(0, after.AdmittedAttemptCount);
        scenario.Projection.Verify(x => x.ProjectAsync(scenario.UserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    private ImportEnrichmentHandoff Command(Scenario scenario)
    {
        var db = fixture.CreateContext();
        return new ImportEnrichmentHandoff(db, scenario.Projection.Object, scenario.Status.Object,
            new LocationEnrichmentProgressQuery(db));
    }

    private async Task<Scenario> SeedAsync(LocationEnrichmentState state, LocationEnrichmentOutcome outcome)
    {
        var user = await fixture.CreateUserAsync();
        var now = DateTime.UtcNow;
        var binding = new PersonalProviderAuthorityBinding("geoapify", Guid.NewGuid(), 1, 1, 1,
            PersonalProviderVerification.Verified, 1, 1, null, null, null);
        await using (var db = fixture.CreateContext())
        {
            var location = new Wayfarer.Models.Location
            {
                UserId = user.Id, Timestamp = now, LocalTimestamp = now, TimeZoneId = "UTC",
                Coordinates = new Point(23, 37) { SRID = 4326 }
            };
            var workflow = LocationEnrichmentWorkflow.Create(user.Id, now);
            workflow.Start(now);
            if (state == LocationEnrichmentState.Running) Assert.True(workflow.TryClaim(workflow.Epoch, now));
            else if (state == LocationEnrichmentState.Completed)
                workflow.TransitionToTerminal(LocationEnrichmentState.Completed,
                    LocationEnrichmentOutcome.NoCandidates, now);
            db.AddRange(location, workflow);
            await db.SaveChangesAsync();
            db.Add(new LocationEnrichmentAttempt
            {
                UserId = user.Id, LocationId = location.Id, ProviderKey = binding.ProviderKey,
                ProviderProfileId = binding.ProfileId, Capability = PersonalProviderCapability.Geocoding,
                CredentialGeneration = 1, ConfigurationGeneration = 1, SelectionGeneration = 1,
                Verification = PersonalProviderVerification.Verified,
                VerificationCredentialGeneration = 1, VerificationGeneration = 1,
                Outcome = outcome, AdmittedAttemptCount = outcome == LocationEnrichmentOutcome.AttemptLimit ? 3 : 1,
                LastAttemptAtUtc = now.AddMinutes(-1), NextAttemptAtUtc = now.AddHours(1)
            });
            await db.SaveChangesAsync();
        }
        var status = new Mock<IPersonalProviderStatusReader>();
        status.Setup(x => x.InspectPersistentGeocodingAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalProviderInspection(PersonalProviderAdmissionCategory.Admitted,
                "geoapify", true, false, null, new(0, 2500, "credits", null, null), binding, now));
        return new(user.Id, status, new Mock<IWorkflowScheduleProjection>());
    }

    private async Task<Snapshot> SnapshotAsync(string userId)
    {
        await using var db = fixture.CreateContext();
        var workflow = await db.LocationEnrichmentWorkflows.AsNoTracking().SingleAsync(x => x.UserId == userId);
        var attempt = await db.LocationEnrichmentAttempts.AsNoTracking().SingleAsync(x => x.UserId == userId);
        return new(workflow.State, workflow.Epoch, workflow.IntentEnabled, workflow.Outcome,
            workflow.NextEligibleAtUtc, workflow.UpdatedAtUtc, attempt.Outcome, attempt.AdmittedAttemptCount,
            attempt.LastAttemptAtUtc, attempt.NextAttemptAtUtc, attempt.OperationId);
    }

    private sealed record Scenario(string UserId, Mock<IPersonalProviderStatusReader> Status,
        Mock<IWorkflowScheduleProjection> Projection);
    private sealed record Snapshot(LocationEnrichmentState State, int Epoch, bool IntentEnabled,
        LocationEnrichmentOutcome WorkflowOutcome, DateTime? NextEligibleAtUtc, DateTime UpdatedAtUtc,
        LocationEnrichmentOutcome AttemptOutcome, int AdmittedAttemptCount, DateTime LastAttemptAtUtc,
        DateTime? NextAttemptAtUtc, Guid? OperationId);
}
