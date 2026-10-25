using System.Text.Json;
using Wayfarer.Services;

namespace Wayfarer.Models.Dtos.Editor;

/// <summary>Complete visible consequences; identities describe the exact affected scope, never authorization.</summary>
public sealed record PlaceDeletionPreviewDto(int ContractVersion, Guid TripId, Guid PlaceId, Guid RegionId,
    string PlaceName, EditorLifecycleDependencySampleDto DeletedSegments,
    EditorLifecycleDependencySampleDto UpdatedSegments, EditorLifecycleAssociationSampleDto DetachedWaypoints,
    int UnlinkedVisits, int DeletedCandidates, IReadOnlyList<Guid> PlaceOrderIds, IReadOnlyList<Guid>? SegmentOrderIds,
    RegionDeletionConfirmationDto Confirmation);

/// <summary>Exact committed scope accompanied by authoritative editor slices.</summary>
public sealed record PlaceDeletionResultDto(Guid TripId, Guid PlaceId, Guid RegionId,
    IReadOnlyList<Guid> UpdatedSegmentIds, IReadOnlyList<EditorLifecycleWaypointAssociationDto> DetachedWaypointAssociations);

/// <summary>Strict preview/confirmation parsing; missing approval never implies consent.</summary>
public static class PlaceDeletionRequestParser
{
    /// <summary>Accepts an empty preview object or just the confirmed operation's token.</summary>
    public static async Task<string?> ParseAsync(Stream body, bool mutation, CancellationToken token)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: token);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var fields = document.RootElement.EnumerateObject().ToArray();
            if (fields.Length == 0) return null;
            if (!mutation || fields.Length != 1 || fields[0].Name != "confirmationToken"
                || fields[0].Value.ValueKind != JsonValueKind.String || fields[0].Value.GetString()!.Length > 16384)
                throw new JsonException();
            return fields[0].Value.GetString();
        }
        catch (JsonException)
        {
            throw new TripMetadataMutationRejected(400, "place-deletion-request-invalid", "Supply only the documented confirmationToken, or an empty preview object.");
        }
    }
}
