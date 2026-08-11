using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

/// <summary>Field-keyed complete-request validation for editor Segment aggregates.</summary>
public sealed partial class TripEditorSegmentMutationService
{
    private static Dictionary<string, string[]> ValidatePlaceReferences(EditorSegmentSaveRequest request, Trip trip)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var places = trip.Regions.SelectMany(region => region.Places).ToDictionary(place => place.Id);
        if (request.FromPlaceId.HasValue && !places.ContainsKey(request.FromPlaceId.Value)) errors["fromPlaceId"] = ["From place must belong to this trip."];
        if (request.ToPlaceId.HasValue && !places.ContainsKey(request.ToPlaceId.Value)) errors["toPlaceId"] = ["To place must belong to this trip."];
        var seen = new HashSet<Guid>();
        for (var index = 0; index < request.WaypointPlaceIds.Count; index++)
        {
            var id = request.WaypointPlaceIds[index];
            if (!places.ContainsKey(id)) errors[$"waypointPlaceIds[{index}]"] = ["Waypoint place was not found in this Trip."];
            else if (!seen.Add(id)) errors[$"waypointPlaceIds[{index}]"] = ["Waypoint place is duplicated."];
            else if (id == request.FromPlaceId || id == request.ToPlaceId) errors[$"waypointPlaceIds[{index}]"] = ["A waypoint cannot duplicate an endpoint."];
        }
        if (request.WaypointPlaceIds.Count == 0) return errors;
        if (!request.FromPlaceId.HasValue) errors["fromPlaceId"] = ["A From place is required when waypoints exist."];
        if (!request.ToPlaceId.HasValue) errors["toPlaceId"] = ["A To place is required when waypoints exist."];
        if (request.FromPlaceId.HasValue && places.TryGetValue(request.FromPlaceId.Value, out var from) && from.Location == null) errors["fromPlaceId"] = ["The anchor requires a location."];
        if (request.ToPlaceId.HasValue && places.TryGetValue(request.ToPlaceId.Value, out var to) && to.Location == null) errors["toPlaceId"] = ["The anchor requires a location."];
        for (var index = 0; index < request.WaypointPlaceIds.Count; index++)
            if (places.TryGetValue(request.WaypointPlaceIds[index], out var waypoint) && waypoint.Location == null) errors[$"waypointPlaceIds[{index}]"] = ["The waypoint anchor requires a location."];
        ValidateWaypointIndices(request, places, errors);
        return errors;
    }

    private static void ValidateWaypointIndices(EditorSegmentSaveRequest request, IReadOnlyDictionary<Guid, Place> places, Dictionary<string, string[]> errors)
    {
        if (request.Route == null) return;
        var prior = 0;
        for (var index = 0; index < request.WaypointRouteVertexIndices.Count; index++)
        {
            var vertex = request.WaypointRouteVertexIndices[index];
            var key = $"waypointRouteVertexIndices[{index}]";
            if (!vertex.HasValue) { errors[key] = ["A custom route requires the waypoint index."]; continue; }
            if (vertex <= prior) errors[key] = ["Waypoint indices must be strictly increasing."];
            else if (vertex <= 0 || vertex >= request.Route.NumPoints - 1) errors[key] = ["The waypoint index is outside the route interior."];
            else if (places.TryGetValue(request.WaypointPlaceIds[index], out var place) && place.Location != null && !CoordinatesMatch(request.Route.GetCoordinateN(vertex.Value), place.Location.Coordinate)) errors[key] = ["The indexed route coordinate does not match the waypoint anchor."];
            prior = vertex.Value;
        }
        if (request.FromPlaceId.HasValue && places.TryGetValue(request.FromPlaceId.Value, out var from) && from.Location != null && !CoordinatesMatch(request.Route.GetCoordinateN(0), from.Location.Coordinate)) errors["fromPlaceId"] = ["The route coordinate does not match the endpoint anchor."];
        if (request.ToPlaceId.HasValue && places.TryGetValue(request.ToPlaceId.Value, out var to) && to.Location != null && !CoordinatesMatch(request.Route.GetCoordinateN(request.Route.NumPoints - 1), to.Location.Coordinate)) errors["toPlaceId"] = ["The route coordinate does not match the endpoint anchor."];
    }

    private static bool CoordinatesMatch(NetTopologySuite.Geometries.Coordinate first, NetTopologySuite.Geometries.Coordinate second) =>
        Math.Abs(first.X - second.X) <= 0.0000001d && Math.Abs(first.Y - second.Y) <= 0.0000001d;

    private static string SegmentValidationCode(IReadOnlyDictionary<string, string[]> errors)
    {
        var first = errors.First();
        if (first.Key == "request") return "segment-request-invalid";
        if (first.Key.StartsWith("waypointPlaceIds[", StringComparison.Ordinal))
        {
            var message = first.Value[0];
            if (message.Contains("not found", StringComparison.OrdinalIgnoreCase)) return "segment-waypoint-not-found";
            if (message.Contains("duplicat", StringComparison.OrdinalIgnoreCase)) return "segment-waypoint-duplicate";
            if (message.Contains("endpoint", StringComparison.OrdinalIgnoreCase)) return "segment-waypoint-endpoint-duplicate";
            if (message.Contains("location", StringComparison.OrdinalIgnoreCase)) return "segment-anchor-location-required";
            return "segment-waypoint-id-invalid";
        }
        if (first.Key is "waypointPlaceIds" or "waypointRouteVertexIndices")
            return first.Value[0].Contains("required", StringComparison.OrdinalIgnoreCase) ? "segment-field-required" : first.Value[0].Contains("matching lengths", StringComparison.OrdinalIgnoreCase) ? "segment-waypoint-index-count-mismatch" : "segment-array-invalid";
        if (first.Key.StartsWith("waypointRouteVertexIndices[", StringComparison.Ordinal))
        {
            var message = first.Value[0];
            if (message.Contains("requires", StringComparison.OrdinalIgnoreCase)) return "segment-waypoint-index-required";
            if (message.Contains("null", StringComparison.OrdinalIgnoreCase)) return "segment-waypoint-index-must-be-null";
            if (message.Contains("increasing", StringComparison.OrdinalIgnoreCase)) return "segment-waypoint-index-order-invalid";
            if (message.Contains("outside", StringComparison.OrdinalIgnoreCase)) return "segment-waypoint-index-out-of-range";
            if (message.Contains("coordinate", StringComparison.OrdinalIgnoreCase)) return "segment-anchor-coordinate-mismatch";
            return "segment-waypoint-index-invalid";
        }
        if (first.Key is "fromPlaceId" or "toPlaceId")
        {
            if (first.Value[0].Contains("location", StringComparison.OrdinalIgnoreCase)) return "segment-anchor-location-required";
            if (first.Value[0].Contains("coordinate", StringComparison.OrdinalIgnoreCase)) return "segment-anchor-coordinate-mismatch";
            if (first.Value[0].Contains("required", StringComparison.OrdinalIgnoreCase)) return "segment-anchor-required";
        }
        if (first.Value.Any(message => message.Contains("required", StringComparison.OrdinalIgnoreCase))) return first.Key == "estimatedDurationSource" ? "segment-duration-source-required" : "segment-field-required";
        if (first.Key == "mode") return "segment-mode-invalid";
        if (first.Key == "estimatedDurationSource") return "segment-duration-source-invalid";
        if (first.Key == "estimatedDurationMinutes") return "segment-duration-invalid";
        if (first.Key.StartsWith("route", StringComparison.Ordinal)) return "segment-route-invalid";
        if (first.Key == "aggregateConcurrencyToken") return "segment-aggregate-token-invalid";
        return "segment-request-invalid";
    }
}
