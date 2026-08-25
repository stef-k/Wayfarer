using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using NetTopologySuite.Geometries;
using System.Data.Common;
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

    [PostgresFact]
    public async Task TwoConcurrentRetriesResetAndAdvanceExactlyOnce()
    {
        fixture.RequireAvailable();
        var scenario = await SeedAsync(LocationEnrichmentState.Completed, LocationEnrichmentOutcome.AttemptLimit);
        var firstGate = new WorkflowLockGate(blockAfterLock: true);
        var secondGate = new WorkflowLockGate(blockAfterLock: false);
        await using var firstDb = fixture.CreateContext(firstGate);
        await using var secondDb = fixture.CreateContext(secondGate);
        var first = Command(scenario, firstDb).RetryDeferredAsync(scenario.UserId);
        await firstGate.Locked.WaitAsync(TimeSpan.FromSeconds(10));
        var second = Command(scenario, secondDb).RetryDeferredAsync(scenario.UserId);
        await secondGate.Attempted.WaitAsync(TimeSpan.FromSeconds(10));
        firstGate.Release();

        var results = await Task.WhenAll(first, second);
        var after = await SnapshotAsync(scenario.UserId);

        Assert.Single(results, result => result.Code == "scheduled");
        Assert.Single(results, result => result.Code == "invalid-state");
        Assert.Equal(2, after.Epoch);
        Assert.Equal(LocationEnrichmentState.Scheduled, after.State);
        Assert.Equal(LocationEnrichmentOutcome.None, after.AttemptOutcome);
        Assert.Equal(0, after.AdmittedAttemptCount);
        scenario.Projection.Verify(x => x.ProjectAsync(scenario.UserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [PostgresFact]
    public async Task ProjectionFailureOccursAfterCommitAndCommandRetryDoesNotResetAgain()
    {
        fixture.RequireAvailable();
        var scenario = await SeedAsync(LocationEnrichmentState.Completed, LocationEnrichmentOutcome.NoResult);
        scenario.Projection.Setup(x => x.ProjectAsync(scenario.UserId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("test projection failure"));

        var failedProjection = await Command(scenario).RetryDeferredAsync(scenario.UserId);
        var committed = await SnapshotAsync(scenario.UserId);
        var repeated = await Command(scenario).RetryDeferredAsync(scenario.UserId);

        Assert.Equal(LocationEnrichmentCommandResult.SchedulingPending, failedProjection.Classification);
        Assert.Equal("scheduling-reconciliation-required", failedProjection.Code);
        Assert.Equal(LocationEnrichmentState.Scheduled, committed.State);
        Assert.Equal(2, committed.Epoch);
        Assert.Equal(LocationEnrichmentOutcome.None, committed.AttemptOutcome);
        Assert.Equal("invalid-state", repeated.Code);
        Assert.Equal(committed, await SnapshotAsync(scenario.UserId));
        scenario.Projection.Verify(x => x.ProjectAsync(scenario.UserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [PostgresTheory]
    [InlineData("pause", LocationEnrichmentState.PausedByUser)]
    [InlineData("cancel", LocationEnrichmentState.Cancelled)]
    public async Task RetryCommitFirstThenControlCommandOperatesOnScheduledState(
        string command, LocationEnrichmentState expectedState)
    {
        fixture.RequireAvailable();
        var scenario = await SeedAsync(LocationEnrichmentState.Completed, LocationEnrichmentOutcome.NoResult);
        var retryGate = new WorkflowLockGate(blockAfterLock: true);
        var controlGate = new WorkflowLockGate(blockAfterLock: false);
        await using var retryDb = fixture.CreateContext(retryGate);
        await using var controlDb = fixture.CreateContext(controlGate);
        var retry = Command(scenario, retryDb).RetryDeferredAsync(scenario.UserId);
        await retryGate.Locked.WaitAsync(TimeSpan.FromSeconds(10));
        var controlOwner = Command(scenario, controlDb);
        var control = command == "pause"
            ? controlOwner.PauseAsync(scenario.UserId) : controlOwner.CancelAsync(scenario.UserId);
        await controlGate.Attempted.WaitAsync(TimeSpan.FromSeconds(10));
        retryGate.Release();

        var results = await Task.WhenAll(retry, control);
        var after = await SnapshotAsync(scenario.UserId);

        Assert.Equal(["scheduled", command == "pause" ? "paused" : "cancelled"],
            results.Select(result => result.Code));
        Assert.Equal(expectedState, after.State);
        Assert.Equal(3, after.Epoch);
        Assert.Equal(LocationEnrichmentOutcome.None, after.AttemptOutcome);
        Assert.Equal(0, after.AdmittedAttemptCount);
        scenario.Projection.Verify(x => x.ProjectAsync(scenario.UserId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [PostgresFact]
    public async Task CancelCommitFirstMakesRetryMutationFree()
    {
        fixture.RequireAvailable();
        var scenario = await SeedAsync(LocationEnrichmentState.Completed, LocationEnrichmentOutcome.NoResult);
        var cancelGate = new WorkflowLockGate(blockAfterLock: true);
        var retryGate = new WorkflowLockGate(blockAfterLock: false);
        await using var cancelDb = fixture.CreateContext(cancelGate);
        await using var retryDb = fixture.CreateContext(retryGate);
        var cancel = Command(scenario, cancelDb).CancelAsync(scenario.UserId);
        await cancelGate.Locked.WaitAsync(TimeSpan.FromSeconds(10));
        var retry = Command(scenario, retryDb).RetryDeferredAsync(scenario.UserId);
        await retryGate.Attempted.WaitAsync(TimeSpan.FromSeconds(10));
        cancelGate.Release();

        var results = await Task.WhenAll(cancel, retry);
        var after = await SnapshotAsync(scenario.UserId);

        Assert.Equal("cancelled", results[0].Code);
        Assert.Equal("invalid-state", results[1].Code);
        Assert.Equal(LocationEnrichmentState.Cancelled, after.State);
        Assert.Equal(2, after.Epoch);
        Assert.Equal(LocationEnrichmentOutcome.NoResult, after.AttemptOutcome);
        Assert.Equal(1, after.AdmittedAttemptCount);
        scenario.Projection.Verify(x => x.ProjectAsync(scenario.UserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [PostgresFact]
    public async Task AuthorityGenerationDriftBeforeRetryIsMutationFree()
    {
        fixture.RequireAvailable();
        var scenario = await SeedAsync(LocationEnrichmentState.Completed, LocationEnrichmentOutcome.NoResult);
        await using (var drift = fixture.CreateContext())
        {
            var selection = await drift.PersonalLocationProviderSelections.SingleAsync(x => x.UserId == scenario.UserId);
            selection.GeocodingSelectionGeneration++;
            await drift.SaveChangesAsync();
        }
        var before = await SnapshotAsync(scenario.UserId);

        var result = await Command(scenario).RetryDeferredAsync(scenario.UserId);

        Assert.Equal(LocationEnrichmentCommandResult.Conflict, result.Classification);
        Assert.Equal("authority-unavailable", result.Code);
        Assert.Equal(before, await SnapshotAsync(scenario.UserId));
        scenario.Projection.Verify(x => x.ProjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private ImportEnrichmentHandoff Command(Scenario scenario)
    {
        var db = fixture.CreateContext();
        return Command(scenario, db);
    }

    private static ImportEnrichmentHandoff Command(Scenario scenario, ApplicationDbContext db) =>
        new(db, scenario.Projection.Object, scenario.Status.Object, new LocationEnrichmentProgressQuery(db));

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
            db.AddRange(location, workflow,
                new PersonalLocationProviderSelection
                {
                    UserId = user.Id, GeocodingProviderKey = binding.ProviderKey,
                    GeocodingSelectionGeneration = binding.SelectionGeneration
                },
                new PersonalLocationProviderProfile
                {
                    Id = binding.ProfileId!.Value, UserId = user.Id, ProviderKey = binding.ProviderKey,
                    ProtectedCredential = "test-only", CredentialGeneration = binding.CredentialGeneration,
                    GeocodingAuthorized = true, GeocodingGeneration = binding.CapabilityGeneration,
                    GeocodingVerification = binding.Verification,
                    GeocodingVerifiedCredentialGeneration = binding.VerifiedCredentialGeneration,
                    GeocodingVerifiedConfigurationGeneration = binding.VerifiedCapabilityGeneration
                });
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

    /// <summary>Pauses one command after its workflow row lock and observes a competing lock attempt.</summary>
    private sealed class WorkflowLockGate(bool blockAfterLock) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource locked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int observed;
        public Task Locked => locked.Task;
        public Task Attempted => attempted.Task;
        public void Release() => release.TrySetResult();

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            ObserveAttempt(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ObserveAttempt(command);
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (IsWorkflowLock(command) && blockAfterLock && Interlocked.Exchange(ref observed, 1) == 0)
            {
                locked.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }

        private void ObserveAttempt(DbCommand command)
        {
            if (IsWorkflowLock(command)) attempted.TrySetResult();
        }

        private static bool IsWorkflowLock(DbCommand command) =>
            command.CommandText.Contains("LocationEnrichmentWorkflows", StringComparison.Ordinal)
            && command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase);
    }
}
