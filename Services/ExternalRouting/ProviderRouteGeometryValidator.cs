using NetTopologySuite.Geometries;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Restores exact semantic anchors and budgets untrusted provider route geometry.</summary>
public sealed class ProviderRouteGeometryValidator : IProviderRouteGeometryValidator
{
    private const double AnchorToleranceMetres = 25;
    private const double EqualityToleranceMetres = 0.001;

    /// <inheritdoc />
    public ProviderRouteValidationResult Validate(
        IReadOnlyList<RouteCoordinate> anchors, ProviderRouteResult providerRoute, CancellationToken cancellationToken)
    {
        if (!providerRoute.Succeeded || anchors.Count is < 2 or > 50
            || providerRoute.Waypoints.Count != anchors.Count || providerRoute.Geometry.Count is < 2 or > 100000
            || anchors.Any(anchor => !anchor.IsValid) || providerRoute.Geometry.Any(coordinate => !coordinate.IsValid)
            || providerRoute.Waypoints.Any(coordinate => !coordinate.IsValid))
            return ProviderRouteValidationResult.Failure("provider-route-invalid");
        for (var index = 0; index < anchors.Count; index++)
            if (DistanceMetres(anchors[index], providerRoute.Waypoints[index]) > AnchorToleranceMetres)
                return ProviderRouteValidationResult.Failure("provider-waypoints-incompatible");

        var geometry = providerRoute.Geometry.ToList();
        if (providerRoute.StructuralWaypointIndices is { } structuralIndices)
            return ValidateStructuralAnchors(anchors, providerRoute, geometry, structuralIndices, cancellationToken);

        var protectedIndices = new List<int>(anchors.Count) { 0 };
        if (DistanceMetres(providerRoute.Waypoints[0], geometry[0]) > AnchorToleranceMetres
            || DistanceMetres(providerRoute.Waypoints[^1], geometry[^1]) > AnchorToleranceMetres)
            return ProviderRouteValidationResult.Failure("provider-waypoints-incompatible");
        geometry[0] = anchors[0];
        for (var anchorIndex = 1; anchorIndex < anchors.Count - 1; anchorIndex++)
        {
            var resolved = ResolveIntermediateAnchor(geometry, providerRoute.Waypoints[anchorIndex], anchors[anchorIndex], protectedIndices[^1]);
            if (resolved < 0) return ProviderRouteValidationResult.Failure("provider-anchor-ambiguous");
            protectedIndices.Add(resolved);
        }
        geometry[^1] = anchors[^1];
        protectedIndices.Add(geometry.Count - 1);
        if (!StrictlyIncreasing(protectedIndices)) return ProviderRouteValidationResult.Failure("provider-anchor-order-invalid");

        return Budget(anchors, geometry, protectedIndices, cancellationToken);
    }

    private static ProviderRouteValidationResult ValidateStructuralAnchors(
        IReadOnlyList<RouteCoordinate> anchors, ProviderRouteResult providerRoute, List<RouteCoordinate> geometry,
        IReadOnlyList<int> indices, CancellationToken cancellationToken)
    {
        if (indices.Count != anchors.Count || indices.Count < 2 || indices[0] != 0
            || indices[^1] != geometry.Count - 1 || !StrictlyIncreasing(indices)
            || indices.Any(index => index < 0 || index >= geometry.Count))
            return ProviderRouteValidationResult.Failure("provider-route-invalid");
        for (var anchorIndex = 0; anchorIndex < anchors.Count; anchorIndex++)
        {
            var coordinate = geometry[indices[anchorIndex]];
            if (DistanceMetres(coordinate, providerRoute.Waypoints[anchorIndex]) > AnchorToleranceMetres
                || DistanceMetres(coordinate, anchors[anchorIndex]) > AnchorToleranceMetres)
                return ProviderRouteValidationResult.Failure("provider-route-invalid");
            geometry[indices[anchorIndex]] = anchors[anchorIndex];
        }
        return Budget(anchors, geometry, indices, cancellationToken);
    }

    private static ProviderRouteValidationResult Budget(
        IReadOnlyList<RouteCoordinate> anchors, IReadOnlyList<RouteCoordinate> geometry,
        IReadOnlyList<int> protectedIndices, CancellationToken cancellationToken)
    {
        try
        {
            var source = geometry.Select(item => new Coordinate(item.Longitude, item.Latitude)).ToArray();
            var budgeted = RouteGeometryBudgeter.Budget(source, protectedIndices, cancellationToken);
            var result = budgeted.Coordinates.Select(item => new RouteCoordinate(item.X, item.Y)).ToArray();
            var indices = MapProtectedIndices(budgeted.SourceIndices, protectedIndices);
            if (indices == null || budgeted.SourceIndices.Count != result.Length
                || result.Length > RouteGeometryBudgeter.MaximumPersistedCoordinates || !StrictlyIncreasing(indices)
                || indices.Where((outputIndex, anchorIndex) => result[outputIndex] != anchors[anchorIndex]).Any())
                return ProviderRouteValidationResult.Failure("provider-route-budget-unsatisfied");
            return new ProviderRouteValidationResult(true, result, indices, null);
        }
        catch (RouteGeometryBudgetException) { return ProviderRouteValidationResult.Failure("provider-route-budget-unsatisfied"); }
    }

