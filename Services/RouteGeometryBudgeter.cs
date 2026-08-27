using NetTopologySuite.Geometries;

namespace Wayfarer.Services;

/// <summary>Applies the fixed validation, fidelity, work, and vertex budgets for generic route geometry.</summary>
public static class RouteGeometryBudgeter
{
    /// <summary>Maximum source positions accepted for one generic route.</summary>
    public const int MaximumInputCoordinates = 100_000;
    /// <summary>Coordinate count above which simplification is required.</summary>
    public const int SimplificationTrigger = 1_000;
    /// <summary>Preferred persisted coordinate count for one generic route.</summary>
    public const int PreferredCoordinates = 500;
    /// <summary>Hard persisted coordinate limit for one generic route.</summary>
    public const int MaximumPersistedCoordinates = 1_000;
    /// <summary>Maximum permitted simplification deviation in metres.</summary>
    public const double MaximumDeviationMetres = 25d;
    /// <summary>Maximum point-to-segment evaluations across one generic document.</summary>
    public const long MaximumEvaluations = 5_000_000;

    private const double EarthRadiusMetres = 6_371_000d;

    /// <summary>Budgets one route with a fresh operation counter.</summary>
    public static RouteGeometryBudgetResult Budget(
        IReadOnlyList<Coordinate> coordinates,
        IReadOnlyCollection<int> protectedIndices,
        CancellationToken cancellationToken) =>
        Budget(coordinates, protectedIndices, new RouteGeometryBudgetWork(), cancellationToken);

    /// <summary>Budgets one route while sharing document-wide simplification work accounting.</summary>
    public static RouteGeometryBudgetResult Budget(
        IReadOnlyList<Coordinate> coordinates,
        IReadOnlyCollection<int> protectedIndices,
        RouteGeometryBudgetWork work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        ArgumentNullException.ThrowIfNull(protectedIndices);
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSource(coordinates, protectedIndices);

        if (coordinates.Count <= SimplificationTrigger)
            return new(coordinates.Select(Copy).ToArray(), false, 0d, 0d, coordinates.Count,
                Enumerable.Range(0, coordinates.Count).ToArray());

        var prepared = Prepare(coordinates, protectedIndices);
        var first = Simplify(prepared, 1d, work, cancellationToken);
        Candidate selected = first;
        if (first.Coordinates.Count > PreferredCoordinates)
        {
            Candidate? best = null;
            var low = 100;
            var high = 2_500;
            for (var iteration = 0; iteration < 12 && low <= high; iteration++)
            {
                var midpoint = low + ((high - low) / 2);
                var candidate = Simplify(prepared, midpoint / 100d, work, cancellationToken);
                if (candidate.Coordinates.Count <= PreferredCoordinates)
                {
                    best = candidate;
                    high = midpoint - 1;
                }
                else
                {
                    low = midpoint + 1;
                }
            }
            selected = best ?? Simplify(prepared, MaximumDeviationMetres, work, cancellationToken);
        }

        var deviation = MeasureDeviation(prepared.Coordinates, selected.OriginalIndices, work, cancellationToken);
        if (selected.Coordinates.Count > MaximumPersistedCoordinates || deviation > MaximumDeviationMetres + 1e-9)
            throw new RouteGeometryBudgetException(
                "generic_kml_geometry_budget_unsatisfied",
                "A route cannot be reduced safely to the supported size.");
        var retainedSourceIndices = selected.OriginalIndices.Select(index => prepared.OriginalIndices[index]).ToArray();
        Revalidate(coordinates, protectedIndices, selected, retainedSourceIndices, deviation);
        return new(selected.Coordinates.Select(Copy).ToArray(), true, selected.ToleranceMetres, deviation,
            coordinates.Count, retainedSourceIndices);
    }

