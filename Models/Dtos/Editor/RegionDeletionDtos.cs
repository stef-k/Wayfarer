using System.Text.Json;

namespace Wayfarer.Models.Dtos.Editor;

/// <summary>Explicit independent child dispositions; neither field has a server default.</summary>
public record RegionDeletionPreviewRequestDto(string Places, string Areas);

/// <summary>One confirmed attempt against the exact previewed persisted contents.</summary>
public sealed record RegionDeletionRequestDto(string Places, string Areas, string? ConfirmationToken)
    : RegionDeletionPreviewRequestDto(Places, Areas);

/// <summary>Canonical destination identity, or a proposed identity which has not been inserted.</summary>
public sealed record RegionDeletionDestinationDto(string Mode, Guid? RegionId, string? Name);

/// <summary>Complete counts and bounded identity samples for the selected dispositions.</summary>
public sealed record RegionDeletionConsequencesDto(
    EditorLifecycleDependencySampleDto MovedPlaces, EditorLifecycleDependencySampleDto DeletedPlaces,
    EditorLifecycleDependencySampleDto MovedAreas, EditorLifecycleDependencySampleDto DeletedAreas,
    EditorLifecycleDependencySampleDto DeletedSegments, EditorLifecycleDependencySampleDto UpdatedSegments,
    EditorLifecycleAssociationSampleDto DetachedWaypoints);

/// <summary>Discloses source metadata loss without copying raw HTML into the dialog.</summary>
public sealed record RegionDeletionMetadataLossDto(bool HasNotes, bool HasCover, bool HasCenter);

/// <summary>Ten-minute, purpose-separated content confirmation, never an operation receipt.</summary>
public sealed record RegionDeletionConfirmationDto(string Token, DateTimeOffset ExpiresAt);

/// <summary>Fresh authoritative, non-mutating Region deletion preview.</summary>
public sealed record RegionDeletionPreviewDto(
    int ContractVersion, Guid TripId, Guid SourceRegionId, string SourceRegionName, string Places, string Areas,
    RegionDeletionDestinationDto Destination, RegionDeletionConsequencesDto Consequences,
    RegionDeletionMetadataLossDto RegionMetadataLoss, RegionDeletionConfirmationDto Confirmation);

/// <summary>Complete committed identities; editor slices are supplied separately in the standard envelope.</summary>
public sealed record RegionDeletionResultDto(
    Guid TripId, Guid SourceRegionId, string Places, string Areas, Guid? DestinationRegionId,
    IReadOnlyList<Guid> MovedPlaceIds, IReadOnlyList<Guid> MovedAreaIds, IReadOnlyList<Guid> UpdatedSegmentIds,
    IReadOnlyList<EditorLifecycleWaypointAssociationDto> DetachedWaypointAssociations);

/// <summary>Strict additive request parser: rejects absent choices, numeric enums, duplicates and extra fields.</summary>
public static class RegionDeletionRequestParser
{
    /// <summary>Reads only the frozen fields for the preview or confirmed operation.</summary>
    public static async Task<RegionDeletionRequestDto> ParseAsync(Stream body, bool mutation, CancellationToken token)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: token);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name) || (property.Name != "places" && property.Name != "areas"
                    && !(mutation && property.Name == "confirmationToken"))) throw new JsonException();
            }
            var places = Choice(root, "places");
            var areas = Choice(root, "areas");
            string? confirmation = null;
            if (root.TryGetProperty("confirmationToken", out var value))
            {
                if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 16384) throw new JsonException();
                confirmation = value.GetString();
            }
            return new(places, areas, confirmation);
        }
        catch (JsonException)
        {
            throw new RegionDeletionRejectedException(400, "region-deletion-request-invalid",
                "Supply explicit places and areas choices ('keep' or 'delete') and only the documented fields.");
        }
    }

    private static string Choice(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || value.GetString() is not ("keep" or "delete")) throw new JsonException();
        return value.GetString()!;
    }
}

/// <summary>A definite rejection before commit; other exceptions retain unknown-outcome semantics.</summary>
public sealed class RegionDeletionRejectedException(int status, string code, string message,
    RegionDeletionPreviewDto? preview = null) : Exception(message)
{
    /// <summary>HTTP status for this definite rejection.</summary>
    public int Status { get; } = status;
    /// <summary>Stable machine-readable recovery code.</summary>
    public string Code { get; } = code;
    /// <summary>Optional fresh owned preview; it always requires another explicit confirmation.</summary>
    public RegionDeletionPreviewDto? Preview { get; } = preview;
}
