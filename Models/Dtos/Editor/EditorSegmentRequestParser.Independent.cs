using System.Text.Json;
using Wayfarer.Util;

namespace Wayfarer.Models.Dtos.Editor;

/// <summary>Strict fail-closed route/Notes dialect on the existing Web update endpoint.</summary>
internal static partial class EditorSegmentRequestParser
{
    /// <summary>Rejects legacy fields, duplicate/unknown members and unsupported contracts without downgrade.</summary>
    private static EditorSegmentNotesIntent? ReadIndependentIntent(JsonElement request, Dictionary<string, string[]> errors)
    {
        var allowed = new[] { "contract", "originalRouteToken", "fromPlaceId", "toPlaceId", "waypointPlaceIds",
            "waypointRouteVertexIndices", "mode", "estimatedDistanceKm", "estimatedDurationMinutes", "estimatedDurationSource",
            "route", "proposal", "expected", "changes" };
        StrictFields(request, allowed, errors);
        if (!request.TryGetProperty("contract", out var contract) || contract.ValueKind != JsonValueKind.String
            || contract.GetString() != "segment-route-notes-1") errors["contract"] = ["Unsupported Segment route contract. Reload the editor."];
        if (request.TryGetProperty("proposal", out var proposal) && proposal.ValueKind != JsonValueKind.Null)
            StrictFields(proposal, ["proposalId", "protectedContext", "manualDurationOverride"], errors);
        if (request.TryGetProperty("route", out var route) && route.ValueKind != JsonValueKind.Null)
            StrictFields(route, ["type", "coordinates"], errors);
        var hasExpected = request.TryGetProperty("expected", out var expected);
        var hasChanges = request.TryGetProperty("changes", out var changes);
        if (hasExpected) StrictFields(expected, ["notesHtml"], errors);
        var original = default(JsonElement);
        var hasOriginal = hasExpected && expected.ValueKind == JsonValueKind.Object && expected.TryGetProperty("notesHtml", out original);
        if (hasOriginal && original.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            errors["notesHtml"] = ["Original Notes must be a string or null."];
        if (!hasChanges) return null;
        StrictFields(changes, ["notesHtml"], errors);
        if (changes.ValueKind != JsonValueKind.Object || !changes.TryGetProperty("notesHtml", out var desired)) return null;
        if (desired.ValueKind == JsonValueKind.Null) return null;
        if (desired.ValueKind != JsonValueKind.String)
        {
            errors["notesHtml"] = ["Notes must be a string or null."];
            return null;
        }
        if (!hasOriginal || original.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
        {
            errors["notesHtml"] = ["Authored Notes require the exact nullable original."];
            return null;
        }
        ValidateNotes(desired.GetString(), errors);
        return new(original.GetString(), RichNotes.Normalize(desired.GetString())!);
    }

    /// <summary>Requires exact member spelling and unique fields at each new dialect object boundary.</summary>
    private static void StrictFields(JsonElement value, string[] allowed, Dictionary<string, string[]> errors)
    {
        if (value.ValueKind != JsonValueKind.Object) { errors["request"] = ["An object is required."]; return; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!allowed.Contains(field.Name) || !seen.Add(field.Name)) errors["request"] = ["Unknown or duplicate fields are not supported."];
    }
}
