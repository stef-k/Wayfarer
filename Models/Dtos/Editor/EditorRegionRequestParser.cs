using System.Text.Json;

namespace Wayfarer.Models.Dtos.Editor;

/// <summary>
/// Parses and validates region mutation JSON while preserving ownership-first request handling.
/// </summary>
internal static class EditorRegionRequestParser
{
    /// <summary>Reserved display name used by the shadow region.</summary>
    public const string ShadowRegionName = "Unassigned Places";

    private static readonly string[] ServerOwnedFields =
    {
        "id",
        "tripId",
        "displayOrder",
        "isShadow",
        "capabilities"
    };

    /// <summary>
    /// Attempts to parse a complete-draft region save request.
    /// </summary>
    public static bool TryParseSave(
        JsonElement request,
        out EditorRegionSaveRequest update,
        out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        update = new EditorRegionSaveRequest(string.Empty, null, null, null);

        if (request.ValueKind != JsonValueKind.Object)
        {
            errors["request"] = new[] { "Region save request must be a JSON object." };
            return false;
        }

        RejectServerOwnedFields(request, errors);

        var name = ReadRequiredString(request, "name", errors);
        var notesHtml = ReadRequiredNullableString(request, "notesHtml", errors);
        var coverImage = ReadRequiredCoverImage(request, errors);
        var center = ReadRequiredCenter(request, errors);

        ValidateName(name, errors);
        ValidateCoverImage(coverImage?.RawUrl, errors);
        ValidateNotesHtml(notesHtml, errors);

        if (errors.Count > 0)
        {
            return false;
        }

        update = new EditorRegionSaveRequest(name!, EditorRichNotesRequestHtml.NormalizeForPersistence(notesHtml), coverImage, center);
        return true;
    }

    /// <summary>
    /// Attempts to parse a region order request.
    /// </summary>
    public static bool TryParseOrder(
        JsonElement request,
        out EditorRegionOrderRequest update,
        out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        update = new EditorRegionOrderRequest(Array.Empty<Guid>());

        if (request.ValueKind != JsonValueKind.Object)
        {
            errors["request"] = new[] { "Region order request must be a JSON object." };
            return false;
        }

        if (!request.TryGetProperty("regionIds", out var regionIdsProperty))
        {
            errors["regionIds"] = new[] { "The field is required." };
            return false;
        }

        if (regionIdsProperty.ValueKind != JsonValueKind.Array)
        {
            errors["regionIds"] = new[] { "Region IDs must be an array." };
            return false;
        }

        var regionIds = new List<Guid>();
        foreach (var item in regionIdsProperty.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Guid.TryParse(item.GetString(), out var id))
            {
                errors["regionIds"] = new[] { "Every region ID must be a GUID string." };
                return false;
            }

            regionIds.Add(id);
        }

        update = new EditorRegionOrderRequest(regionIds);
        return true;
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

    private static EditorImageUpdateRequest? ReadRequiredCoverImage(JsonElement request, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty("coverImage", out var property))
        {
            errors["coverImage"] = new[] { "The field is required." };
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Object)
        {
            errors["coverImage"] = new[] { "The field must be an object or null." };
            return null;
        }

        if (!property.TryGetProperty("rawUrl", out var rawUrlProperty))
        {
            errors["coverImage.rawUrl"] = new[] { "The field is required." };
            return null;
        }

        if (rawUrlProperty.ValueKind == JsonValueKind.Null)
        {
            return new EditorImageUpdateRequest(null);
        }

        if (rawUrlProperty.ValueKind != JsonValueKind.String)
        {
            errors["coverImage.rawUrl"] = new[] { "The field must be a string or null." };
            return null;
        }

        return new EditorImageUpdateRequest(rawUrlProperty.GetString());
    }

    private static EditorCoordinateDto? ReadRequiredCenter(JsonElement request, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty("center", out var property))
        {
            errors["center"] = new[] { "The field is required." };
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Object)
        {
            errors["center"] = new[] { "The field must be an object or null." };
            return null;
        }

        var latitude = ReadRequiredDouble(property, "latitude", "center.latitude", errors);
        var longitude = ReadRequiredDouble(property, "longitude", "center.longitude", errors);
        if (latitude is < -90 or > 90)
        {
            errors["center.latitude"] = new[] { "Latitude must be between -90 and 90." };
        }

        if (longitude is < -180 or > 180)
        {
            errors["center.longitude"] = new[] { "Longitude must be between -180 and 180." };
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

    private static void ValidateName(string? name, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = new[] { "Name is required." };
            return;
        }

        if (string.Equals(name.Trim(), ShadowRegionName, StringComparison.OrdinalIgnoreCase))
        {
            errors["name"] = new[] { "The reserved shadow region name cannot be used." };
        }
    }

    private static void ValidateCoverImage(string? rawUrl, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return;
        }

        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors["coverImage.rawUrl"] = new[] { "Cover image URL must be an absolute HTTP or HTTPS URL." };
        }
    }

    private static void ValidateNotesHtml(string? notesHtml, Dictionary<string, string[]> errors)
    {
        if (EditorRichNotesRequestHtml.ContainsDataImageSource(notesHtml))
        {
            errors["notesHtml"] = new[] { "Notes images must use external image URLs, not data:image sources." };
        }
    }
}
