using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Owns the explicit OSRM route request and untrusted JSON response contract.</summary>
public static partial class OsrmRoutingAdapter
{
    private const int MaximumInputCoordinates = 100000;

    /// <summary>Builds the fixed OSRM route path and query from trusted server inputs.</summary>
    public static string BuildRelativeRequest(string profile, IReadOnlyList<RouteCoordinate> coordinates)
    {
        if (string.IsNullOrWhiteSpace(profile) || !ProfilePattern().IsMatch(profile))
            throw new ArgumentException("The OSRM profile is invalid.", nameof(profile));
        if (coordinates.Count is < 2 or > MaximumInputCoordinates || coordinates.Any(coordinate => !coordinate.IsValid))
            throw new ArgumentException("The route coordinates are invalid.", nameof(coordinates));

        var formatted = string.Join(';', coordinates.Select(coordinate =>
            $"{coordinate.Longitude.ToString("R", CultureInfo.InvariantCulture)},{coordinate.Latitude.ToString("R", CultureInfo.InvariantCulture)}"));
        return $"route/v1/{profile}/{formatted}?alternatives=false&steps=false&overview=full&geometries=geojson";
    }

    /// <summary>Parses and validates exactly one successful non-empty GeoJSON route.</summary>
    public static async Task<OsrmRouteResult> ParseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode) return OsrmRouteResult.Invalid("provider-http-failure");
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String || code.GetString() != "Ok"
                || !root.TryGetProperty("routes", out var routes) || routes.ValueKind != JsonValueKind.Array
                || routes.GetArrayLength() != 1)
                return OsrmRouteResult.Invalid("provider-response-invalid");

            var route = routes[0];
            if (route.ValueKind != JsonValueKind.Object
                || !route.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object
                || !geometry.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "LineString"
                || !geometry.TryGetProperty("coordinates", out var coordinates))
                return OsrmRouteResult.Invalid("provider-response-invalid");
            var routeCoordinates = ParseCoordinates(coordinates);
            if (routeCoordinates is not { Count: >= 2 }) return OsrmRouteResult.Invalid("provider-response-invalid");

            if (!root.TryGetProperty("waypoints", out var waypoints) || waypoints.ValueKind != JsonValueKind.Array)
                return OsrmRouteResult.Invalid("provider-response-invalid");
            var waypointCoordinates = new List<RouteCoordinate>(waypoints.GetArrayLength());
            foreach (var waypoint in waypoints.EnumerateArray())
            {
                if (waypoint.ValueKind != JsonValueKind.Object
                    || !waypoint.TryGetProperty("location", out var location) || ParseCoordinate(location) is not { } parsed)
                    return OsrmRouteResult.Invalid("provider-response-invalid");
                waypointCoordinates.Add(parsed);
            }
            if (waypointCoordinates.Count < 2) return OsrmRouteResult.Invalid("provider-response-invalid");
            return new OsrmRouteResult(true, routeCoordinates, waypointCoordinates, null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return OsrmRouteResult.Invalid("provider-response-invalid");
        }
    }

    private static List<RouteCoordinate>? ParseCoordinates(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return null;
        var result = new List<RouteCoordinate>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (ParseCoordinate(item) is not { } coordinate) return null;
            result.Add(coordinate);
        }
        return result;
    }

    private static RouteCoordinate? ParseCoordinate(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2
            || value[0].ValueKind != JsonValueKind.Number || value[1].ValueKind != JsonValueKind.Number
            || !value[0].TryGetDouble(out var longitude) || !value[1].TryGetDouble(out var latitude)) return null;
        var coordinate = new RouteCoordinate(longitude, latitude);
        return coordinate.IsValid ? coordinate : null;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,80}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfilePattern();
}

/// <summary>Represents one longitude/latitude pair without provider measurements.</summary>
public readonly record struct RouteCoordinate(double Longitude, double Latitude)
{
    /// <summary>Gets whether both ordinates are finite WGS84 values.</summary>
    public bool IsValid => double.IsFinite(Longitude) && double.IsFinite(Latitude)
        && Longitude is >= -180 and <= 180 && Latitude is >= -90 and <= 90;
}

/// <summary>Contains only validated OSRM route and snapped waypoint coordinates.</summary>
public sealed record OsrmRouteResult(
    bool Succeeded, IReadOnlyList<RouteCoordinate> Geometry, IReadOnlyList<RouteCoordinate> Waypoints, string? ErrorCode)
{
    /// <summary>Creates a bounded invalid result without provider details.</summary>
    public static OsrmRouteResult Invalid(string code) => new(false, [], [], code);
}
