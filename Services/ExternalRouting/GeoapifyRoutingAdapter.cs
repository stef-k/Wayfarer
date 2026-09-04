using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Owns the fixed Geoapify Routing request and complete untrusted response contract.</summary>
public static class GeoapifyRoutingAdapter
{
    private const int MaximumPoints = 10_000;
    private const int MaximumSteps = 5_000;

    /// <summary>Builds an exact bounded routing request from a validated closed mode.</summary>
    public static string BuildRelativeRequest(string mode, IReadOnlyList<RouteCoordinate> coordinates, string credential)
    {
        if (!GeoapifyRouteCost.TryParse(mode, out _) || coordinates.Count is < 2 or > 25
            || coordinates.Any(coordinate => !coordinate.IsValid))
            throw new ArgumentException("The Geoapify routing request is invalid.");
        var waypoints = string.Join("%7C", coordinates.Select(coordinate =>
            $"{coordinate.Latitude.ToString("R", CultureInfo.InvariantCulture)},{coordinate.Longitude.ToString("R", CultureInfo.InvariantCulture)}"));
        var stopover = coordinates.Count > 2 ? "&intermediate_waypoint_mode=stopover" : string.Empty;
        return $"v1/routing?waypoints={waypoints}&mode={mode}&format=json&lang=en&details=instruction_details"
            + $"&type=balanced&traffic=free_flow{stopover}&apiKey={Uri.EscapeDataString(credential)}";
    }

    /// <summary>Parses exactly one complete route and validates every input anchor.</summary>
    public static async Task<ProviderRouteResult> ParseAsync(HttpResponseMessage response,
        IReadOnlyList<RouteCoordinate> anchors, CancellationToken cancellationToken = default)
    {
        if (!response.IsSuccessStatusCode) return ProviderRouteResult.Invalid("provider-http-failure");
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() != 1) return Invalid();
            var route = results[0];
            if (anchors.Count is < 2 or > 25
                || !Number(route, "distance", out var distance) || !Number(route, "time", out var duration)
                || !ValidDistanceUnits(route)
                || !route.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Array
                || geometry.GetArrayLength() != anchors.Count - 1
                || !route.TryGetProperty("legs", out var legs) || legs.ValueKind != JsonValueKind.Array
                || legs.GetArrayLength() != anchors.Count - 1) return Invalid();

            var points = new List<RouteCoordinate>();
            var legPoints = new List<IReadOnlyList<RouteCoordinate>>();
            var offsets = new int[geometry.GetArrayLength()];
            var anchorIndices = new int[anchors.Count];
            anchorIndices[0] = 0;
            for (var legIndex = 0; legIndex < geometry.GetArrayLength(); legIndex++)
            {
                if (ParseCoordinates(geometry[legIndex]) is not { Count: >= 2 } line) return Invalid();
                offsets[legIndex] = legIndex == 0 ? 0 : points.Count - 1;
                if (legIndex == 0) points.AddRange(line);
                else
                {
                    if (!Close(points[^1], line[0])) return Invalid();
                    points.AddRange(line.Skip(1));
                }
                if (points.Count > MaximumPoints) return Invalid();
                legPoints.Add(line);
                anchorIndices[legIndex + 1] = offsets[legIndex] + line.Count - 1;
            }
            if (!MapAnchors(points, anchors, anchorIndices)) return Invalid();

