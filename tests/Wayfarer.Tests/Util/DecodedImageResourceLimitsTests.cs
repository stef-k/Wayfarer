using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>
/// Defines the fixed decoded-resource boundary expected from the image proxy preflight.
/// </summary>
public sealed class DecodedImageResourceLimitsTests
{
    /// <summary>Each independent fixed boundary accepts its exact limit.</summary>
    [Theory]
    [InlineData(8192L, 1L, 1L)]
    [InlineData(1L, 8192L, 1L)]
    [InlineData(4000L, 3000L, 1L)]
    [InlineData(1L, 1L, 8L)]
    [InlineData(2048L, 1024L, 8L)]
    public void Evaluate_AcceptsExactBoundaries(long width, long height, long frameCount)
    {
        var result = DecodedImageResourceLimits.Evaluate(width, height, frameCount);

        Assert.Equal(DecodedImageResourceDecision.Accepted, result.Decision);
    }

    /// <summary>Each independent fixed boundary rejects one unit over its limit.</summary>
    [Theory]
    [InlineData(8193L, 1L, 1L, "width")]
    [InlineData(1L, 8193L, 1L, "height")]
    [InlineData(4001L, 3000L, 1L, "pixels-per-frame")]
    [InlineData(1L, 1L, 9L, "frame-count")]
    [InlineData(2048L, 1025L, 8L, "aggregate-decoded-bytes")]
    public void Evaluate_RejectsOneOverBoundaries(
        long width,
        long height,
        long frameCount,
        string expectedLimit)
    {
        var result = DecodedImageResourceLimits.Evaluate(width, height, frameCount);

        Assert.Equal(DecodedImageResourceDecision.TooLarge, result.Decision);
        Assert.Equal(expectedLimit, result.LimitName);
    }

    /// <summary>Checked resource arithmetic fails closed as a positive policy rejection.</summary>
    [Fact]
    public void Evaluate_RejectsArithmeticOverflow()
    {
        var result = DecodedImageResourceLimits.Evaluate(long.MaxValue, long.MaxValue, long.MaxValue);

        Assert.Equal(DecodedImageResourceDecision.TooLarge, result.Decision);
        Assert.Equal("resource-arithmetic", result.LimitName);
    }

    /// <summary>Invalid decoder metadata is a malformed-input failure, not a policy rejection.</summary>
    [Theory]
    [InlineData(0L, 1L, 1L)]
    [InlineData(1L, 0L, 1L)]
    [InlineData(1L, 1L, 0L)]
    [InlineData(-1L, 1L, 1L)]
    public void Evaluate_FailsInvalidMetadata(long width, long height, long frameCount)
    {
        var result = DecodedImageResourceLimits.Evaluate(width, height, frameCount);

        Assert.Equal(DecodedImageResourceDecision.Failed, result.Decision);
    }
}
