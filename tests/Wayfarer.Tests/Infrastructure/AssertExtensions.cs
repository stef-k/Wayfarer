using Wayfarer.Models;
using Xunit;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>
/// Provides custom assertion methods for common test validations.
/// </summary>
public static class AssertExtensions
{
    /// <summary>
    /// Asserts that two locations are equal within a specified coordinate tolerance.
    /// </summary>
    /// <param name="expected">The expected location.</param>
    /// <param name="actual">The actual location.</param>
    /// <param name="coordinateTolerance">The allowed difference in coordinates. Defaults to 0.00001.</param>
    public static void LocationEquals(Location expected, Location actual, double coordinateTolerance = 0.00001)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.UserId, actual.UserId);
        Assert.InRange(actual.Coordinates.Y, expected.Coordinates.Y - coordinateTolerance, expected.Coordinates.Y + coordinateTolerance);
        Assert.InRange(actual.Coordinates.X, expected.Coordinates.X - coordinateTolerance, expected.Coordinates.X + coordinateTolerance);
        Assert.Equal(expected.Timestamp, actual.Timestamp);
        Assert.Equal(expected.TimeZoneId, actual.TimeZoneId);
    }

    /// <summary>
    /// Asserts that a string contains valid KML structure.
    /// </summary>
    /// <param name="kml">The KML string to validate.</param>
    public static void IsValidKml(string kml)
    {
        Assert.NotNull(kml);
        Assert.NotEmpty(kml);
        Assert.Contains("<?xml", kml);
        Assert.Contains("<kml", kml);
        Assert.Contains("</kml>", kml);
    }

    /// <summary>
    /// Asserts that a string contains valid GeoJSON structure.
    /// </summary>
    /// <param name="geoJson">The GeoJSON string to validate.</param>
    public static void IsValidGeoJson(string geoJson)
    {
        Assert.NotNull(geoJson);
        Assert.NotEmpty(geoJson);
        Assert.Contains("\"type\"", geoJson);
        Assert.Contains("\"features\"", geoJson);
    }

    /// <summary>
    /// Asserts that a group member has the expected role and status.
    /// </summary>
    /// <param name="member">The group member to check.</param>
    /// <param name="expectedRole">The expected role.</param>
    /// <param name="expectedStatus">The expected status.</param>
    public static void MemberHasRoleAndStatus(GroupMember member, string expectedRole, string expectedStatus)
    {
        Assert.NotNull(member);
        Assert.Equal(expectedRole, member.Role);
        Assert.Equal(expectedStatus, member.Status);
    }

    /// <summary>
    /// Asserts that a trip has the expected number of regions.
    /// </summary>
    /// <param name="trip">The trip to check.</param>
    /// <param name="expectedCount">The expected number of regions.</param>
    public static void TripHasRegionCount(Trip trip, int expectedCount)
    {
        Assert.NotNull(trip);
        Assert.NotNull(trip.Regions);
        Assert.Equal(expectedCount, trip.Regions.Count);
    }

    /// <summary>
    /// Asserts that a region has the expected number of places.
    /// </summary>
    /// <param name="region">The region to check.</param>
    /// <param name="expectedCount">The expected number of places.</param>
    public static void RegionHasPlaceCount(Region region, int expectedCount)
    {
        Assert.NotNull(region);
        Assert.NotNull(region.Places);
        Assert.Equal(expectedCount, region.Places.Count);
    }

    /// <summary>
    /// Asserts that coordinates are within valid ranges.
    /// </summary>
    /// <param name="latitude">The latitude to check (-90 to 90).</param>
    /// <param name="longitude">The longitude to check (-180 to 180).</param>
    public static void CoordinatesAreValid(double latitude, double longitude)
    {
        Assert.InRange(latitude, -90, 90);
        Assert.InRange(longitude, -180, 180);
    }

    /// <summary>
    /// Asserts that a timestamp is within an expected range.
    /// </summary>
    /// <param name="actual">The actual timestamp.</param>
    /// <param name="expected">The expected timestamp.</param>
    /// <param name="tolerance">The allowed time difference. Defaults to 1 second.</param>
    public static void TimestampIsClose(DateTime actual, DateTime expected, TimeSpan? tolerance = null)
    {
        tolerance ??= TimeSpan.FromSeconds(1);
        var difference = (actual - expected).Duration();
        Assert.True(difference <= tolerance,
            $"Timestamp difference {difference} exceeds tolerance {tolerance}. Expected: {expected}, Actual: {actual}");
    }
}
