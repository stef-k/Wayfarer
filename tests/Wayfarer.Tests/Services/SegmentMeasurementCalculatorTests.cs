using NetTopologySuite.Geometries;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Verifies deterministic distance and duration numerical policy independently of persistence.</summary>
public sealed class SegmentMeasurementCalculatorTests
{
    /// <summary>Uses independently precomputed GeographicLib-style fixtures for difficult spherical routes.</summary>
    [Theory]
    [MemberData(nameof(DifficultRouteFixtures))]
    public void CalculateDistance_DifficultSphericalFixturesMatchIndependentConstants(
        Coordinate[] coordinates,
        double expectedMetres,
        double toleranceMetres)
    {
        var result = SegmentMeasurementCalculator.CalculateDistance(coordinates);

        Assert.InRange(result.UnroundedMetres, expectedMetres - toleranceMetres, expectedMetres + toleranceMetres);
        Assert.True(double.IsFinite(result.UnroundedMetres));
    }

    /// <summary>Independent expected distances, not calculated through production code.</summary>
    public static TheoryData<Coordinate[], double, double> DifficultRouteFixtures => new()
    {
        { [new(179.9, 0), new(-179.9, 0)], 22_238.985, 0.01 },
        { [new(0, 89.9), new(90, 89.9)], 15_725.333, 0.02 },
        { [new(0, 0), new(179.999999, 0)], 20_015_086.796, 0.05 },
        { [new(23.7, 37.9), new(23.7 + 1e-12, 37.9 + 1e-12)], 0.000000142, 0.00000001 },
        { [new(0, 0), new(1, 0), new(1, 1)], 222_389.853, 0.01 }
    };

    /// <summary>Proves longitude/latitude Haversine distance across every consecutive pair.</summary>
    [Fact]
    public void CalculateDistance_SumsEveryConsecutiveLongitudeLatitudePair()
    {
        var result = SegmentMeasurementCalculator.CalculateDistance([
            new Coordinate(0, 0),
            new Coordinate(1, 0),
            new Coordinate(1, 1)
        ]);

        Assert.Equal(222_389.853, result.UnroundedMetres, 3);
        Assert.Equal(222.390, result.RoundedKilometres);
    }

    /// <summary>Proves non-equatorial longitude distance uses latitude in radians.</summary>
    [Fact]
    public void CalculateDistance_NonEquatorialFixtureUsesCorrectAxisOrder()
    {
        var result = SegmentMeasurementCalculator.CalculateDistance([
            new Coordinate(23.7275, 37.9838),
            new Coordinate(23.7375, 37.9838)
        ]);

        Assert.InRange(result.UnroundedMetres, 876.0, 878.0);
    }

    /// <summary>Proves a complete zero-length route is measured as zero rather than unavailable.</summary>
    [Fact]
    public void CalculateDistance_IdenticalCoordinatesReturnsZero()
    {
        var result = SegmentMeasurementCalculator.CalculateDistance([
            new Coordinate(10, 20),
            new Coordinate(10, 20)
        ]);

        Assert.Equal(0d, result.UnroundedMetres);
        Assert.Equal(0d, result.RoundedKilometres);
    }

    /// <summary>Proves Automatic duration uses unrounded metres and rounds seconds away from zero.</summary>
    [Theory]
    [InlineData(1.49, 3.6, 1)]
    [InlineData(1.50, 3.6, 2)]
    [InlineData(1.51, 3.6, 2)]
    public void CalculateAutomaticDuration_RoundsWholeSecondsAwayFromZero(double metres, double speedKmh, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds),
            SegmentMeasurementCalculator.CalculateAutomaticDuration(metres, speedKmh));
    }

    /// <summary>Proves Manual minutes round once to whole seconds and accept explicit zero.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.0248333333333333, 1)]
    [InlineData(0.025, 2)]
    public void NormalizeManualDuration_RoundsWholeSecondsAwayFromZero(double minutes, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds),
            SegmentMeasurementCalculator.NormalizeManualDuration(minutes));
    }

    /// <summary>Proves invalid finite/range inputs are rejected rather than persisted.</summary>
    [Fact]
    public void Calculator_RejectsNonFiniteNegativeAndOverflowingInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SegmentMeasurementCalculator.CalculateDistance([new Coordinate(double.NaN, 0), new Coordinate(0, 0)]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SegmentMeasurementCalculator.CalculateAutomaticDuration(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SegmentMeasurementCalculator.CalculateAutomaticDuration(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SegmentMeasurementCalculator.NormalizeManualDuration(double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SegmentMeasurementCalculator.NormalizeManualDuration(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SegmentMeasurementCalculator.CalculateAutomaticDuration(double.MaxValue, double.Epsilon));
    }
}
