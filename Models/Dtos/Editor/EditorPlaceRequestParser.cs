using System.Text.Json;

namespace Wayfarer.Models.Dtos.Editor;

/// <summary>
/// Parses and validates place mutation JSON while preserving ownership-first request handling.
/// </summary>
internal static class EditorPlaceRequestParser
{
    private static readonly string[] ServerOwnedFields =
    {
        "id",
        "tripId",
        "displayOrder",
        "visitSummary",
        "capabilities"
    };

    /// <summary>
    /// Attempts to parse a complete-draft place create request.
    /// </summary>
    public static bool TryParseCreate(
        JsonElement request,
        IReadOnlySet<string> iconNames,
        IReadOnlySet<string> markerColors,
        out EditorPlaceCreateRequest update,
        out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        update = new EditorPlaceCreateRequest(string.Empty, null, null, null, string.Empty, string.Empty, false);

        if (!ValidateObject(request, errors))
        {
            return false;
        }

        RejectServerOwnedFields(request, errors);
        RejectPathOwnedField(request, "regionId", errors);

        var fields = ReadSaveFields(request, errors);
        ValidateFields(fields, iconNames, markerColors, errors);
        if (errors.Count > 0)
        {
            return false;
        }

        update = new EditorPlaceCreateRequest(
            fields.Name!,
            EditorRichNotesRequestHtml.NormalizeForPersistence(fields.NotesHtml),
            fields.Address,
            fields.Location,
            fields.IconName!,
            fields.MarkerColor!,
            fields.ReverseGeocode!.Value);
        return true;
    }

    /// <summary>
    /// Attempts to parse a complete-draft place update request.
    /// </summary>
    public static bool TryParseUpdate(
        JsonElement request,
        IReadOnlySet<string> iconNames,
        IReadOnlySet<string> markerColors,
        out EditorPlaceUpdateRequest update,
        out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        update = new EditorPlaceUpdateRequest(Guid.Empty, string.Empty, null, null, null, string.Empty, string.Empty, false);

        if (!ValidateObject(request, errors))
        {
            return false;
        }

        RejectServerOwnedFields(request, errors);
        var regionId = ReadRequiredGuid(request, "regionId", errors);
        var fields = ReadSaveFields(request, errors);
        ValidateFields(fields, iconNames, markerColors, errors);
        if (errors.Count > 0)
        {
            return false;
        }

        update = new EditorPlaceUpdateRequest(
            regionId!.Value,
            fields.Name!,
            EditorRichNotesRequestHtml.NormalizeForPersistence(fields.NotesHtml),
            fields.Address,
            fields.Location,
            fields.IconName!,
            fields.MarkerColor!,
            fields.ReverseGeocode!.Value);
        return true;
    }

    /// <summary>
    /// Attempts to parse a complete region-scoped place order request.
    /// </summary>
    public static bool TryParseOrder(JsonElement request, out EditorPlaceOrderRequest update, out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        update = new EditorPlaceOrderRequest(Array.Empty<Guid>());

        if (!ValidateObject(request, errors))
        {
            return false;
        }

        if (!request.TryGetProperty("placeIds", out var placeIdsProperty))
        {
            errors["placeIds"] = new[] { "The field is required." };
            return false;
        }

        if (placeIdsProperty.ValueKind != JsonValueKind.Array)
        {
            errors["placeIds"] = new[] { "Place IDs must be an array." };
            return false;
        }

        var placeIds = new List<Guid>();
        foreach (var item in placeIdsProperty.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Guid.TryParse(item.GetString(), out var id))
            {
                errors["placeIds"] = new[] { "Every place ID must be a GUID string." };
                return false;
            }

            placeIds.Add(id);
        }