    private static void ValidateSource(IReadOnlyList<Coordinate> coordinates, IReadOnlyCollection<int> protectedIndices)
    {
        if (coordinates.Count > MaximumInputCoordinates)
            throw new RouteGeometryBudgetException(
                "generic_kml_linestring_input_limit",
                "A route contains more than 100,000 coordinates.");
        if (coordinates.Count < 2 || protectedIndices.Any(index => index < 0 || index >= coordinates.Count))
            throw InvalidCoordinate();
        var hasDistinctPosition = false;
        for (var index = 0; index < coordinates.Count; index++)
        {
            var coordinate = coordinates[index];
            if (!double.IsFinite(coordinate.X) || !double.IsFinite(coordinate.Y)
                || coordinate.X is < -180d or > 180d || coordinate.Y is < -90d or > 90d)
                throw InvalidCoordinate();
            if (index == 0) continue;
            hasDistinctPosition |= !EqualPosition(coordinates[0], coordinate);
            if (IsAntipodal(coordinates[index - 1], coordinate)) throw InvalidCoordinate();
        }
        if (!hasDistinctPosition) throw InvalidCoordinate();
    }

    private static PreparedRoute Prepare(
        IReadOnlyList<Coordinate> source,
        IReadOnlyCollection<int> requestedProtectedIndices)
    {
        var protectedSource = requestedProtectedIndices.ToHashSet();
        protectedSource.Add(0);
        protectedSource.Add(source.Count - 1);
        if (EqualPosition(source[0], source[^1]))
        {
            var pivot = Enumerable.Range(1, source.Count - 2)
                .Select(index => (Index: index, Distance: AngularDistance(source[0], source[index])))
                .OrderByDescending(item => item.Distance)
                .ThenBy(item => item.Index)
                .First();
            if (pivot.Distance == 0d) throw InvalidCoordinate();
            protectedSource.Add(pivot.Index);
        }

        var coordinates = new List<Coordinate>(source.Count);
        var originalIndices = new List<int>(source.Count);
        var preparedProtected = new HashSet<int>();
        for (var sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
        {
            var mustRetain = protectedSource.Contains(sourceIndex);
            if (coordinates.Count > 0 && EqualPosition(coordinates[^1], source[sourceIndex]) && !mustRetain
                && sourceIndex != source.Count - 1)
                continue;
            coordinates.Add(source[sourceIndex]);
            originalIndices.Add(sourceIndex);
            if (mustRetain) preparedProtected.Add(coordinates.Count - 1);
        }
        return new(coordinates, originalIndices, preparedProtected.Order().ToArray());
    }

    private static Candidate Simplify(
        PreparedRoute route,
        double toleranceMetres,
        RouteGeometryBudgetWork work,
        CancellationToken cancellationToken)
    {
        var retained = new bool[route.Coordinates.Count];
        foreach (var index in route.ProtectedIndices) retained[index] = true;
        for (var protectedIndex = 1; protectedIndex < route.ProtectedIndices.Count; protectedIndex++)
        {
            var start = route.ProtectedIndices[protectedIndex - 1];
            var end = route.ProtectedIndices[protectedIndex];
            var stack = new Stack<(int Start, int End)>();
            stack.Push((start, end));
            while (stack.Count > 0)
            {
                var range = stack.Pop();
                var farthestIndex = -1;
                var farthestDistance = -1d;
                for (var index = range.Start + 1; index < range.End; index++)
                {
                    work.RecordEvaluation(cancellationToken);
                    var distance = PointToSegmentDistance(
                        route.Coordinates[index], route.Coordinates[range.Start], route.Coordinates[range.End]);
                    if (distance > farthestDistance)
                    {
                        farthestDistance = distance;
                        farthestIndex = index;
                    }
                }
                if (farthestIndex < 0 || farthestDistance <= toleranceMetres) continue;
                retained[farthestIndex] = true;
                stack.Push((farthestIndex, range.End));
                stack.Push((range.Start, farthestIndex));
            }
        }
        var indices = Enumerable.Range(0, retained.Length).Where(index => retained[index]).ToArray();
        return new(indices.Select(index => route.Coordinates[index]).ToArray(), indices, toleranceMetres);
    }

    private static double MeasureDeviation(
        IReadOnlyList<Coordinate> source,
        IReadOnlyList<int> retainedIndices,
        RouteGeometryBudgetWork work,
        CancellationToken cancellationToken)
    {
        var maximum = 0d;
        for (var segment = 1; segment < retainedIndices.Count; segment++)
        {
            var start = retainedIndices[segment - 1];
            var end = retainedIndices[segment];
            for (var index = start + 1; index < end; index++)
            {
                work.RecordEvaluation(cancellationToken);
                maximum = Math.Max(maximum, PointToSegmentDistance(source[index], source[start], source[end]));
            }
        }
        return maximum;
    }

    private static void Revalidate(
        IReadOnlyList<Coordinate> source,
        IReadOnlyCollection<int> protectedIndices,
        Candidate selected,
        IReadOnlyList<int> retainedSourceIndices,
        double deviation)
    {
        if (selected.Coordinates.Count < 2 || selected.Coordinates.Count > MaximumPersistedCoordinates
            || deviation > MaximumDeviationMetres + 1e-9
            || retainedSourceIndices.Count != selected.Coordinates.Count
            || !StrictlyIncreasing(retainedSourceIndices)
            || !EqualPosition(source[0], selected.Coordinates[0])
            || !EqualPosition(source[^1], selected.Coordinates[^1]))
            throw Unsatisfied();
        for (var index = 0; index < retainedSourceIndices.Count; index++)
            if (!EqualPosition(source[retainedSourceIndices[index]], selected.Coordinates[index])) throw Unsatisfied();
        if (protectedIndices.Any(index => !retainedSourceIndices.Contains(index))) throw Unsatisfied();
        if (EqualPosition(source[0], source[^1])
            && (!EqualPosition(selected.Coordinates[0], selected.Coordinates[^1])
                || selected.Coordinates.Count < 3
                || selected.Coordinates.Skip(1).SkipLast(1).All(point => EqualPosition(point, selected.Coordinates[0]))))
            throw Unsatisfied();
        foreach (var coordinate in selected.Coordinates)
            if (!double.IsFinite(coordinate.X) || !double.IsFinite(coordinate.Y)
                || coordinate.X is < -180d or > 180d || coordinate.Y is < -90d or > 90d)
                throw InvalidCoordinate();
    }

    private static double PointToSegmentDistance(Coordinate point, Coordinate start, Coordinate end)
    {
        if (EqualPosition(start, end)) return AngularDistance(point, start) * EarthRadiusMetres;
        var segmentDistance = AngularDistance(start, end);
        var pointDistance = AngularDistance(start, point);
        if (pointDistance == 0d) return 0d;
        var segmentBearing = InitialBearing(start, end);
        var pointBearing = InitialBearing(start, point);
        var bearingDelta = NormalizeRadians(pointBearing - segmentBearing);
        var crossTrack = Math.Asin(Math.Clamp(Math.Sin(pointDistance) * Math.Sin(bearingDelta), -1d, 1d));
        var alongTrack = Math.Atan2(
            Math.Sin(pointDistance) * Math.Cos(bearingDelta),
            Math.Cos(pointDistance));
        if (alongTrack >= 0d && alongTrack <= segmentDistance)
            return Math.Abs(crossTrack) * EarthRadiusMetres;
        return Math.Min(AngularDistance(point, start), AngularDistance(point, end)) * EarthRadiusMetres;
    }

    private static double AngularDistance(Coordinate first, Coordinate second)
    {
        var latitude1 = DegreesToRadians(first.Y);
        var latitude2 = DegreesToRadians(second.Y);
        var latitudeDelta = latitude2 - latitude1;
        var longitudeDelta = NormalizeRadians(DegreesToRadians(second.X - first.X));
        var haversine = Math.Pow(Math.Sin(latitudeDelta / 2d), 2d)
            + Math.Cos(latitude1) * Math.Cos(latitude2) * Math.Pow(Math.Sin(longitudeDelta / 2d), 2d);
        return 2d * Math.Atan2(Math.Sqrt(Math.Clamp(haversine, 0d, 1d)), Math.Sqrt(Math.Max(0d, 1d - haversine)));
    }

    private static double InitialBearing(Coordinate first, Coordinate second)
    {
        var latitude1 = DegreesToRadians(first.Y);
        var latitude2 = DegreesToRadians(second.Y);
        var longitudeDelta = NormalizeRadians(DegreesToRadians(second.X - first.X));
        return Math.Atan2(
            Math.Sin(longitudeDelta) * Math.Cos(latitude2),
            Math.Cos(latitude1) * Math.Sin(latitude2)
                - Math.Sin(latitude1) * Math.Cos(latitude2) * Math.Cos(longitudeDelta));
    }

    // Exact coordinate relationships avoid Haversine rounding near pi; longitude is irrelevant at either pole.
    private static bool IsAntipodal(Coordinate first, Coordinate second) =>
        second.Y == -first.Y
        && (Math.Abs(first.Y) == 90d || Math.Abs(NormalizeDegrees(second.X - first.X)) == 180d);
    private static bool EqualPosition(Coordinate first, Coordinate second) => first.X == second.X && first.Y == second.Y;
    private static bool StrictlyIncreasing(IReadOnlyList<int> values) =>
        values.Zip(values.Skip(1), (first, second) => second > first).All(value => value);
    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
    private static double NormalizeDegrees(double degrees)
    {
        var normalized = (degrees + 180d) % 360d;
        if (normalized < 0d) normalized += 360d;
        return normalized - 180d;
    }
    private static double NormalizeRadians(double radians)
    {
        var normalized = (radians + Math.PI) % (2d * Math.PI);
        if (normalized < 0d) normalized += 2d * Math.PI;
        return normalized - Math.PI;
    }
    private static Coordinate Copy(Coordinate coordinate) => new(coordinate.X, coordinate.Y);
    private static RouteGeometryBudgetException InvalidCoordinate() => new(
        "generic_kml_invalid_coordinate", "A route contains an invalid coordinate.");
    private static RouteGeometryBudgetException Unsatisfied() => new(
        "generic_kml_geometry_budget_unsatisfied", "A route cannot be reduced safely to the supported size.");

    private sealed record PreparedRoute(
        IReadOnlyList<Coordinate> Coordinates,
        IReadOnlyList<int> OriginalIndices,
        IReadOnlyList<int> ProtectedIndices);
    private sealed record Candidate(
        IReadOnlyList<Coordinate> Coordinates,
        IReadOnlyList<int> OriginalIndices,
        double ToleranceMetres);
}

/// <summary>Tracks the fixed document-wide point-to-segment evaluation budget.</summary>
public sealed class RouteGeometryBudgetWork
{
    /// <summary>Creates a fresh document-wide operation counter.</summary>
    public RouteGeometryBudgetWork()
    {
    }

