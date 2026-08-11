using System.Text.Json;
using NetTopologySuite.Geometries;
using Wayfarer.Services;

namespace Wayfarer.Models.Dtos.Editor;

/// <summary>
/// Parses and validates segment mutation JSON while preserving ownership-first request handling.
/// </summary>
internal static class EditorSegmentRequestParser
{
    private static readonly string[] ServerOwnedFields = { "id", "tripId", "displayOrder", "capabilities" };
    /// <summary>
    /// Attempts to parse a complete-draft segment save request.
    /// </summary>
    public static bool TryParseSave(JsonElement request, out EditorSegmentSaveRequest update, out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        update = new EditorSegmentSaveRequest(null, null, [], [], string.Empty, null, null, EstimatedDurationSource.Automatic, null, null, null);
        if (!ValidateObject(request, errors, "Segment mutation request must be a JSON object."))
        {
            return false;
        }

        RejectFields(request, ServerOwnedFields, "The field is server-owned and cannot be updated.", errors);
        var fromPlaceId = ReadRequiredNullableGuid(request, "fromPlaceId", errors);
        var toPlaceId = ReadRequiredNullableGuid(request, "toPlaceId", errors);
        var waypointPlaceIds = ReadRequiredGuidArray(request, "waypointPlaceIds", errors);
        var waypointIndices = ReadRequiredNullableIntArray(request, "waypointRouteVertexIndices", errors);
        var mode = ReadRequiredNullableString(request, "mode", errors);
        var distance = ReadIgnoredRequiredNumber(request, "estimatedDistanceKm", errors);
        var durationSource = ReadRequiredDurationSource(request, errors);
        var duration = durationSource == EstimatedDurationSource.Manual
            ? ReadRequiredNullableNonNegativeDouble(request, "estimatedDurationMinutes", errors)
            : ReadIgnoredRequiredNumber(request, "estimatedDurationMinutes", errors);
        if (durationSource == EstimatedDurationSource.Manual && !duration.HasValue && !errors.ContainsKey("estimatedDurationMinutes"))
            errors["estimatedDurationMinutes"] = ["Manual duration is required."];
        var notesHtml = ReadRequiredNullableString(request, "notesHtml", errors);
        var route = ReadRequiredNullableRoute(request, errors);
        var aggregateToken = ReadRequiredNullableString(request, "aggregateConcurrencyToken", errors);
        if (waypointPlaceIds.Count != waypointIndices.Count)
            errors["waypointRouteVertexIndices"] = ["Waypoint IDs and route vertex indices must have matching lengths."];
        if (route == null)
            for (var index = 0; index < waypointIndices.Count; index++)
                if (waypointIndices[index].HasValue)
                    errors[$"waypointRouteVertexIndices[{index}]"] = ["The route vertex index must be null when route is null."];
        ValidateMode(mode, errors);
        ValidateNotes(notesHtml, errors);

        if (errors.Count > 0)
        {
            return false;
        }

        update = new EditorSegmentSaveRequest(
            fromPlaceId,
            toPlaceId,
            waypointPlaceIds,
            waypointIndices,
            CanonicalMode(mode),
            distance,
            duration,
            durationSource,
            EditorRichNotesRequestHtml.NormalizeForPersistence(notesHtml),
            route,
            aggregateToken);
        return true;
    }

    /// <summary>
    /// Attempts to parse a complete trip-level segment order request.
    /// </summary>
    public static bool TryParseOrder(JsonElement request, out EditorSegmentOrderRequest update, out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        update = new EditorSegmentOrderRequest(Array.Empty<Guid>());
        if (!ValidateObject(request, errors, "Segment order request must be a JSON object."))
        {
            return false;
        }

        RejectFields(request, ServerOwnedFields, "The field is server-owned and cannot be updated.", errors);
        if (!request.TryGetProperty("segmentIds", out var idsProperty))
        {
            errors["segmentIds"] = new[] { "The field is required." };
            return false;
        }

        if (idsProperty.ValueKind != JsonValueKind.Array)
        {
            errors["segmentIds"] = new[] { "Segment IDs must be an array." };
            return false;
        }

        var segmentIds = new List<Guid>();
        foreach (var item in idsProperty.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Guid.TryParse(item.GetString(), out var id))
            {
                errors["segmentIds"] = new[] { "Every segment ID must be a GUID string." };
                return false;
            }

            segmentIds.Add(id);
        }

