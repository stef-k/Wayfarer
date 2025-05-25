namespace Wayfarer.Util;

public static class DistanceChecker
{
    private static readonly double EarthRadius = 6_371_000; // metres
    
    /// <summary>
    /// Calculates the distance between two locations
    /// </summary>
    /// <param name="lat1">Location 1 latitude</param>
    /// <param name="lon1">Location 1 longitude </param>
    /// <param name="lat2">Location 2 latitude</param>
    /// <param name="lon2">Location 2 longitude</param>
    /// <returns>Distance between 2 locations in meters</returns>
    public static double HaversineDistance(
        double lat1, double lon1,
        double lat2, double lon2)
    {
        double ToRad(double deg) => deg * (Math.PI / 180);

        double dLat = ToRad(lat2 - lat1);
        double dLon = ToRad(lon2 - lon1);

        double sinDlat = Math.Sin(dLat / 2);
        double sinDlon = Math.Sin(dLon / 2);

        double a = sinDlat * sinDlat
                   + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2))
                                           * sinDlon * sinDlon;

        // guard against floating-point drift
        a = Math.Min(1.0, Math.Max(0.0, a));

        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EarthRadius * c;
    }
}