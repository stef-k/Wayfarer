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
            foreach (var anchor in anchors)
                if (!points.Any(point => Close(point, anchor))) return Invalid();
            var instructions = new List<RouteInstruction>();
            foreach (var leg in legs.EnumerateArray())
            {
                if (!Number(leg, "distance", out _) || !Number(leg, "time", out _)
                    || !leg.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) return Invalid();
                foreach (var step in steps.EnumerateArray())
                {
                    if (instructions.Count == MaximumInstructions || !TryInstruction(step, out var instruction)) return Invalid();
                    instructions.Add(instruction!);
                }
            }
            if (instructions.Count == 0) return Invalid();
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
            || !step.TryGetProperty("to_index", out var to) || !to.TryGetInt32(out var toIndex) || toIndex <= fromIndex
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

    private static OsrmRouteResult Invalid() => OsrmRouteResult.Invalid("provider-response-invalid");
}