        update = new EditorPlaceOrderRequest(placeIds);
        return true;
    }

    private static PlaceSaveFields ReadSaveFields(JsonElement request, Dictionary<string, string[]> errors) =>
        new(
            ReadRequiredString(request, "name", errors),
            ReadRequiredNullableString(request, "notesHtml", errors),
            ReadRequiredNullableString(request, "address", errors),
            ReadRequiredLocation(request, errors),
            ReadRequiredString(request, "iconName", errors),
            ReadRequiredString(request, "markerColor", errors),
            ReadRequiredBoolean(request, "reverseGeocode", errors));

    private static void ValidateFields(
        PlaceSaveFields fields,
        IReadOnlySet<string> iconNames,
        IReadOnlySet<string> markerColors,
        Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(fields.Name))
        {
            errors["name"] = new[] { "Name is required." };
        }

        if (fields.IconName != null && !iconNames.Contains(fields.IconName))
        {
            errors["iconName"] = new[] { "Icon name must be one of the editor options." };
        }

        if (fields.MarkerColor != null && !markerColors.Contains(fields.MarkerColor))
        {
            errors["markerColor"] = new[] { "Marker color must be one of the editor options." };
        }

        if (fields.ReverseGeocode == true && fields.Location == null)
        {
            errors["reverseGeocode"] = new[] { "Reverse geocoding requires a location." };
        }

        if (ContainsDataImageSource(fields.NotesHtml))
        {
            errors["notesHtml"] = new[] { "Notes cannot contain data image sources." };
        }
    }

    private static bool ValidateObject(JsonElement request, Dictionary<string, string[]> errors)
    {
        if (request.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        errors["request"] = new[] { "Place mutation request must be a JSON object." };
        return false;
    }

    private static void RejectServerOwnedFields(JsonElement request, Dictionary<string, string[]> errors)
    {
        foreach (var field in ServerOwnedFields)
        {
            if (request.TryGetProperty(field, out _))
            {
                errors[field] = new[] { "The field is server-owned and cannot be updated." };
            }
        }
    }

    private static void RejectPathOwnedField(JsonElement request, string field, Dictionary<string, string[]> errors)
    {
        if (request.TryGetProperty(field, out _))
        {
            errors[field] = new[] { "The field is path-owned and cannot be supplied in the request body." };
        }
    }

    private static Guid? ReadRequiredGuid(JsonElement request, string name, Dictionary<string, string[]> errors)
    {
        var value = ReadRequiredString(request, name, errors);
        if (value == null)
        {
            return null;
        }

        if (!Guid.TryParse(value, out var id))
        {
            errors[name] = new[] { "The field must be a GUID string." };
            return null;
        }

        return id;
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

    private static bool? ReadRequiredBoolean(JsonElement request, string name, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty(name, out var property))
        {
            errors[name] = new[] { "The field is required." };
            return null;
        }

        if (property.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            errors[name] = new[] { "The field must be a boolean." };
            return null;
        }

        return property.GetBoolean();
    }

    private static EditorCoordinateDto? ReadRequiredLocation(JsonElement request, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty("location", out var property))
        {
            errors["location"] = new[] { "The field is required." };
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Object)
        {
            errors["location"] = new[] { "The field must be an object or null." };
            return null;
        }

        var latitude = ReadRequiredDouble(property, "latitude", "location.latitude", errors);
        var longitude = ReadRequiredDouble(property, "longitude", "location.longitude", errors);
        if (latitude is < -90 or > 90)
        {
            errors["location.latitude"] = new[] { "Latitude must be between -90 and 90." };
        }

        if (longitude is < -180 or > 180)
        {
            errors["location.longitude"] = new[] { "Longitude must be between -180 and 180." };
        }

        return latitude.HasValue && longitude.HasValue ? new EditorCoordinateDto(latitude.Value, longitude.Value) : null;
    }

    private static double? ReadRequiredDouble(JsonElement request, string propertyName, string fieldKey, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty(propertyName, out var property))
        {
            errors[fieldKey] = new[] { "The field is required." };
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var value) || double.IsNaN(value) || double.IsInfinity(value))
        {
            errors[fieldKey] = new[] { "The field must be a finite number." };
            return null;
        }

        return value;
    }

    private static bool ContainsDataImageSource(string? notesHtml) =>
        EditorRichNotesRequestHtml.ContainsDataImageSource(notesHtml);

    private sealed record PlaceSaveFields(
        string? Name,
        string? NotesHtml,
        string? Address,
        EditorCoordinateDto? Location,
        string? IconName,
        string? MarkerColor,
        bool? ReverseGeocode);
}
