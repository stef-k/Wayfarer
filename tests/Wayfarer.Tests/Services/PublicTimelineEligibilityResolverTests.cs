using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests the authoritative persisted public-timeline eligibility rules.
/// </summary>
public class PublicTimelineEligibilityResolverTests
{
    [Theory]
    [InlineData("now", true, false, 0)]
    [InlineData("1d", false, true, 24)]
    [InlineData("1.5w", false, true, 252)]
    [InlineData("1D", false, true, 24)]
    public void Resolve_ReturnsValidStoredThresholdState(string threshold, bool expectedLive, bool expectedDelayed, double expectedHours)
    {
        var user = TestDataFixtures.CreateUser();
        user.IsTimelinePublic = true;
        user.PublicTimelineTimeThreshold = threshold;

        var result = PublicTimelineEligibilityResolver.Resolve(user);

        Assert.True(result.IsThresholdValid);
        Assert.True(result.IsEffectivelyPublic);
        Assert.Equal(expectedLive, result.IsLive);
        Assert.Equal(expectedDelayed, !result.IsLive);
        Assert.Equal(TimeSpan.FromHours(expectedHours), result.Delay);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" now")]
    [InlineData("now ")]
    [InlineData("NOW")]
    [InlineData("bad")]
    [InlineData("1z")]
    [InlineData("21y")]
    public void Resolve_FailsClosed_ForInvalidRawStoredThresholds(string? threshold)
    {
        var user = TestDataFixtures.CreateUser();
        user.IsTimelinePublic = true;
        user.PublicTimelineTimeThreshold = threshold;

        var result = PublicTimelineEligibilityResolver.Resolve(user);

        Assert.False(result.IsThresholdValid);
        Assert.False(result.IsEffectivelyPublic);
        Assert.False(result.IsLive);
        Assert.Null(result.Delay);
    }

    [Fact]
    public void Resolve_RejectsPrivateUserWithOtherwiseValidThreshold()
    {
        var user = TestDataFixtures.CreateUser();
        user.IsTimelinePublic = false;
        user.PublicTimelineTimeThreshold = "1d";

        var result = PublicTimelineEligibilityResolver.Resolve(user);

        Assert.True(result.IsThresholdValid);
        Assert.False(result.IsEffectivelyPublic);
    }
}
