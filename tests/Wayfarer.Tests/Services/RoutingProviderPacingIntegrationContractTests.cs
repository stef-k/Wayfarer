using Wayfarer.Areas.Admin.Models;
using Wayfarer.Models;
using Wayfarer.Services.ExternalRouting;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Defines production-facing persistence, Admin, admission, and timeout contracts.</summary>
public sealed class RoutingProviderPacingIntegrationContractTests
{
    [Fact]
    public void ConfigurationDefaultsToOneSecond()
    {
        var provider = new RoutingProviderConfiguration();
        Assert.Equal(1000, provider.MinimumIntervalMilliseconds);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("0.0", 0)]
    [InlineData("1.0", 1000)]
    [InlineData("1.1", 1100)]
    [InlineData("60.0", 60000)]
    public void AdminConverterAcceptsExactAsciiTenths(string value, int milliseconds)
    {
        Assert.True(RoutingMinimumIntervalConverter.TryParse(value, out var parsed));
        Assert.Equal(milliseconds, parsed);
        Assert.Equal($"{milliseconds / 1000}.{milliseconds % 1000 / 100}",
            RoutingMinimumIntervalConverter.Format(milliseconds));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1,1")]
    [InlineData("+1.0")]
    [InlineData("-0.1")]
    [InlineData("1e0")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1 .0")]
    [InlineData("1.01")]
    [InlineData("60.1")]
    public void AdminConverterRejectsNonContractInput(string value) =>
        Assert.False(RoutingMinimumIntervalConverter.TryParse(value, out _));

    [Fact]
    public void AdminModelIsStringBackedAndDefaultsToOneDecimalSecond()
    {
        var model = new RoutingProviderEditViewModel();
        Assert.Equal("1.0", model.MinimumIntervalSeconds);
    }

    [Fact]
    public async Task FailedFinalAuthorityReleasesConcurrencyWithoutRecordingAttempt()
    {
        var provider = Provider(0);
        var budget = new RoutingRequestBudget();
        var coordinator = new RoutingAttemptCoordinator(
            new RoutingProviderPacer(TimeProvider.System), budget);

        var rejected = await coordinator.PrepareAsync(
            provider, _ => Task.FromResult(false), CancellationToken.None);
        using (var recovered = await budget.AcquireAttemptConcurrencyAsync(provider.Id, 1, CancellationToken.None))
            Assert.NotNull(recovered);
        using var nextPacingTurn = await coordinator.PrepareAsync(
            provider, _ => Task.FromResult(true), CancellationToken.None);

        Assert.Equal("provider-configuration-stale", rejected.ErrorCode);
        Assert.True(nextPacingTurn.Succeeded);
        Assert.True(budget.TryAdmitProviderAttempt(provider.Id, 1));
    }

    [Fact]
    public async Task CancellationBeforeTurnPerformsNoAuthorityOrRateAdmission()
    {
        var provider = Provider(0);
        var budget = new RoutingRequestBudget();
        var pacer = new RoutingProviderPacer(TimeProvider.System);
        pacer.ApplyConfiguration(provider.Id, provider.ConfigurationVersion, 0);
        var owner = await pacer.WaitAsync(provider.Id, provider.ConfigurationVersion, CancellationToken.None);
        var coordinator = new RoutingAttemptCoordinator(pacer, budget);
        var validations = 0;
        using var cancellation = new CancellationTokenSource();
        var waiting = coordinator.PrepareAsync(provider, _ =>
        {
            validations++;
            return Task.FromResult(true);
        }, cancellation.Token);
        cancellation.Cancel();

        Assert.Equal("request-cancelled", (await waiting).ErrorCode);
        Assert.Equal(0, validations);
        Assert.True(budget.TryAdmitProviderAttempt(provider.Id, 1));
        owner.Turn!.Dispose();
    }

    [Fact]
    public async Task PreparedAttemptRetainsPacingTurnUntilExecutorCanStartSynchronously()
    {
        var provider = Provider(0);
        var budget = new RoutingRequestBudget();
        var pacer = new RoutingProviderPacer(TimeProvider.System);
        var coordinator = new RoutingAttemptCoordinator(pacer, budget);
        var prepared = await coordinator.PrepareAsync(provider, _ => Task.FromResult(true), CancellationToken.None);

        var following = coordinator.PrepareAsync(provider, _ => Task.FromResult(true), CancellationToken.None);

        Assert.False(following.IsCompleted);
        prepared.Dispose();
        (await following).Dispose();
    }

    private static RoutingProviderConfiguration Provider(int interval) => new()
    {
        Id = Guid.NewGuid(), ConfigurationVersion = 1, MinimumIntervalMilliseconds = interval,
        RequestsPerMinute = 1, MaxConcurrency = 1
    };

}
