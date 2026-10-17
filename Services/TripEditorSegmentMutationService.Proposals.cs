using System.Text.Json;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services.ExternalRouting;

namespace Wayfarer.Services;

/// <summary>Save-owned provider proposal validation, measurements and provenance.</summary>
public sealed partial class TripEditorSegmentMutationService
{
    /// <summary>Builds the complete ordinary route mutation before optional trusted measurement preservation.</summary>
    private static SegmentRouteProposal BuildProposal(
        Guid segmentId,
        EditorSegmentSaveRequest request,
        (string Key, Guid? ProfileId) mode) =>
        new(segmentId, request.FromPlaceId, request.ToPlaceId,
            request.WaypointPlaceIds.Select((id, index) => new SegmentWaypointProposal(id, index, request.WaypointRouteVertexIndices[index])).ToArray(), request.Route,
            new(mode.Key, mode.ProfileId, request.EstimatedDurationSource, request.EstimatedDurationMinutes),
            ApplyNotes: true, NotesHtml: request.NotesHtml);

    private static EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>> ProposalFailed(string code) =>
        EditorRegionMutationOutcome<EditorMutationResult<EditorSegmentDto>>.ValidationFailed(
            new() { ["proposal"] = ["The route proposal cannot be saved. Discard it or explicitly generate a new proposal."] }, code);

    /// <summary>Preserves only validated proposal estimates or unchanged database-owned persistent route measurements.</summary>
    private static SegmentRouteProposal BuildSavedRoute(
        Segment canonical, EditorSegmentSaveRequest request, (string Key, Guid? ProfileId) mode,
        ExternalRouteProposalBinding? binding)
    {
        var route = BuildProposal(canonical.Id, request, mode);
        var retained = SegmentRouteReconciler.SameRouteIdentity(canonical, route);
        if (binding == null && !retained) return route;
        var manual = request.EstimatedDurationSource == EstimatedDurationSource.Manual;
        var provider = SegmentRouteProvenance.Capture(canonical)?.IsQualifiedProvider() == true;
        var duration = binding != null
            ? binding.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null
            : !manual && canonical.EstimatedDurationSource == EstimatedDurationSource.Manual ? null : canonical.EstimatedDuration;
        var source = manual ? EstimatedDurationSource.Manual : EstimatedDurationSource.Automatic;
        var recalculateDuration = binding == null && !provider && !manual
            && (canonical.EstimatedDurationSource == EstimatedDurationSource.Manual
                || mode.Key != canonical.Mode || mode.ProfileId != canonical.TransportProfileId);
        return route with { PreservedMeasurements = new(
            binding != null ? binding.DistanceMetres / 1000d : canonical.EstimatedDistanceKm,
            duration, source, manual ? request.EstimatedDurationMinutes : null, recalculateDuration) };
    }

    /// <summary>Copies provenance exclusively from the final validated binding, before the caller commits.</summary>
    private static void ApplyProposalProvenance(Segment segment, ExternalRouteProposalBinding binding)
    {
        segment.RouteInstructionsJson = JsonSerializer.Serialize(binding.Instructions ?? []);
        segment.RouteProvider = binding.ProviderKey;
        segment.RouteProviderConfigurationId = null;
        segment.RouteProviderConfigurationVersion = null;
        segment.RouteTransportProfileId = binding.TransportProfileId;
        segment.RouteMappingMode = binding.MappingMode;
        segment.RouteGeneratedAt = binding.GeneratedAt?.ToUniversalTime();
        segment.RouteAttribution = binding.Attribution;
        segment.RouteStorageMode = binding.StorageMode;
    }
}
