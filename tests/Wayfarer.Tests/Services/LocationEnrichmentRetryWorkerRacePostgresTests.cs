using Moq;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

public sealed partial class LocationEnrichmentRetryAtomicityPostgresTests
{
    /// <summary>Proves a committed Running lease makes a concurrent Retry mutation-free.</summary>
    [PostgresFact(Timeout = 20_000)]
    public async Task WorkerRunningCommitFirstMakesConcurrentRetryMutationFree()
    {
        fixture.RequireAvailable();
        var scenario = await SeedAsync(LocationEnrichmentState.Scheduled, LocationEnrichmentOutcome.NoResult);
        var workerGate = new WorkflowLockGate(true);
        var retryGate = new WorkflowLockGate(false);
        var authority = new LocationEnrichmentExecutionAuthority(new InterceptedFactory(fixture, workerGate));
        var worker = authority.TryAcquireAsync(scenario.UserId, 1);
        await workerGate.Locked.WaitAsync(TimeSpan.FromSeconds(10));
        await using var retryDb = fixture.CreateContext(retryGate);
        var retry = Command(scenario, retryDb).RetryDeferredAsync(scenario.UserId);
        await retryGate.Attempted.WaitAsync(TimeSpan.FromSeconds(10));
        workerGate.Release();

        var lease = await worker.WaitAsync(TimeSpan.FromSeconds(10));
        var result = await retry.WaitAsync(TimeSpan.FromSeconds(10));
        var after = await SnapshotAsync(scenario.UserId);

        Assert.True(lease.HasValue);
        Assert.Equal("invalid-state", result.Code);
        Assert.Equal(LocationEnrichmentState.Running, after.State);
        Assert.Equal(1, after.Epoch);
        Assert.Equal(lease.Value.LeaseId, after.ExecutionLeaseId);
        Assert.Equal(LocationEnrichmentOutcome.NoResult, after.AttemptOutcome);
        scenario.Projection.Verify(x => x.ProjectAsync(It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Proves a stale epoch cannot claim after Retry creates the sole current epoch.</summary>
    [PostgresFact(Timeout = 20_000)]
    public async Task RetryCommitFirstLetsWorkerClaimOnlyRetryEpoch()
    {
        fixture.RequireAvailable();
        var scenario = await SeedAsync(LocationEnrichmentState.Completed, LocationEnrichmentOutcome.NoResult);
        var retry = await Command(scenario).RetryDeferredAsync(scenario.UserId);
        var authority = new LocationEnrichmentExecutionAuthority(new InterceptedFactory(fixture));

        var stale = await authority.TryAcquireAsync(scenario.UserId, 1);
        var current = await authority.TryAcquireAsync(scenario.UserId, 2);
        var after = await SnapshotAsync(scenario.UserId);

        Assert.Equal("scheduled", retry.Code);
        Assert.Null(stale);
        Assert.True(current.HasValue);
        Assert.Equal(LocationEnrichmentState.Running, after.State);
        Assert.Equal(2, after.Epoch);
        Assert.Equal(current.Value.LeaseId, after.ExecutionLeaseId);
        Assert.Equal(LocationEnrichmentOutcome.None, after.AttemptOutcome);
        Assert.Equal(0, after.AdmittedAttemptCount);
        scenario.Projection.Verify(x => x.ProjectAsync(scenario.UserId,
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
