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

    [Theory]
    [InlineData("GenerationTotalTimeout", 300)]
    [InlineData("VerificationTotalTimeout", 600)]
    [InlineData("ConcurrencyNotHeldDuringPacing", 0)]
    [InlineData("GlobalThenProviderConcurrency", 0)]
    [InlineData("OneRateAdmissionPerAttempt", 0)]
    [InlineData("DnsAndSendFailuresAdvancePacing", 0)]
    [InlineData("ResponseFailureAddsNoStart", 0)]
    [InlineData("RetryPacedAndReadmitted", 0)]
    [InlineData("CancellationBeforeStartHasNoContact", 0)]
    [InlineData("SharedGenerationVerificationQueue", 0)]
    [InlineData("VerificationProfilesShareQueue", 0)]
    [InlineData("FinalPreContactStaleRejection", 0)]
    public void AttemptBoundaryContractIsExposed(string contract, int seconds) =>
        Assert.True(RoutingAttemptContract.Supports(contract, seconds));
}
