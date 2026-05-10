using System.Text.Json;
using System.Text.RegularExpressions;
using NetTopologySuite.Geometries;

namespace Wayfarer.Models.Dtos.Editor;

/// <summary>
/// Parses and validates area mutation JSON while preserving ownership-first request handling.
/// </summary>
internal static class EditorAreaRequestParser
{
    private static readonly string[] SaveServerOwnedFields = { "id", "tripId", "displayOrder", "capabilities" };
    private static readonly string[] GeometryForbiddenFields = { "id", "tripId", "regionId", "displayOrder", "capabilities", "name", "notesHtml", "fillHex" };
    private static readonly Regex FillHexRegex = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);
    private static readonly Regex DataImageSourceRegex = new(
        @"<img\b[^>]*?\bsrc\s*=\s*[""']?\s*data:image/",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Attempts to parse a complete-draft area create request.
    /// </summary>
    public static bool TryParseCreate(JsonElement request, out EditorAreaSaveRequest update, out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        update = new EditorAreaSaveRequest("Area", null, "#ff6600", EmptyPolygon());
        if (!ValidateObject(request, errors, "Area mutation request must be a JSON object."))
        {
            return false;
        }

        RejectFields(request, SaveServerOwnedFields.Append("regionId"), "The field is server-owned and cannot be updated.", errors);
        var name = ReadRequiredNullableString(request, "name", errors);
        var notesHtml = ReadRequiredNullableString(request, "notesHtml", errors);
        var fillHex = ReadRequiredNullableString(request, "fillHex", errors);
        var geometry = ReadRequiredGeometry(request, errors);
        if (!string.IsNullOrWhiteSpace(fillHex))
        {
            ValidateFillHex(fillHex, errors);
        }

        ValidateNotes(notesHtml, errors);

        if (errors.Count > 0)
        {
            return false;
        }

        update = new EditorAreaSaveRequest(
            string.IsNullOrWhiteSpace(name) ? "Area" : name!.Trim(),
            notesHtml,
            string.IsNullOrWhiteSpace(fillHex) ? "#ff6600" : fillHex!.Trim().ToLowerInvariant(),
            geometry!);
        return true;
    }

    /// <summary>
    /// Attempts to parse a complete-draft area update request.
    /// </summary>
    public static bool TryParseUpdate(JsonElement request, out EditorAreaSaveRequest update, out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        update = new EditorAreaSaveRequest(string.Empty, null, string.Empty, EmptyPolygon());
        if (!ValidateObject(request, errors, "Area mutation request must be a JSON object."))
        {
            return false;
        }

        RejectFields(request, SaveServerOwnedFields.Append("regionId"), "The field is server-owned and cannot be updated.", errors);
        var name = ReadRequiredString(request, "name", errors);
        var notesHtml = ReadRequiredNullableString(request, "notesHtml", errors);
        var fillHex = ReadRequiredString(request, "fillHex", errors);
        var geometry = ReadRequiredGeometry(request, errors);
        ValidateRequiredName(name, errors);
        ValidateFillHex(fillHex, errors);
        ValidateNotes(notesHtml, errors);

        if (errors.Count > 0)
        {
            return false;
        }

        update = new EditorAreaSaveRequest(name!.Trim(), notesHtml, fillHex!.Trim().ToLowerInvariant(), geometry!);
        return true;
    }

    /// <summary>
    /// Attempts to parse a geometry-only area update request.
    /// </summary>
    public static bool TryParseGeometryUpdate(JsonElement request, out EditorAreaGeometryUpdateRequest update, out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        update = new EditorAreaGeometryUpdateRequest(EmptyPolygon());
        if (!ValidateObject(request, errors, "Area geometry request must be a JSON object."))
        {
            return false;
        }

        RejectFields(request, GeometryForbiddenFields, "The field cannot be supplied for a geometry-only update.", errors);
        var geometry = ReadRequiredGeometry(request, errors);
        if (errors.Count > 0)
        {
            return false;
        }

        update = new EditorAreaGeometryUpdateRequest(geometry!);
        return true;
    }

    /// <summary>
    /// Attempts to parse a complete region-scoped area order request.
    /// </summary>
    public static bool TryParseOrder(JsonElement request, out EditorAreaOrderRequest update, out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        update = new EditorAreaOrderRequest(Array.Empty<Guid>());
        if (!ValidateObject(request, errors, "Area order request must be a JSON object."))
        {
            return false;
        }

        if (!request.TryGetProperty("areaIds", out var idsProperty))
        {
            errors["areaIds"] = new[] { "The field is required." };
            return false;
        }

        if (idsProperty.ValueKind != JsonValueKind.Array)
        {
            errors["areaIds"] = new[] { "Area IDs must be an array." };
            return false;
        }

        var areaIds = new List<Guid>();
        foreach (var item in idsProperty.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Guid.TryParse(item.GetString(), out var id))
            {
                errors["areaIds"] = new[] { "Every area ID must be a GUID string." };
                return false;
            }

            areaIds.Add(id);
        }

        update = new EditorAreaOrderRequest(areaIds);
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

    private static string? ReadRequiredString(JsonElement request, string name, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty(name, out var property))
        {
            errors[name] = new[] { "The field is required." };
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            errors[name] = new[] { "The field must be a string." };
            return null;
        }

        return property.GetString();
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

    private static Polygon? ReadRequiredGeometry(JsonElement request, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty("geometry", out var property))
        {
            errors["geometry"] = new[] { "The field is required." };
            return null;
        }

        if (property.ValueKind != JsonValueKind.Object)
        {
            errors["geometry"] = new[] { "Geometry must be a GeoJSON Polygon object." };
            return null;
        }

        if (!property.TryGetProperty("type", out var typeProperty) || typeProperty.ValueKind != JsonValueKind.String || typeProperty.GetString() != "Polygon")
        {
            errors["geometry"] = new[] { "Geometry type must be Polygon." };
            return null;
        }

        if (!property.TryGetProperty("coordinates", out var coordinatesProperty) || coordinatesProperty.ValueKind != JsonValueKind.Array)
        {
            errors["geometry.coordinates"] = new[] { "Polygon coordinates must be an array of rings." };
            return null;
        }

        var rings = new List<LinearRing>();
        foreach (var ringProperty in coordinatesProperty.EnumerateArray())
        {
            var ring = ReadRing(ringProperty, errors);
            if (ring == null)
            {
                return null;
            }

            rings.Add(ring);
        }

        if (rings.Count == 0)
        {
            errors["geometry.coordinates"] = new[] { "Polygon must include an exterior ring." };
            return null;
        }

        return new Polygon(rings[0], rings.Skip(1).ToArray()) { SRID = 4326 };
    }

    private static LinearRing? ReadRing(JsonElement ringProperty, Dictionary<string, string[]> errors)
    {
        if (ringProperty.ValueKind != JsonValueKind.Array)
        {
            errors["geometry.coordinates"] = new[] { "Every polygon ring must be an array." };
            return null;
        }

        var coordinates = new List<Coordinate>();
        foreach (var positionProperty in ringProperty.EnumerateArray())
        {
            var coordinate = ReadPosition(positionProperty, errors);
            if (coordinate == null)
            {
                return null;
            }

            coordinates.Add(coordinate);
        }

        if (coordinates.Count < 4)
        {
            errors["geometry.coordinates"] = new[] { "Every polygon ring must contain at least four positions." };
            return null;
        }

        if (!coordinates[0].Equals2D(coordinates[^1]))
        {
            errors["geometry.coordinates"] = new[] { "Every polygon ring must be closed." };
            return null;
        }

        return new LinearRing(coordinates.ToArray()) { SRID = 4326 };
    }

    private static Coordinate? ReadPosition(JsonElement positionProperty, Dictionary<string, string[]> errors)
    {
        if (positionProperty.ValueKind != JsonValueKind.Array || positionProperty.GetArrayLength() != 2)
        {
            errors["geometry.coordinates"] = new[] { "Every position must contain exactly longitude and latitude." };
            return null;
        }

        var values = positionProperty.EnumerateArray().ToArray();
        if (!TryReadFiniteDouble(values[0], out var longitude) || !TryReadFiniteDouble(values[1], out var latitude))
        {
            errors["geometry.coordinates"] = new[] { "Every position must contain finite numeric longitude and latitude." };
            return null;
        }

        if (longitude is < -180 or > 180 || latitude is < -90 or > 90)
        {
            errors["geometry.coordinates"] = new[] { "Coordinates must use longitude -180..180 and latitude -90..90." };
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

    private static void ValidateRequiredName(string? name, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = new[] { "Name is required." };
        }
    }

    private static void ValidateFillHex(string? fillHex, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(fillHex) || !FillHexRegex.IsMatch(fillHex.Trim()))
        {
            errors["fillHex"] = new[] { "Fill color must be a #RRGGBB hex color." };
        }
    }

    private static void ValidateNotes(string? notesHtml, Dictionary<string, string[]> errors)
    {
        if (!string.IsNullOrEmpty(notesHtml) && DataImageSourceRegex.IsMatch(notesHtml))
        {
            errors["notesHtml"] = new[] { "Notes cannot contain data image sources." };
        }
    }

    private static Polygon EmptyPolygon() => new(new LinearRing(Array.Empty<Coordinate>()));
}
