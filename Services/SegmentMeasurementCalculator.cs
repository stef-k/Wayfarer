using NetTopologySuite.Geometries;

namespace Wayfarer.Services;

/// <summary>Contains the deterministic numerical policy for authoritative segment measurements.</summary>
public static class SegmentMeasurementCalculator
{
    /// <summary>Earth radius in metres used for every Haversine pair.</summary>
    public const double EarthRadiusMetres = 6_371_000d;

    /// <summary>Calculates unrounded metres and the authoritative rounded kilometre value.</summary>
    public static SegmentDistanceMeasurement CalculateDistance(IReadOnlyList<Coordinate> coordinates)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        if (coordinates.Count < 2)
            throw new ArgumentException("A complete route requires at least two coordinates.", nameof(coordinates));

        var metres = 0d;
        for (var index = 0; index < coordinates.Count; index++)
            ValidateCoordinate(coordinates[index], index);
        for (var index = 1; index < coordinates.Count; index++)
        {
            metres += HaversineMetres(coordinates[index - 1], coordinates[index]);
            if (!double.IsFinite(metres) || metres < 0)
                throw new ArgumentOutOfRangeException(nameof(coordinates), "Route distance must be finite and non-negative.");
        }

        var kilometres = Math.Round(metres / 1000d, 3, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(kilometres))
            throw new ArgumentOutOfRangeException(nameof(coordinates), "Rounded route distance must be finite.");
        return new(metres, kilometres);
    }

    /// <summary>Calculates a whole-second Automatic duration from unrounded metres and canonical speed.</summary>
    public static TimeSpan CalculateAutomaticDuration(double unroundedMetres, double planningSpeedKmh)
    {
        if (!double.IsFinite(unroundedMetres) || unroundedMetres < 0)
            throw new ArgumentOutOfRangeException(nameof(unroundedMetres), "Distance must be finite and non-negative.");
        if (!double.IsFinite(planningSpeedKmh) || planningSpeedKmh <= 0)
            throw new ArgumentOutOfRangeException(nameof(planningSpeedKmh), "Planning speed must be finite and positive.");

        return FromValidatedSeconds(unroundedMetres / (planningSpeedKmh * 1000d / 3600d));
    }

    /// <summary>Normalizes submitted Manual minutes to a whole-second non-negative duration.</summary>
    public static TimeSpan NormalizeManualDuration(double minutes)
    {
        if (!double.IsFinite(minutes) || minutes < 0)
            throw new ArgumentOutOfRangeException(nameof(minutes), "Manual duration must be finite and non-negative.");
        return FromValidatedSeconds(minutes * 60d);
    }

    private static TimeSpan FromValidatedSeconds(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Duration must be finite and non-negative.");
        var wholeSeconds = Math.Round(seconds, 0, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(wholeSeconds) || wholeSeconds > TimeSpan.MaxValue.TotalSeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Duration exceeds the TimeSpan range.");
        return TimeSpan.FromSeconds(wholeSeconds);
    }

    private static void ValidateCoordinate(Coordinate coordinate, int index)
    {
        if (!double.IsFinite(coordinate.X) || !double.IsFinite(coordinate.Y)
            || coordinate.X is < -180d or > 180d || coordinate.Y is < -90d or > 90d)
            throw new ArgumentOutOfRangeException(nameof(coordinate), $"Route coordinate {index} is outside finite longitude/latitude bounds.");
    }

    private static double HaversineMetres(Coordinate from, Coordinate to)
    {
        var latitude1 = DegreesToRadians(from.Y);
        var latitude2 = DegreesToRadians(to.Y);
        var latitudeDelta = latitude2 - latitude1;
        var longitudeDelta = DegreesToRadians(to.X - from.X);
        var latitudeTerm = Math.Sin(latitudeDelta / 2d);
        var longitudeTerm = Math.Sin(longitudeDelta / 2d);
        var a = latitudeTerm * latitudeTerm
            + Math.Cos(latitude1) * Math.Cos(latitude2) * longitudeTerm * longitudeTerm;
        var centralAngle = 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
        return EarthRadiusMetres * centralAngle;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}

/// <summary>Returns transient unrounded metres with the rounded persisted kilometre value.</summary>
/// <param name="UnroundedMetres">Unrounded Haversine accumulation used for duration.</param>
/// <param name="RoundedKilometres">Kilometres rounded to three decimals away from zero.</param>
public readonly record struct SegmentDistanceMeasurement(double UnroundedMetres, double RoundedKilometres);
