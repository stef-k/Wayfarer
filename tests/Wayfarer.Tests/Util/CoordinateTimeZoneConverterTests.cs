using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>
/// Coordinate-based timezone conversion behaviors.
/// </summary>
public class CoordinateTimeZoneConverterTests
{
    [Fact]
    public void GetTimeZoneIdFromCoordinates_ReturnsExpectedZone()
    {
        var zoneId = CoordinateTimeZoneConverter.GetTimeZoneIdFromCoordinates(40.7128, -74.0060);

        Assert.Equal("America/New_York", zoneId);
    }

    [Fact]
    public void ConvertToUtc_UsesLocalZoneOffset()
    {
        var local = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Unspecified);

        var utc = CoordinateTimeZoneConverter.ConvertToUtc(64.1466, -21.9426, local); // Reykjavik (UTC, no DST)

        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void ConvertUtcToLocal_AdjustsFromUtcEvenWhenUnspecifiedKind()
    {
        var utc = new DateTime(2024, 1, 15, 17, 0, 0, DateTimeKind.Unspecified);

        var local = CoordinateTimeZoneConverter.ConvertUtcToLocal(40.7128, -74.0060, utc); // New York in standard time

        Assert.Equal(new DateTime(2024, 1, 15, 12, 0, 0), local);
    }
    /// <summary>Servicing timezone data must retain strict rejection of missing or ambiguous local times.</summary>
    [Theory]
    [InlineData(3, 9, 2)]
    [InlineData(11, 2, 1)]
    public void ConvertToUtc_RejectsDstGapAndFold(int month, int day, int hour)
    {
        var local = new DateTime(2025, month, day, hour, 30, 0, DateTimeKind.Unspecified);

        var error = Assert.Throws<InvalidOperationException>(() =>
            CoordinateTimeZoneConverter.ConvertToUtc(40.7128, -74.0060, local));
        Assert.IsAssignableFrom<ArgumentException>(error.InnerException);
    }

}
