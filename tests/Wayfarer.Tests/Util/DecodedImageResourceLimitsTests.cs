using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>Defines every independent fixed decoded-resource boundary.</summary>
public sealed class DecodedImageResourceLimitsTests
{
    /// <summary>Each authority accepts its exact fixed limit when the other values remain valid.</summary>
    [Theory]
    [InlineData(8192L, 1L, 1L, 4L, 1L)]
    [InlineData(1L, 8192L, 1L, 4L, 1L)]
    [InlineData(1L, 1L, 12_000_000L, 4L, 1L)]
    [InlineData(1L, 1L, 1L, 67_108_864L, 1L)]
    [InlineData(1L, 1L, 1L, 4L, 1L)]
    public void EvaluateCalculated_AcceptsExactBoundaries(
        long width, long height, long pixels, long decodedBytes, long frameCount)
    {
        var result = DecodedImageResourceLimits.EvaluateCalculated(
            width, height, pixels, decodedBytes, frameCount);

        Assert.Equal(DecodedImageResourceDecision.Accepted, result.Decision);
    }

    /// <summary>Each authority rejects one unit over its fixed limit.</summary>
    [Theory]
    [InlineData(8193L, 1L, 1L, 4L, 1L, "width")]
    [InlineData(1L, 8193L, 1L, 4L, 1L, "height")]
    [InlineData(1L, 1L, 12_000_001L, 4L, 1L, "pixels")]
    [InlineData(1L, 1L, 1L, 67_108_865L, 1L, "decoded-bytes")]
    [InlineData(1L, 1L, 1L, 4L, 2L, "frame-count")]
    public void EvaluateCalculated_RejectsOneOverBoundaries(
        long width, long height, long pixels, long decodedBytes, long frameCount, string expectedLimit)
    {
        var result = DecodedImageResourceLimits.EvaluateCalculated(
            width, height, pixels, decodedBytes, frameCount);

        Assert.Equal(DecodedImageResourceDecision.TooLarge, result.Decision);
        Assert.Equal(expectedLimit, result.LimitName);
    }

    /// <summary>Checked resource arithmetic overflow is a positive policy rejection.</summary>
    [Fact]
    public void Evaluate_RejectsArithmeticOverflow()
    {
        var result = DecodedImageResourceLimits.Evaluate(long.MaxValue, long.MaxValue, 1);

        Assert.Equal(DecodedImageResourceDecision.TooLarge, result.Decision);
        Assert.Equal("resource-arithmetic", result.LimitName);
    }

    /// <summary>Invalid decoder metadata is malformed rather than a positive policy violation.</summary>
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
