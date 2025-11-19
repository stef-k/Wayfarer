using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>
/// Tests for <see cref="DateTimeUtils"/> utility methods.
/// </summary>
public class DateTimeUtilsTests
{
    [Fact]
    public void ConvertUtcToLocalTime_ReturnsCorrectTime_ForNewYork()
    {
        // Arrange - January (no DST) 12:00 UTC
        var utcTime = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);

        // Act
        var result = DateTimeUtils.ConvertUtcToLocalTime(utcTime, "America/New_York");

        // Assert - Should be 7:00 AM (UTC-5)
        Assert.Equal(7, result.Hour);
    }

    [Fact]
    public void ConvertUtcToLocalTime_ReturnsCorrectTime_ForLondon()
    {
        // Arrange - January (no DST) 12:00 UTC
        var utcTime = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);

        // Act
        var result = DateTimeUtils.ConvertUtcToLocalTime(utcTime, "Europe/London");

        // Assert - Should be 12:00 (UTC+0)
        Assert.Equal(12, result.Hour);
    }

    [Fact]
    public void ConvertUtcToLocalTime_ReturnsCorrectTime_ForTokyo()
    {
        // Arrange - 12:00 UTC
        var utcTime = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);

        // Act
        var result = DateTimeUtils.ConvertUtcToLocalTime(utcTime, "Asia/Tokyo");

        // Assert - Should be 21:00 (UTC+9)
        Assert.Equal(21, result.Hour);
    }

    [Fact]
    public void ConvertUtcToLocalTime_HandlesDateChange()
    {
        // Arrange - 23:00 UTC
        var utcTime = new DateTime(2024, 1, 15, 23, 0, 0, DateTimeKind.Utc);

        // Act
        var result = DateTimeUtils.ConvertUtcToLocalTime(utcTime, "Asia/Tokyo");

        // Assert - Should be next day 8:00 AM (UTC+9)
        Assert.Equal(16, result.Day);
        Assert.Equal(8, result.Hour);
    }

    [Fact]
    public void ConvertUtcToLocalTime_FallsBackToUtc_ForInvalidTimeZone()
    {
        // Arrange
        var utcTime = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);

        // Act
        var result = DateTimeUtils.ConvertUtcToLocalTime(utcTime, "Invalid/Timezone");

        // Assert - Should return original UTC time
        Assert.Equal(12, result.Hour);
    }

    [Fact]
    public void ConvertUtcToLocalTime_HandlesNonUtcInput()
    {
        // Arrange - DateTime without explicit Kind
        var unspecifiedTime = new DateTime(2024, 1, 15, 12, 0, 0);

        // Act
        var result = DateTimeUtils.ConvertUtcToLocalTime(unspecifiedTime, "Europe/Athens");

        // Assert - Should still convert (Athens is UTC+2)
        Assert.Equal(14, result.Hour);
    }

    [Fact]
    public void ConvertUtcToLocalTime_ReturnsCorrectTime_ForAthens()
    {
        // Arrange - January (no DST) 12:00 UTC
        var utcTime = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);

        // Act
        var result = DateTimeUtils.ConvertUtcToLocalTime(utcTime, "Europe/Athens");

        // Assert - Should be 14:00 (UTC+2)
        Assert.Equal(14, result.Hour);
    }

    [Fact]
    public void ConvertUtcToLocalTime_HandlesNegativeOffset()
    {
        // Arrange - 5:00 UTC
        var utcTime = new DateTime(2024, 1, 15, 5, 0, 0, DateTimeKind.Utc);

        // Act
        var result = DateTimeUtils.ConvertUtcToLocalTime(utcTime, "America/Los_Angeles");

        // Assert - Should be previous day 21:00 (UTC-8)
        Assert.Equal(14, result.Day);
        Assert.Equal(21, result.Hour);
    }

    [Fact]
    public void ConvertUtcToLocalTime_PreservesMinutesAndSeconds()
    {
        // Arrange
        var utcTime = new DateTime(2024, 1, 15, 12, 30, 45, DateTimeKind.Utc);

        // Act
        var result = DateTimeUtils.ConvertUtcToLocalTime(utcTime, "Europe/Athens");

        // Assert
        Assert.Equal(30, result.Minute);
        Assert.Equal(45, result.Second);
    }

    [Fact]
    public void ConvertUtcToLocalTime_HandlesAustralianTimezone()
    {
        // Arrange - 12:00 UTC
        var utcTime = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);

        // Act
        var result = DateTimeUtils.ConvertUtcToLocalTime(utcTime, "Australia/Sydney");

        // Assert - Should be 23:00 (UTC+11 during summer DST)
        Assert.Equal(23, result.Hour);
    }

    [Fact]
    public void ConvertUtcToLocalTime_HandlesMidnight()
    {
        // Arrange
        var utcTime = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var result = DateTimeUtils.ConvertUtcToLocalTime(utcTime, "Europe/Athens");

        // Assert - Should be 2:00 AM
        Assert.Equal(2, result.Hour);
    }

    [Fact]
    public void ConvertUtcToLocalTime_HandlesIndiaTimezone()
    {
        // Arrange - India is UTC+5:30 (half hour offset)
        var utcTime = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);

        // Act
        var result = DateTimeUtils.ConvertUtcToLocalTime(utcTime, "Asia/Kolkata");

        // Assert - Should be 17:30 (UTC+5:30)
        Assert.Equal(17, result.Hour);
        Assert.Equal(30, result.Minute);
    }
}
