using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Owns the fixed Geoapify Routing request and complete untrusted response contract.</summary>
public static class GeoapifyRoutingAdapter
{
    private const int MaximumPoints = 10_000;
    private const int MaximumInstructions = 5_000;

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
    public static async Task<OsrmRouteResult> ParseAsync(HttpResponseMessage response,
        IReadOnlyList<RouteCoordinate> anchors, CancellationToken cancellationToken = default)
    {
        if (!response.IsSuccessStatusCode) return OsrmRouteResult.Invalid("provider-http-failure");
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() != 1) return Invalid();
            var route = results[0];
            if (!Number(route, "distance", out var distance) || !Number(route, "time", out var duration)
                || duration > TimeSpan.FromDays(365).TotalSeconds
                || !route.TryGetProperty("geometry", out var geometry)
                || !geometry.TryGetProperty("type", out var type) || type.GetString() != "LineString"
                || !geometry.TryGetProperty("coordinates", out var coordinates)
                || ParseCoordinates(coordinates) is not { Count: >= 2 } points || points.Count > MaximumPoints
                || !route.TryGetProperty("legs", out var legs) || legs.ValueKind != JsonValueKind.Array
                || legs.GetArrayLength() != anchors.Count - 1) return Invalid();
            if (!Close(points[0], anchors[0]) || !Close(points[^1], anchors[^1])) return Invalid();
            var anchorIndices = MapAnchors(points, anchors);
            if (anchorIndices == null) return Invalid();
            var instructions = new List<RouteInstruction>();
            var legDistances = new List<double>();
            var legDurations = new List<double>();
            var legIndex = 0;
            foreach (var leg in legs.EnumerateArray())
            {
                if (!Number(leg, "distance", out var legDistance) || !Number(leg, "time", out var legDuration)
                    || !leg.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) return Invalid();
                var legInstructions = new List<RouteInstruction>();
                foreach (var step in steps.EnumerateArray())
                {
                    if (instructions.Count == MaximumInstructions || !TryInstruction(step, out var instruction)) return Invalid();
                    legInstructions.Add(instruction!);
                    instructions.Add(instruction!);
                }
                if (!ValidateLeg(legInstructions, anchorIndices[legIndex], anchorIndices[legIndex + 1],
                        points.Count, legDistance, legDuration)) return Invalid();
                legDistances.Add(legDistance);
                legDurations.Add(legDuration);
                legIndex++;
            }
            if (instructions.Count == 0 || !TotalsAgree(legDistances, distance) || !TotalsAgree(legDurations, duration))
                return Invalid();
            return new(true, points, anchors.ToArray(), null, distance, duration, instructions);
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

    private static bool TryInstruction(JsonElement step, out RouteInstruction? value)
    {
        value = null;
        if (!step.TryGetProperty("instruction", out var instruction)
            || !instruction.TryGetProperty("text", out var text) || string.IsNullOrWhiteSpace(text.GetString())
            || !instruction.TryGetProperty("type", out var type) || string.IsNullOrWhiteSpace(type.GetString())
            || !step.TryGetProperty("from_index", out var from) || !from.TryGetInt32(out var fromIndex) || fromIndex < 0
            || !step.TryGetProperty("to_index", out var to) || !to.TryGetInt32(out var toIndex) || toIndex < fromIndex
            || !Number(step, "distance", out var distance) || !Number(step, "time", out var duration)) return false;
        value = new(text.GetString()!.Trim()[..Math.Min(500, text.GetString()!.Trim().Length)],
            type.GetString()!.Trim()[..Math.Min(80, type.GetString()!.Trim().Length)],
            fromIndex, toIndex, distance, duration);
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

    private static int[]? MapAnchors(IReadOnlyList<RouteCoordinate> points, IReadOnlyList<RouteCoordinate> anchors)
    {
        var indices = new int[anchors.Count];
        for (var anchorIndex = 0; anchorIndex < anchors.Count; anchorIndex++)
        {
            var matches = Enumerable.Range(0, points.Count).Where(index => Close(points[index], anchors[anchorIndex])).ToArray();
            if (matches.Length != 1 || anchorIndex > 0 && matches[0] <= indices[anchorIndex - 1]) return null;
            indices[anchorIndex] = matches[0];
        }
        return indices[0] == 0 && indices[^1] == points.Count - 1 ? indices : null;
    }

    private static bool ValidateLeg(IReadOnlyList<RouteInstruction> steps, int legStart, int legEnd,
        int pointCount, double legDistance, double legDuration)
    {
        if (steps.Count == 0 || steps[0].FromIndex != legStart || steps[^1].ToIndex != legEnd) return false;
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (step.FromIndex < legStart || step.ToIndex > legEnd || step.ToIndex >= pointCount
                || index > 0 && step.FromIndex != steps[index - 1].ToIndex) return false;
        }
        return TotalsAgree(steps.Select(step => step.DistanceMetres), legDistance)
            && TotalsAgree(steps.Select(step => step.DurationSeconds), legDuration);
    }

    // Geoapify reports decimal metrics; half of the final displayed hundredth per summed value bounds rounding drift.
    private static bool TotalsAgree(IEnumerable<double> parts, double total)
    {
        var values = parts.ToArray();
        var sum = values.Sum();
        var tolerance = (values.Length + 1) * 0.005 + 1e-9;
        return double.IsFinite(sum) && Math.Abs(sum - total) <= tolerance;
    }

    private static OsrmRouteResult Invalid() => OsrmRouteResult.Invalid("provider-response-invalid");
}
