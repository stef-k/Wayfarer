using NetTopologySuite.Geometries;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Defines the fixed production-facing geometry budget for generic KML routes.</summary>
public sealed class RouteGeometryBudgeterTests
{
    /// <summary>Proves routes at or below the trigger retain every original coordinate exactly.</summary>
    [Fact]
    public void Budget_BelowTrigger_PreservesEveryCoordinateExactly()
    {
        var source = Enumerable.Range(0, 1_000)
            .Select(index => new Coordinate(-10d + index * 0.001d, 35d + Math.Sin(index / 20d) * 0.01d))
            .ToArray();

        var result = RouteGeometryBudgeter.Budget(source, [0, source.Length - 1], CancellationToken.None);

        Assert.False(result.WasSimplified);
        Assert.Equal(source.Length, result.Coordinates.Count);
        Assert.Equal(source.Select(CoordinatePair), result.Coordinates.Select(CoordinatePair));
    }

    /// <summary>Proves oversized deterministic input is reduced to the fixed preferred target.</summary>
    [Fact]
    public void Budget_OversizedDeterministicRoute_RequiresBudgeting()
    {
        var source = Enumerable.Range(0, 2_001)
            .Select(index => new Coordinate(index * 0.0001d, 40d))
            .ToArray();

        var result = RouteGeometryBudgeter.Budget(source, [0, source.Length - 1], CancellationToken.None);

        Assert.True(result.WasSimplified);
        Assert.InRange(result.Coordinates.Count, 2, 500);
        Assert.Equal(result.Coordinates.Select(CoordinatePair),
            RouteGeometryBudgeter.Budget(source, [0, source.Length - 1], CancellationToken.None)
                .Coordinates.Select(CoordinatePair));
    }

    /// <summary>Proves budgeting retains the exact source endpoint values.</summary>
    [Fact]
    public void Budget_OversizedRoute_PreservesExactEndpoints()
    {
        var source = Enumerable.Range(0, 1_501)
            .Select(index => new Coordinate(170d + index * 0.001d, 70d + Math.Sin(index / 12d) * 0.00001d))
            .ToArray();

        var result = RouteGeometryBudgeter.Budget(source, [0, source.Length - 1], CancellationToken.None);

        Assert.Equal(CoordinatePair(source[0]), CoordinatePair(result.Coordinates[0]));
        Assert.Equal(CoordinatePair(source[^1]), CoordinatePair(result.Coordinates[^1]));
    }

    /// <summary>Proves invalid, non-finite, and out-of-range source coordinates are rejected.</summary>
    [Theory]
    [InlineData(double.NaN, 0d)]
    [InlineData(double.PositiveInfinity, 0d)]
    [InlineData(181d, 0d)]
    [InlineData(0d, -91d)]
    public void Budget_InvalidCoordinate_Rejects(double longitude, double latitude)
    {
        var source = new[] { new Coordinate(0d, 0d), new Coordinate(longitude, latitude) };

        var error = Assert.Throws<RouteGeometryBudgetException>(() =>
            RouteGeometryBudgeter.Budget(source, [0, 1], CancellationToken.None));

        Assert.Equal("generic_kml_invalid_coordinate", error.Code);
    }

    /// <summary>Proves caller-protected original positions survive deterministic simplification.</summary>
    [Fact]
    public void Budget_ProtectedInteriorIndex_PreservesOriginalCoordinate()
    {
        var source = Enumerable.Range(0, 2_001)
            .Select(index => new Coordinate(index * 0.0001d, 45d))
            .ToArray();
        const int protectedIndex = 777;

        var result = RouteGeometryBudgeter.Budget(
            source, [0, protectedIndex, source.Length - 1], CancellationToken.None);

        Assert.Contains(CoordinatePair(source[protectedIndex]), result.Coordinates.Select(CoordinatePair));
    }

    private static (double Longitude, double Latitude) CoordinatePair(Coordinate coordinate) =>
        (coordinate.X, coordinate.Y);
}
