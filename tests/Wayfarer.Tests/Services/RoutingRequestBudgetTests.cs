using Wayfarer.Services.ExternalRouting;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Verifies fixed user and bounded provider request/concurrency budgets.</summary>
public sealed class RoutingRequestBudgetTests
{
    [Fact]
    public async Task UserGenerationBudget_AllowsFivePerMinuteAndRecoversAfterWindow()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-18T12:00:00Z"));
        var budget = new RoutingRequestBudget(time);
        for (var index = 0; index < 5; index++)
            (await budget.AcquireAsync("user", Guid.NewGuid(), 60, 4, CancellationToken.None))!.Dispose();

        var rejected = await budget.AcquireAsync("user", Guid.NewGuid(), 60, 4, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        var recovered = await budget.AcquireAsync("user", Guid.NewGuid(), 60, 4, CancellationToken.None);

        Assert.Null(rejected);
        Assert.NotNull(recovered);
        recovered!.Dispose();
    }

    [Fact]
    public async Task ProviderConcurrency_IsIndependentAndReleasedByLease()
    {
        var budget = new RoutingRequestBudget();
        var provider = Guid.NewGuid();
        using var first = await budget.AcquireAsync("first", provider, 60, 1, CancellationToken.None);

        var rejected = await budget.AcquireAsync("second", provider, 60, 1, CancellationToken.None);
        first!.Dispose();
        using var recovered = await budget.AcquireAsync("second", provider, 60, 1, CancellationToken.None);

        Assert.Null(rejected);
        Assert.NotNull(recovered);
    }

    [Fact]
    public async Task RetryAttemptsShareProviderRequestBudget()
    {
        var budget = new RoutingRequestBudget();
        using var lease = await budget.AcquireAsync("user", Guid.NewGuid(), 1, 1, CancellationToken.None);

        Assert.True(lease!.TryAdmitProviderAttempt());
        Assert.False(lease.TryAdmitProviderAttempt());
    }

    [Fact]
    public async Task ProviderCapacityChangesKeepOneGateWithoutOverlappingOwnership()
    {
        var budget = new RoutingRequestBudget();
        var provider = Guid.NewGuid();
        using var first = await budget.AcquireAsync("first", provider, 60, 2, CancellationToken.None);
        using var second = await budget.AcquireAsync("second", provider, 60, 2, CancellationToken.None);

        Assert.Null(await budget.AcquireAsync("third", provider, 60, 1, CancellationToken.None));
        first!.Dispose();
        Assert.Null(await budget.AcquireAsync("third", provider, 60, 1, CancellationToken.None));
        second!.Dispose();
        using var reduced = await budget.AcquireAsync("third", provider, 60, 1, CancellationToken.None);
        Assert.NotNull(reduced);
        using var increased = await budget.AcquireAsync("fourth", provider, 60, 2, CancellationToken.None);
        Assert.NotNull(increased);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
