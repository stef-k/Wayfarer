using Wayfarer.Services.ExternalRouting;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Defines the deterministic production contract for provider-scoped attempt pacing.</summary>
public sealed class RoutingProviderPacerTests
{
    [Fact]
    public void ProductionAuthorityExposesBoundedProviderScopedContract()
    {
        Assert.Equal(32, RoutingProviderPacer.MaximumQueuedWaiters);
        Assert.Equal(TimeSpan.FromSeconds(120), RoutingProviderPacer.MaximumWait);
        Assert.Equal(TimeSpan.FromMinutes(5), RoutingProviderPacer.MinimumIdleLifetime);
    }

    [Theory]
    [InlineData("ExactIntervalSpacing")]
    [InlineData("FifoOrdering")]
    [InlineData("ZeroIntervalAtomicTurn")]
    [InlineData("DifferentProvidersIndependent")]
    [InlineData("QueueLimitAndThirtyThirdRejection")]
    [InlineData("HeadCancellationAdvancesQueue")]
    [InlineData("NonHeadCancellationRemovesWaiter")]
    [InlineData("PacingWaitTimeout")]
    [InlineData("RetryRejoinsQueue")]
    [InlineData("IntervalIncreaseReevaluatesHead")]
    [InlineData("IntervalDecreaseWakesHead")]
    [InlineData("StaleVersionUpdateRejected")]
    [InlineData("LastAttemptStartPreserved")]
    [InlineData("AtomicIdleCleanup")]
    [InlineData("NoDuplicateLiveGate")]
    [InlineData("NoResourceLeaks")]
    public async Task DeterministicScenarioIsOwnedByProductionPacer(string scenario)
    {
        var time = new RoutingPacingTestTimeProvider();
        var pacer = new RoutingProviderPacer(time);

        var result = await pacer.RunContractScenarioAsync(scenario, CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorCode);
    }

    private sealed class RoutingPacingTestTimeProvider : TimeProvider
    {
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => 0;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
