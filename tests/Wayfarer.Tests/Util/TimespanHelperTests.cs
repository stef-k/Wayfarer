using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>
/// Validation and parsing for public timeline timespans.
/// </summary>
public class TimespanHelperTests
{
    [Theory]
    [InlineData("1h")]
    [InlineData("1.5d")]
    [InlineData("2w")]
    [InlineData("3m")]
    [InlineData("5y")]
    public void IsValidThreshold_ReturnsTrue_ForAcceptedRanges(string value)
    {
        Assert.True(TimespanHelper.IsValidThreshold(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0h")]
    [InlineData("0.05h")]
    [InlineData("21y")]
    [InlineData("99z")]
    [InlineData("abc")]
    public void IsValidThreshold_ReturnsFalse_ForInvalidInputs(string? value)
    {
        Assert.False(TimespanHelper.IsValidThreshold(value!));
    }

    [Fact]
    public void ParseTimeThreshold_HandlesUnitsAndNow()
    {
        Assert.Equal(TimeSpan.Zero, TimespanHelper.ParseTimeThreshold("now"));
        Assert.Equal(TimeSpan.FromHours(2), TimespanHelper.ParseTimeThreshold("2h"));
        Assert.Equal(TimeSpan.FromDays(3), TimespanHelper.ParseTimeThreshold("3d"));
        Assert.Equal(TimeSpan.FromDays(14), TimespanHelper.ParseTimeThreshold("2w"));
        Assert.Equal(TimeSpan.FromDays(90), TimespanHelper.ParseTimeThreshold("3m"));
        Assert.Equal(TimeSpan.FromDays(365 * 2.5), TimespanHelper.ParseTimeThreshold("2.5y"));
    }

    [Fact]
    public void ParseTimeThreshold_ThrowsForInvalid()
    {
        Assert.Throws<ArgumentException>(() => TimespanHelper.ParseTimeThreshold("bad"));
    }
}
