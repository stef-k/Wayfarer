using Microsoft.EntityFrameworkCore;
using Moq;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves Start and Resume revalidate provider authority inside their locked transaction.</summary>
public sealed partial class LocationEnrichmentRetryAtomicityPostgresTests
{
    [PostgresFact(Timeout = 20_000)]
    public async Task StartAuthorityDriftAfterInspectionCreatesNoWorkflowOrProjection()
    {
        fixture.RequireAvailable();
        var scenario = await SeedAsync(LocationEnrichmentState.Completed, LocationEnrichmentOutcome.NoResult);
        await using (var remove = fixture.CreateContext())
        {
            remove.LocationEnrichmentWorkflows.Remove(
                await remove.LocationEnrichmentWorkflows.SingleAsync(x => x.UserId == scenario.UserId));
            await remove.SaveChangesAsync();
        }
        var gate = new AsyncGate();
        await using var commandDb = fixture.CreateContext();
        var owner = Command(scenario, commandDb);
        owner.BeforeTransactionalAuthorityValidationAsync = gate.BlockAsync;

        var start = owner.StartAsync(scenario.UserId);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await RevokeAuthorityAsync(scenario.UserId);
        gate.Release();
        var result = await start.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("authority-unavailable", result.Code);
        await using var verify = fixture.CreateContext();
        Assert.Equal(0, await verify.LocationEnrichmentWorkflows.CountAsync(x => x.UserId == scenario.UserId));
        scenario.Projection.Verify(x => x.ProjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [PostgresFact(Timeout = 20_000)]
    public async Task ResumeAuthorityDriftAfterInspectionLeavesPausedWorkflowUnchanged()
    {
        fixture.RequireAvailable();
        var scenario = await SeedAsync(LocationEnrichmentState.PausedByUser, LocationEnrichmentOutcome.NoResult);
        var before = await SnapshotAsync(scenario.UserId);
        var gate = new AsyncGate();
        await using var commandDb = fixture.CreateContext();
        var owner = Command(scenario, commandDb);
        owner.BeforeTransactionalAuthorityValidationAsync = gate.BlockAsync;

        var resume = owner.ResumeAsync(scenario.UserId);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await RevokeAuthorityAsync(scenario.UserId);
        gate.Release();
        var result = await resume.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("authority-unavailable", result.Code);
        Assert.Equal(before, await SnapshotAsync(scenario.UserId));
        scenario.Projection.Verify(x => x.ProjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private async Task RevokeAuthorityAsync(string userId)
    {
        await using var drift = fixture.CreateContext();
        var profile = await drift.PersonalLocationProviderProfiles.SingleAsync(x => x.UserId == userId);
        profile.RevokedAt = DateTimeOffset.UtcNow;
        await drift.SaveChangesAsync();
    }

    /// <summary>Deterministically pauses a command between advisory inspection and locked validation.</summary>
    private sealed class AsyncGate
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => entered.Task;
        public void Release() => release.TrySetResult();
        public async Task BlockAsync(CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }
    }
}