    /// <summary>Creates an operation counter at a known value for deterministic boundary tests.</summary>
    internal RouteGeometryBudgetWork(long evaluations) => Evaluations = evaluations;

    /// <summary>Gets the number of point-to-segment evaluations performed.</summary>
    public long Evaluations { get; private set; }

    /// <summary>Records one evaluation and performs the prescribed periodic cancellation check.</summary>
    internal void RecordEvaluation(CancellationToken cancellationToken)
    {
        Evaluations++;
        if ((Evaluations & 1_023) == 0) cancellationToken.ThrowIfCancellationRequested();
        if (Evaluations > RouteGeometryBudgeter.MaximumEvaluations)
            throw new RouteGeometryBudgetException(
                "generic_kml_processing_limit",
                "The route geometry is too complex to process safely.");
    }
}

/// <summary>Bounded accepted geometry and simplification metadata for one generic route.</summary>
public sealed record RouteGeometryBudgetResult(
    IReadOnlyList<Coordinate> Coordinates,
    bool WasSimplified,
    double ToleranceMetres,
    double MaximumDeviationMetres,
    int OriginalCoordinateCount,
    IReadOnlyList<int> SourceIndices);

/// <summary>Represents one stable generic route geometry budget failure.</summary>
public sealed class RouteGeometryBudgetException : Exception
{
    /// <summary>Creates a failure with its stable client-facing code and bounded message.</summary>
    public RouteGeometryBudgetException(string code, string message) : base(message) => Code = code;

    /// <summary>Gets the stable client-facing failure code.</summary>
    public string Code { get; }
}
