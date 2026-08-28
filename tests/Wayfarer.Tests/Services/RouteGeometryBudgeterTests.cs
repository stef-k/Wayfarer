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
        Assert.Equal(Enumerable.Range(0, source.Length), result.SourceIndices);
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

        var outputIndex = Assert.Single(result.SourceIndices.Select((sourceIndex, index) => (sourceIndex, index)),
            item => item.sourceIndex == protectedIndex).index;
        Assert.True(result.WasSimplified);
        Assert.Equal(result.Coordinates.Count, result.SourceIndices.Count);
        Assert.True(result.SourceIndices.Zip(result.SourceIndices.Skip(1), (first, second) => second > first).All(value => value));
        Assert.Equal(CoordinatePair(source[protectedIndex]), CoordinatePair(result.Coordinates[outputIndex]));
    }

    /// <summary>Proves an exact oversized loop retains both endpoint occurrences and a distinct pivot.</summary>
    [Fact]
    public void Budget_ExactClosedLoop_PreservesClosureAndDistinctInterior()
    {
        var source = Enumerable.Range(0, 1_200).Select(index =>
        {
            var angle = index * 2d * Math.PI / 1_200d;
            return new Coordinate(20d + Math.Cos(angle) * 0.1d, 35d + Math.Sin(angle) * 0.1d);
        }).Append(new Coordinate(20.1d, 35d)).ToArray();

        var result = RouteGeometryBudgeter.Budget(source, [0, source.Length - 1], CancellationToken.None);

        Assert.Equal(CoordinatePair(source[0]), CoordinatePair(result.Coordinates[0]));
        Assert.Equal(CoordinatePair(source[^1]), CoordinatePair(result.Coordinates[^1]));
        Assert.True(result.Coordinates.Count >= 3);
        Assert.Contains(result.Coordinates.Skip(1).SkipLast(1), point => CoordinatePair(point) != CoordinatePair(source[0]));
    }

    /// <summary>Proves consecutive duplicates are removable while nonconsecutive ordered positions remain meaningful.</summary>
    [Fact]
    public void Budget_Duplicates_PreservesOrderedRouteSemantics()
    {
        var source = Enumerable.Range(0, 1_100)
            .SelectMany(index => index == 500
                ? new[] { new Coordinate(0.05d, 10.01d), new Coordinate(0.05d, 10.01d) }
                : new[] { new Coordinate(index * 0.0001d, 10d + (index % 2) * 0.00001d) })
            .Append(new Coordinate(0.05d, 10.01d))
            .ToArray();

        var result = RouteGeometryBudgeter.Budget(source, [0, source.Length - 1], CancellationToken.None);

        Assert.Equal(CoordinatePair(source[0]), CoordinatePair(result.Coordinates[0]));
        Assert.Equal(CoordinatePair(source[^1]), CoordinatePair(result.Coordinates[^1]));
        Assert.True(result.Coordinates.Count <= RouteGeometryBudgeter.MaximumPersistedCoordinates);
    }

    /// <summary>Proves spherical budgeting supports antimeridian crossings at high latitude.</summary>
    [Fact]
    public void Budget_AntimeridianHighLatitudeRoute_RemainsBounded()
    {
        var source = Enumerable.Range(0, 1_501).Select(index =>
        {
            var longitude = 179.5d + index * 0.001d;
            if (longitude > 180d) longitude -= 360d;
            return new Coordinate(longitude, 82d + Math.Sin(index / 10d) * 0.00001d);
        }).ToArray();

        var result = RouteGeometryBudgeter.Budget(source, [0, source.Length - 1], CancellationToken.None);

        Assert.InRange(result.Coordinates.Count, 2, RouteGeometryBudgeter.MaximumPersistedCoordinates);
        Assert.InRange(result.MaximumDeviationMetres, 0d, RouteGeometryBudgeter.MaximumDeviationMetres);
    }

    /// <summary>Proves the valid two-position minimum remains unchanged.</summary>
    [Fact]
    public void Budget_TwoPointRoute_RemainsExact()
    {
        var source = new[] { new Coordinate(-1d, 1d), new Coordinate(1d, 2d) };

        var result = RouteGeometryBudgeter.Budget(source, [0, 1], CancellationToken.None);

        Assert.Equal(source.Select(CoordinatePair), result.Coordinates.Select(CoordinatePair));
        Assert.False(result.WasSimplified);
    }

    /// <summary>Proves coordinates close to mathematical antipodes remain valid route endpoints.</summary>
    [Theory]
    [InlineData(0d, 0d, 179.999999d, 0d)]
    [InlineData(170d, 12d, -10.000001d, -12d)]
    [InlineData(40d, 90d, -140d, -89.999999d)]
    public void Budget_NearAntipodalRoute_RemainsExact(
        double firstLongitude,
        double firstLatitude,
        double secondLongitude,
        double secondLatitude)
    {
        var source = new[]
        {
            new Coordinate(firstLongitude, firstLatitude),
            new Coordinate(secondLongitude, secondLatitude)
        };

        var result = RouteGeometryBudgeter.Budget(source, [0, 1], CancellationToken.None);

        Assert.Equal(source.Select(CoordinatePair), result.Coordinates.Select(CoordinatePair));
        Assert.False(result.WasSimplified);
    }

    /// <summary>Proves mathematical antipodes reject, including opposite poles with arbitrary longitudes.</summary>
    [Theory]
    [InlineData(0d, 0d, 180d, 0d)]
    [InlineData(170d, 12d, -10d, -12d)]
    [InlineData(40d, 90d, 75d, -90d)]
    public void Budget_ExactlyAntipodalRoute_Rejects(
        double firstLongitude,
        double firstLatitude,
        double secondLongitude,
        double secondLatitude)
    {
        var source = new[]
        {
            new Coordinate(firstLongitude, firstLatitude),
            new Coordinate(secondLongitude, secondLatitude)
        };

        var error = Assert.Throws<RouteGeometryBudgetException>(() =>
            RouteGeometryBudgeter.Budget(source, [0, 1], CancellationToken.None));

        Assert.Equal("generic_kml_invalid_coordinate", error.Code);
    }

    /// <summary>Proves zero-length and adjacent antipodal routes reject with the stable coordinate code.</summary>
    [Theory]
    [MemberData(nameof(PathologicalRoutes))]
    public void Budget_PathologicalRoute_Rejects(IReadOnlyList<Coordinate> source)
    {
        var error = Assert.Throws<RouteGeometryBudgetException>(() =>
            RouteGeometryBudgeter.Budget(source, [0, source.Count - 1], CancellationToken.None));

        Assert.Equal("generic_kml_invalid_coordinate", error.Code);
    }

    /// <summary>Proves protected geometry rejects rather than exceeding the persisted vertex limit.</summary>
    [Fact]
    public void Budget_ImpossibleProtectedCount_Rejects()
    {
        var source = Enumerable.Range(0, 1_002)
            .Select(index => new Coordinate(index * 0.001d, 20d + (index % 2) * 0.001d))
            .ToArray();

        var error = Assert.Throws<RouteGeometryBudgetException>(() =>
            RouteGeometryBudgeter.Budget(source, Enumerable.Range(0, source.Length).ToArray(), CancellationToken.None));

        Assert.Equal("generic_kml_geometry_budget_unsatisfied", error.Code);
    }

    /// <summary>Proves the document operation ceiling rejects before one extra evaluation is accepted.</summary>
    [Fact]
    public void Work_OperationBudgetExhausted_Rejects()
    {
        var work = new RouteGeometryBudgetWork(RouteGeometryBudgeter.MaximumEvaluations);

        var error = Assert.Throws<RouteGeometryBudgetException>(() =>
            work.RecordEvaluation(CancellationToken.None));

        Assert.Equal("generic_kml_processing_limit", error.Code);
        Assert.Equal(RouteGeometryBudgeter.MaximumEvaluations + 1, work.Evaluations);
    }

    /// <summary>Proves cancellation is observed on the prescribed 1,024th evaluation.</summary>
    [Fact]
    public void Work_AtCancellationCadence_PropagatesCancellation()
    {
        var work = new RouteGeometryBudgetWork(1_023);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => work.RecordEvaluation(cancellation.Token));
        Assert.Equal(1_024, work.Evaluations);
    }

    /// <summary>Proves representative ordered-route shapes remain within fixed count and fidelity bounds.</summary>
    [Theory]
    [MemberData(nameof(RepresentativeRoutes))]
    public void Budget_RepresentativeRouteShape_StaysWithinHardBounds(IReadOnlyList<Coordinate> source)
    {
        var result = RouteGeometryBudgeter.Budget(source, [0, source.Count - 1], CancellationToken.None);

        Assert.InRange(result.Coordinates.Count, 2, RouteGeometryBudgeter.MaximumPersistedCoordinates);
        Assert.InRange(result.MaximumDeviationMetres, 0d, RouteGeometryBudgeter.MaximumDeviationMetres);
        Assert.Equal(CoordinatePair(source[0]), CoordinatePair(result.Coordinates[0]));
        Assert.Equal(CoordinatePair(source[^1]), CoordinatePair(result.Coordinates[^1]));
    }

    /// <summary>Provides deterministic zero-length and antipodal-adjacent invalid routes.</summary>
    public static TheoryData<IReadOnlyList<Coordinate>> PathologicalRoutes => new()
    {
        new[] { new Coordinate(1d, 2d), new Coordinate(1d, 2d) },
        new[] { new Coordinate(0d, 0d), new Coordinate(180d, 0d) }
    };

    /// <summary>Provides straight, curved, sharp-turn, jitter, backtracking, and self-crossing oversized routes.</summary>
    public static TheoryData<IReadOnlyList<Coordinate>> RepresentativeRoutes => new()
    {
        Enumerable.Range(0, 1_201).Select(index => new Coordinate(index * 0.0001d, 30d)).ToArray(),
        Enumerable.Range(0, 1_201).Select(index => new Coordinate(index * 0.0001d, 30d + Math.Sin(index / 100d) * 0.01d)).ToArray(),
        Enumerable.Range(0, 1_201).Select(index => index < 600
            ? new Coordinate(index * 0.0001d, 30d)
            : new Coordinate(0.06d, 30d + (index - 600) * 0.0001d)).ToArray(),
        Enumerable.Range(0, 1_201).Select(index => new Coordinate(index * 0.0001d, 30d + (index % 2) * 0.00001d)).ToArray(),
        Enumerable.Range(0, 1_201).Select(index => new Coordinate(index * 0.00005d + Math.Sin(index / 20d) * 0.00002d, 30d)).ToArray(),
        Enumerable.Range(0, 1_201).Select(index =>
        {
            var angle = index * 4d * Math.PI / 1_200d;
            return new Coordinate(0.02d + Math.Sin(angle) * 0.01d, 30d + Math.Sin(angle) * Math.Cos(angle) * 0.01d);
        }).ToArray()
    };

    private static (double Longitude, double Latitude) CoordinatePair(Coordinate coordinate) =>
        (coordinate.X, coordinate.Y);
}
