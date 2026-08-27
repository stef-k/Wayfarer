using Wayfarer.Services.LocationEnrichment;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Defines deterministic retry limits and provider-native wake boundaries.</summary>
public sealed class LocationEnrichmentRetryPolicyTests
{
    [Fact]
    public void GeoapifyWakeUsesOldestStrictlyCountedAdmissionPlusSafetyMargin()
    {
        var now = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var admissions = new[] { now.AddHours(-24), now.AddHours(-23), now.AddHours(-1) };

        var wake = LocationEnrichmentRetryPolicy.GeoapifyWake(now, admissions);

        Assert.Equal(now.AddHours(1).AddSeconds(5), wake);
    }

    [Fact]
    public void MapboxWakeUsesNextWayfarerUtcMonthNotExternalAccountState()
    {
        var now = new DateTimeOffset(2026, 12, 31, 23, 59, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 5, TimeSpan.Zero),
            LocationEnrichmentRetryPolicy.MapboxWake(now));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void AtMostThreeAdmittedAttemptsPerGeneration(int admittedAttempts, bool mayRetry)
        => Assert.Equal(mayRetry, LocationEnrichmentRetryPolicy.MayRetry(admittedAttempts));
}
