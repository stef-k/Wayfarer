using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wayfarer.Models.Dtos.Editor;

/// <summary>
/// Parses and validates complete-draft metadata mutation JSON while preserving omitted-field detection.
/// </summary>
internal static class EditorTripMetadataUpdateRequestParser
{
    private static readonly string[] ServerOwnedFields =
    {
        "shareProgressEnabled",
        "publicUrl",
        "progressPublicUrl",
        "updatedAt"
    };

    private static readonly Regex DataImageSourceRegex = new(
        @"<img\b[^>]*?\bsrc\s*=\s*[""']?\s*data:image/",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Attempts to parse an editor metadata update request and returns field-keyed validation errors.
    /// </summary>
    public static bool TryParse(
        JsonElement request,
        out EditorTripMetadataUpdateRequest update,
        out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        update = new EditorTripMetadataUpdateRequest(string.Empty, null, false, null, null, null);

        if (request.ValueKind != JsonValueKind.Object)
        {
            errors["request"] = new[] { "Metadata update request must be a JSON object." };
            return false;
        }

        RejectServerOwnedFields(request, errors);

        var name = ReadRequiredString(request, "name", errors);
        var notesHtml = ReadRequiredNullableString(request, "notesHtml", errors);
        var isPublic = ReadRequiredBoolean(request, "isPublic", errors);
        var coverImage = ReadRequiredCoverImage(request, errors);
        var center = ReadRequiredCenter(request, errors);
        var zoom = ReadRequiredZoom(request, errors);

        ValidateName(name, errors);
        ValidateCoverImage(coverImage?.RawUrl, errors);
        ValidateNotesHtml(notesHtml, errors);

        if (errors.Count > 0)
        {
            return false;
        }

        update = new EditorTripMetadataUpdateRequest(name!, notesHtml, isPublic!.Value, coverImage, center, zoom);
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

    private static int? ReadRequiredZoom(JsonElement request, Dictionary<string, string[]> errors)
    {
        if (!request.TryGetProperty("zoom", out var property))
        {
            errors["zoom"] = new[] { "The field is required." };
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var zoom))
        {
            errors["zoom"] = new[] { "Zoom must be an integer from 0 through 19." };
            return null;
        }

        if (zoom is < 0 or > 19)
        {
            errors["zoom"] = new[] { "Zoom must be an integer from 0 through 19." };
        }

        return zoom;
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
        if (!string.IsNullOrEmpty(notesHtml) && DataImageSourceRegex.IsMatch(notesHtml))
        {
            errors["notesHtml"] = new[] { "Notes images must use external image URLs, not data:image sources." };
        }
    }
}