        update = new EditorSegmentOrderRequest(segmentIds);
        return true;
    }

    private static bool ValidateObject(JsonElement request, Dictionary<string, string[]> errors, string message)
    {
        if (request.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        errors["request"] = new[] { message };
        return false;
    }

    private static void RejectFields(JsonElement request, IEnumerable<string> fields, string message, Dictionary<string, string[]> errors)
    {
        foreach (var field in fields)
        {
            if (request.TryGetProperty(field, out _))
            {
                errors[field] = new[] { message };
            }
        }
    }

    private static Guid? ReadRequiredNullableGuid(JsonElement request, string name, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty(name, out var property))
        {
            errors[name] = new[] { "The field is required." };
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String || !Guid.TryParse(property.GetString(), out var value))
        {
            errors[name] = new[] { "The field must be a GUID string or null." };
            return null;
        }

        return value;
    }

    private static IReadOnlyList<Guid> ReadRequiredGuidArray(JsonElement request, string name, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty(name, out var property))
        {
            errors[name] = ["The field is required."];
            return [];
        }
        if (property.ValueKind != JsonValueKind.Array)
        {
            errors[name] = ["The field must be an array."];
            return [];
        }
        var values = new List<Guid>();
        var index = 0;
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Guid.TryParse(item.GetString(), out var value))
                errors[$"{name}[{index}]"] = ["The waypoint ID must be a GUID string."];
            else values.Add(value);
            index++;
        }
        return values;
    }

    private static IReadOnlyList<int?> ReadRequiredNullableIntArray(JsonElement request, string name, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty(name, out var property))
        {
            errors[name] = ["The field is required."];
            return [];
        }
        if (property.ValueKind != JsonValueKind.Array)
        {
            errors[name] = ["The field must be an array."];
            return [];
        }
        var values = new List<int?>();
        var index = 0;
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Null) values.Add(null);
            else if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var value)) values.Add(value);
            else errors[$"{name}[{index}]"] = ["The route vertex index must be an integer or null."];
            index++;
        }
        return values;
    }

    private static string? ReadRequiredNullableString(JsonElement request, string name, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty(name, out var property))
        {
            errors[name] = new[] { "The field is required." };
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            errors[name] = new[] { "The field must be a string or null." };
            return null;
        }

        return property.GetString();
    }

    private static double? ReadRequiredNullableNonNegativeDouble(JsonElement request, string name, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty(name, out var property))
        {
            errors[name] = new[] { "The field is required." };
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (!TryReadFiniteDouble(property, out var value) || value < 0)
        {
            errors[name] = new[] { "The field must be a finite non-negative number or null." };
            return null;
        }

        return value;
    }

    private static double? ReadIgnoredRequiredNumber(JsonElement request, string name, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty(name, out _))
            errors[name] = ["The field is required for request-shape compatibility."];
        return null;
    }

    private static EstimatedDurationSource ReadRequiredDurationSource(JsonElement request, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty("estimatedDurationSource", out var property))
        {
            errors["estimatedDurationSource"] = ["Reload the editor before saving; estimated duration source is required."];
            return EstimatedDurationSource.Automatic;
        }
        if (property.ValueKind != JsonValueKind.String
            || !Enum.TryParse<EstimatedDurationSource>(property.GetString(), ignoreCase: true, out var source)
            || !Enum.IsDefined(source))
        {
            errors["estimatedDurationSource"] = ["Duration source must be Automatic or Manual."];
            return EstimatedDurationSource.Automatic;
        }
        return source;
    }

    private static LineString? ReadRequiredNullableRoute(JsonElement request, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty("route", out var property))
        {
            errors["route"] = new[] { "The field is required." };
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Object)
        {
            errors["route"] = new[] { "Route must be a GeoJSON LineString object or null." };
            return null;
        }

        if (!property.TryGetProperty("type", out var typeProperty) || typeProperty.ValueKind != JsonValueKind.String || typeProperty.GetString() != "LineString")
        {
            errors["route"] = new[] { "Route type must be LineString." };
            return null;
        }

        if (!property.TryGetProperty("coordinates", out var coordinatesProperty) || coordinatesProperty.ValueKind != JsonValueKind.Array)
        {
            errors["route.coordinates"] = new[] { "LineString coordinates must be an array of positions." };
            return null;
        }

        var coordinates = new List<Coordinate>();
        foreach (var positionProperty in coordinatesProperty.EnumerateArray())
        {
            var coordinate = ReadPosition(positionProperty, errors);
            if (coordinate == null)
            {
                return null;
            }

            coordinates.Add(coordinate);
        }

        if (coordinates.Count < 2)
        {
            errors["route.coordinates"] = new[] { "LineString must contain at least two positions." };
            return null;
        }

        return new LineString(coordinates.ToArray()) { SRID = 4326 };
    }

    private static Coordinate? ReadPosition(JsonElement positionProperty, Dictionary<string, string[]> errors)
    {
        if (positionProperty.ValueKind != JsonValueKind.Array || positionProperty.GetArrayLength() != 2)
        {
            errors["route.coordinates"] = new[] { "Every position must contain exactly longitude and latitude." };
            return null;
        }

        var values = positionProperty.EnumerateArray().ToArray();
        if (!TryReadFiniteDouble(values[0], out var longitude) || !TryReadFiniteDouble(values[1], out var latitude))
        {
            errors["route.coordinates"] = new[] { "Every position must contain finite numeric longitude and latitude." };
            return null;
        }

        if (longitude is < -180 or > 180 || latitude is < -90 or > 90)
        {
            errors["route.coordinates"] = new[] { "Coordinates must use longitude -180..180 and latitude -90..90." };
            return null;
        }

        return new Coordinate(longitude, latitude);
    }

    private static bool TryReadFiniteDouble(JsonElement property, out double value)
    {
        value = default;
        return property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value)
            && !double.IsNaN(value)
            && !double.IsInfinity(value);
    }

    private static void ValidateMode(string? mode, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return;
        }

        var normalized = TransportProfile.NormalizeKey(mode);
        if (normalized.Length > 80)
        {
            errors["mode"] = new[] { "Mode must be 80 characters or fewer." };
        }
    }

    private static string CanonicalMode(string? mode) =>
        string.IsNullOrWhiteSpace(mode)
            ? string.Empty
            : mode;

    private static void ValidateNotes(string? notesHtml, Dictionary<string, string[]> errors)
    {
        if (EditorRichNotesRequestHtml.ContainsDataImageSource(notesHtml))
        {
            errors["notesHtml"] = new[] { "Notes cannot contain data image sources." };
        }
    }
}
