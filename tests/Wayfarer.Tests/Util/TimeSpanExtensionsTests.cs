using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>
/// Tests for <see cref="TimeSpanExtensions"/> utility methods.
/// </summary>
public class TimeSpanExtensionsTests
{
    [Fact]
    public void FormatDuration_ReturnsMinutes_WhenLessThanOneHour()
    {
        // Arrange
        TimeSpan? duration = TimeSpan.FromMinutes(45);

        // Act
        var result = duration.FormatDuration();

        // Assert
        Assert.Equal("45 min", result);
    }

    [Fact]
    public void FormatDuration_ReturnsHours_WhenOneHourOrMore()
    {
        // Arrange
        TimeSpan? duration = TimeSpan.FromHours(2);

        // Act
        var result = duration.FormatDuration();

        // Assert
        Assert.Equal("2 h", result);
    }

    [Fact]
    public void FormatDuration_ReturnsZeroMinutes_WhenNull()
    {
        // Arrange
        TimeSpan? duration = null;

        // Act
        var result = duration.FormatDuration();

        // Assert
        Assert.Equal("0 min", result);
    }

    [Fact]
    public void FormatDuration_ReturnsZeroMinutes_WhenZero()
    {
        // Arrange
        TimeSpan? duration = TimeSpan.Zero;

        // Act
        var result = duration.FormatDuration();

        // Assert
        Assert.Equal("0 min", result);
    }

    [Fact]
    public void FormatDuration_TruncatesMinutes()
    {
        // Arrange - 30.5 minutes should show as 30 min
        TimeSpan? duration = TimeSpan.FromMinutes(30.5);

        // Act
        var result = duration.FormatDuration();

        // Assert
        Assert.Equal("30 min", result);
    }

    [Fact]
    public void FormatDuration_TruncatesHours()
    {
        // Arrange - 2.5 hours should show as 2 h
        TimeSpan? duration = TimeSpan.FromHours(2.5);

        // Act
        var result = duration.FormatDuration();

        // Assert
        Assert.Equal("2 h", result);
    }

    [Fact]
    public void FormatDuration_ReturnsMinutes_AtExactly59Minutes()
    {
        // Arrange
        TimeSpan? duration = TimeSpan.FromMinutes(59);

        // Act
        var result = duration.FormatDuration();

        // Assert
        Assert.Equal("59 min", result);
    }

    [Fact]
    public void FormatDuration_ReturnsHours_AtExactly60Minutes()
    {
        // Arrange
        TimeSpan? duration = TimeSpan.FromMinutes(60);

        // Act
        var result = duration.FormatDuration();

        // Assert
        Assert.Equal("1 h", result);
    }

    [Fact]
    public void FormatDuration_HandlesLargeDurations()
    {
        // Arrange - 100 hours
        TimeSpan? duration = TimeSpan.FromHours(100);

        // Act
        var result = duration.FormatDuration();

        // Assert
        Assert.Equal("100 h", result);
    }

    [Fact]
    public void FormatDuration_HandlesSingleMinute()
    {
        // Arrange
        TimeSpan? duration = TimeSpan.FromMinutes(1);

        // Act
        var result = duration.FormatDuration();

        // Assert
        Assert.Equal("1 min", result);
    }
}
