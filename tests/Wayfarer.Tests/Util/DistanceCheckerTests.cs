using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>
/// Tests for <see cref="DistanceChecker"/> utility methods.
/// </summary>
public class DistanceCheckerTests
{
    [Fact]
    public void HaversineDistance_ReturnsZero_WhenSameLocation()
    {
        // Arrange
        double lat = 40.7128;
        double lon = -74.0060;

        // Act
        var result = DistanceChecker.HaversineDistance(lat, lon, lat, lon);

        // Assert
        Assert.Equal(0, result, 0.001);
    }

    [Fact]
    public void HaversineDistance_CalculatesCorrectly_ForKnownDistance()
    {
        // Arrange - New York to Los Angeles (approx 3944 km)
        double nyLat = 40.7128;
        double nyLon = -74.0060;
        double laLat = 34.0522;
        double laLon = -118.2437;

        // Act
        var result = DistanceChecker.HaversineDistance(nyLat, nyLon, laLat, laLon);

        // Assert - Should be approximately 3944 km (3,944,000 meters)
        Assert.InRange(result, 3_935_000, 3_955_000);
    }

    [Fact]
    public void HaversineDistance_CalculatesCorrectly_ForShortDistance()
    {
        // Arrange - Two points ~1km apart in Athens, Greece
        double lat1 = 37.9838;
        double lon1 = 23.7275;
        double lat2 = 37.9928;
        double lon2 = 23.7275;

        // Act
        var result = DistanceChecker.HaversineDistance(lat1, lon1, lat2, lon2);

        // Assert - Should be approximately 1000 meters
        Assert.InRange(result, 990, 1010);
    }

    [Fact]
    public void HaversineDistance_HandlesNegativeLatitudes()
    {
        // Arrange - Sydney, Australia to Auckland, New Zealand
        double sydLat = -33.8688;
        double sydLon = 151.2093;
        double aklLat = -36.8485;
        double aklLon = 174.7633;

        // Act
        var result = DistanceChecker.HaversineDistance(sydLat, sydLon, aklLat, aklLon);

        // Assert - Should be approximately 2155 km
        Assert.InRange(result, 2_145_000, 2_165_000);
    }

    [Fact]
    public void HaversineDistance_HandlesAntipodalPoints()
    {
        // Arrange - Points on opposite sides of Earth
        double lat1 = 0;
        double lon1 = 0;
        double lat2 = 0;
        double lon2 = 180;

        // Act
        var result = DistanceChecker.HaversineDistance(lat1, lon1, lat2, lon2);

        // Assert - Half circumference of Earth ~20,015 km
        Assert.InRange(result, 20_000_000, 20_040_000);
    }

    [Fact]
    public void HaversineDistance_HandlesNorthToSouthPole()
    {
        // Arrange - North Pole to South Pole
        double northLat = 90;
        double northLon = 0;
        double southLat = -90;
        double southLon = 0;

        // Act
        var result = DistanceChecker.HaversineDistance(northLat, northLon, southLat, southLon);

        // Assert - Half circumference of Earth ~20,015 km
        Assert.InRange(result, 20_000_000, 20_040_000);
    }

    [Fact]
    public void HaversineDistance_IsSymmetric()
    {
        // Arrange
        double lat1 = 51.5074;
        double lon1 = -0.1278;
        double lat2 = 48.8566;
        double lon2 = 2.3522;

        // Act
        var result1 = DistanceChecker.HaversineDistance(lat1, lon1, lat2, lon2);
        var result2 = DistanceChecker.HaversineDistance(lat2, lon2, lat1, lon1);

        // Assert
        Assert.Equal(result1, result2, 0.001);
    }

    [Fact]
    public void HaversineDistance_CalculatesCorrectly_LondonToParis()
    {
        // Arrange - London to Paris (approx 344 km)
        double londonLat = 51.5074;
        double londonLon = -0.1278;
        double parisLat = 48.8566;
        double parisLon = 2.3522;

        // Act
        var result = DistanceChecker.HaversineDistance(londonLat, londonLon, parisLat, parisLon);

        // Assert - Should be approximately 344 km
        Assert.InRange(result, 340_000, 348_000);
    }

    [Fact]
    public void HaversineDistance_CalculatesCorrectly_CrossingInternationalDateLine()
    {
        // Arrange - Points crossing the 180 meridian
        double lat1 = 0;
        double lon1 = 179;
        double lat2 = 0;
        double lon2 = -179;

        // Act
        var result = DistanceChecker.HaversineDistance(lat1, lon1, lat2, lon2);

        // Assert - Should be about 222 km (2 degrees at equator)
        Assert.InRange(result, 218_000, 226_000);
    }

    [Fact]
    public void HaversineDistance_CalculatesCorrectly_OnEquator()
    {
        // Arrange - Two points on equator, 1 degree apart
        double lat1 = 0;
        double lon1 = 0;
        double lat2 = 0;
        double lon2 = 1;

        // Act
        var result = DistanceChecker.HaversineDistance(lat1, lon1, lat2, lon2);

        // Assert - 1 degree at equator ~111 km
        Assert.InRange(result, 110_000, 112_000);
    }

    [Fact]
    public void HaversineDistance_CalculatesCorrectly_AtHighLatitude()
    {
        // Arrange - Two points at 60°N, 1 degree longitude apart
        double lat1 = 60;
        double lon1 = 0;
        double lat2 = 60;
        double lon2 = 1;

        // Act
        var result = DistanceChecker.HaversineDistance(lat1, lon1, lat2, lon2);

        // Assert - 1 degree longitude at 60°N ~55.8 km (half of equator)
        Assert.InRange(result, 54_000, 57_000);
    }

    [Fact]
    public void HaversineDistance_HandlesVerySmallDistances()
    {
        // Arrange - Points only meters apart
        double lat1 = 40.7128;
        double lon1 = -74.0060;
        double lat2 = 40.71289; // About 10m north
        double lon2 = -74.0060;

        // Act
        var result = DistanceChecker.HaversineDistance(lat1, lon1, lat2, lon2);

        // Assert - Should be approximately 10 meters
        Assert.InRange(result, 8, 12);
    }
}