    private static int ResolveIntermediateAnchor(
        List<RouteCoordinate> geometry, RouteCoordinate snapped, RouteCoordinate exact, int previousIndex)
    {
        var candidates = Enumerable.Range(previousIndex + 1, geometry.Count - previousIndex - 2)
            .Select(index => (Index: index, Distance: DistanceMetres(snapped, geometry[index])))
            .Where(item => item.Distance <= AnchorToleranceMetres).OrderBy(item => item.Distance).ThenBy(item => item.Index).ToArray();
        if (candidates.Length > 0)
        {
            if (candidates.Length > 1 && Math.Abs(candidates[0].Distance - candidates[1].Distance) <= EqualityToleranceMetres)
                return -1;
            geometry[candidates[0].Index] = exact;
            return candidates[0].Index;
        }

        var segments = Enumerable.Range(previousIndex, geometry.Count - previousIndex - 1)
            .Select(index => (Index: index, Distance: SegmentDistanceMetres(snapped, geometry[index], geometry[index + 1])))
            .Where(item => item.Distance <= AnchorToleranceMetres).OrderBy(item => item.Distance).ThenBy(item => item.Index).ToArray();
        if (segments.Length == 0 || (segments.Length > 1 && Math.Abs(segments[0].Distance - segments[1].Distance) <= EqualityToleranceMetres))
            return -1;
        var inserted = segments[0].Index + 1;
        geometry.Insert(inserted, exact);
        return inserted;
    }

    private static int[]? MapProtectedIndices(
        IReadOnlyList<int> retainedSourceIndices, IReadOnlyList<int> protectedSourceIndices)
    {
        if (!StrictlyIncreasing(retainedSourceIndices)) return null;
        var result = new int[protectedSourceIndices.Count];
        var outputIndex = 0;
        for (var protectedIndex = 0; protectedIndex < protectedSourceIndices.Count; protectedIndex++)
        {
            while (outputIndex < retainedSourceIndices.Count
                   && retainedSourceIndices[outputIndex] < protectedSourceIndices[protectedIndex]) outputIndex++;
            if (outputIndex >= retainedSourceIndices.Count
                || retainedSourceIndices[outputIndex] != protectedSourceIndices[protectedIndex]) return null;
            result[protectedIndex] = outputIndex;
            outputIndex++;
        }
        return result;
    }

    private static bool StrictlyIncreasing(IReadOnlyList<int> values) =>
        values.Count >= 2 && values.Zip(values.Skip(1), (first, second) => second > first).All(value => value);

    private static double SegmentDistanceMetres(RouteCoordinate point, RouteCoordinate start, RouteCoordinate end)
    {
        var latitudeScale = Math.Cos(point.Latitude * Math.PI / 180);
        var startX = (start.Longitude - point.Longitude) * latitudeScale;
        var startY = start.Latitude - point.Latitude;
        var endX = (end.Longitude - point.Longitude) * latitudeScale;
        var endY = end.Latitude - point.Latitude;
        var deltaX = endX - startX;
        var deltaY = endY - startY;
        var denominator = deltaX * deltaX + deltaY * deltaY;
        var position = denominator == 0 ? 0 : Math.Clamp(-(startX * deltaX + startY * deltaY) / denominator, 0, 1);
        var closest = new RouteCoordinate(point.Longitude + (startX + position * deltaX) / latitudeScale,
            point.Latitude + startY + position * deltaY);
        return DistanceMetres(point, closest);
    }

    private static double DistanceMetres(RouteCoordinate first, RouteCoordinate second)
    {
        const double radius = 6371000;
        var firstLatitude = first.Latitude * Math.PI / 180;
        var secondLatitude = second.Latitude * Math.PI / 180;
        var latitudeDelta = secondLatitude - firstLatitude;
        var longitudeDelta = (second.Longitude - first.Longitude) * Math.PI / 180;
        var haversine = Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2)
            + Math.Cos(firstLatitude) * Math.Cos(secondLatitude) * Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2);
        return radius * 2 * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(Math.Max(0, 1 - haversine)));
    }
}