            var instructions = new List<RouteInstruction>();
            var legDistances = new List<double>();
            var legDurations = new List<double>();
            var stepCount = 0;
            for (var legIndex = 0; legIndex < legs.GetArrayLength(); legIndex++)
            {
                var leg = legs[legIndex];
                if (!Number(leg, "distance", out var legDistance) || !Number(leg, "time", out var legDuration)
                    || !leg.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) return Invalid();
                var parsedSteps = new List<ParsedStep>();
                foreach (var step in steps.EnumerateArray())
                {
                    if (++stepCount > MaximumSteps
                        || !TryStep(step, legPoints[legIndex].Count, offsets[legIndex], out var parsed)) return Invalid();
                    parsedSteps.Add(parsed);
                    if (parsed.Instruction != null) instructions.Add(parsed.Instruction);
                }
                if (!ValidateLeg(parsedSteps, legPoints[legIndex].Count, legDistance, legDuration)) return Invalid();
                legDistances.Add(legDistance);
                legDurations.Add(legDuration);
            }
            if (!TotalsAgree(legDistances, distance) || !TotalsAgree(legDurations, duration))
                return Invalid();
            return new(true, points, anchors.ToArray(), null, distance, duration, instructions, anchorIndices);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        { return Invalid(); }
    }

    private static List<RouteCoordinate>? ParseCoordinates(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return null;
        var result = new List<RouteCoordinate>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 2
                || !item[0].TryGetDouble(out var longitude) || !item[1].TryGetDouble(out var latitude)) return null;
            var coordinate = new RouteCoordinate(longitude, latitude);
            if (!coordinate.IsValid) return null;
            result.Add(coordinate);
        }
        return result;
    }

    private static bool TryStep(JsonElement step, int legPointCount, int offset, out ParsedStep value)
    {
        value = default;
        if (!step.TryGetProperty("from_index", out var from) || !from.TryGetInt32(out var fromIndex)
            || !step.TryGetProperty("to_index", out var to) || !to.TryGetInt32(out var toIndex)
            || fromIndex < 0 || fromIndex > toIndex || toIndex >= legPointCount
            || !Number(step, "distance", out var distance) || !Number(step, "time", out var duration)
            || !TryInstruction(step, offset + fromIndex, offset + toIndex, distance, duration, out var instruction))
            return false;
        value = new(fromIndex, toIndex, distance, duration, instruction);
        return true;
    }

    private static bool TryInstruction(JsonElement step, int fromIndex, int toIndex, double distance,
        double duration, out RouteInstruction? value)
    {
        value = null;
        if (!step.TryGetProperty("instruction", out var instruction) || instruction.ValueKind == JsonValueKind.Null)
            return true;
        if (instruction.ValueKind != JsonValueKind.Object) return false;
        if (!instruction.TryGetProperty("text", out var text) || text.ValueKind == JsonValueKind.Null) return true;
        if (text.ValueKind != JsonValueKind.String) return false;
        var normalizedText = text.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedText)) return true;
        var normalizedType = "None";
        if (instruction.TryGetProperty("type", out var type) && type.ValueKind != JsonValueKind.Null)
        {
            if (type.ValueKind != JsonValueKind.String) return false;
            if (!string.IsNullOrWhiteSpace(type.GetString())) normalizedType = type.GetString()!.Trim();
        }
        value = new(normalizedText[..Math.Min(500, normalizedText.Length)],
            normalizedType[..Math.Min(80, normalizedType.Length)], fromIndex, toIndex, distance, duration);
        return true;
    }

    private static bool Number(JsonElement value, string name, out double parsed)
    {
        parsed = 0;
        return value.TryGetProperty(name, out var number) && number.TryGetDouble(out parsed)
            && double.IsFinite(parsed) && parsed >= 0;
    }

    private static bool Close(RouteCoordinate first, RouteCoordinate second) =>
        Math.Abs(first.Longitude - second.Longitude) <= 0.00025
        && Math.Abs(first.Latitude - second.Latitude) <= 0.00025;

    private static bool ValidDistanceUnits(JsonElement route)
    {
        if (!route.TryGetProperty("distance_units", out var units)) return true;
        return units.ValueKind == JsonValueKind.String
            && string.Equals(units.GetString(), "meters", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MapAnchors(IReadOnlyList<RouteCoordinate> points, IReadOnlyList<RouteCoordinate> anchors,
        IReadOnlyList<int> indices)
    {
        for (var anchorIndex = 0; anchorIndex < anchors.Count; anchorIndex++)
        {
            if (indices[anchorIndex] < 0 || indices[anchorIndex] >= points.Count
                || !Close(points[indices[anchorIndex]], anchors[anchorIndex])
                || anchorIndex > 0 && indices[anchorIndex] <= indices[anchorIndex - 1]) return false;
        }
        return indices[0] == 0 && indices[^1] == points.Count - 1;
    }

    private static bool ValidateLeg(IReadOnlyList<ParsedStep> steps, int legPointCount,
        double legDistance, double legDuration)
    {
        if (steps.Count == 0 || steps[0].FromIndex != 0 || steps[^1].ToIndex != legPointCount - 1) return false;
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (index > 0 && step.FromIndex != steps[index - 1].ToIndex) return false;
        }
        return TotalsAgree(steps.Select(step => step.DistanceMetres), legDistance)
            && TotalsAgree(steps.Select(step => step.DurationSeconds), legDuration);
    }

    /// <summary>Applies the issue contract's scale-aware absolute tolerance without changing provider metrics.</summary>
    private static bool TotalsAgree(IEnumerable<double> parts, double total)
    {
        var sum = parts.Sum();
        var tolerance = Math.Max(0.01, 1e-9 * Math.Max(Math.Abs(sum), Math.Abs(total)));
        return double.IsFinite(sum) && Math.Abs(sum - total) <= tolerance;
    }

    private readonly record struct ParsedStep(int FromIndex, int ToIndex, double DistanceMetres,
        double DurationSeconds, RouteInstruction? Instruction);

    private static ProviderRouteResult Invalid() => ProviderRouteResult.Invalid("provider-response-invalid");
}
