using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

internal sealed partial class TripCompositionApply
{
    /// <summary>Retains measurements only after full source and approved-destination route compatibility checks.</summary>
    private async Task<Segment> CopySegmentAsync(TripCompositionItem item, Segment sourceSegment, Segment? existing, CancellationToken token)
    {
        try { TripCloneCoordinator.ValidateCopyRoute(sourceSegment); }
        catch (InvalidOperationException) { throw RouteInvalid(); }
        var sourceAnchors = sourceSegment.Waypoints.Select(w => w.PlaceId)
            .Concat(new[] { sourceSegment.FromPlaceId, sourceSegment.ToPlaceId }.OfType<Guid>()).Distinct().ToArray();
        if (item.Anchors == null || item.Anchors.Count != sourceAnchors.Length
            || item.Anchors.Select(a => a.SourceId).Distinct().Count() != sourceAnchors.Length
            || item.Anchors.Any(a => !sourceAnchors.Contains(a.SourceId))) throw RouteInvalid();
        var places = destination.Regions.SelectMany(r => r.Places).ToDictionary(p => p.Id);
        var map = new Dictionary<Guid, Guid>();
        foreach (var anchor in item.Anchors)
        {
            var id = anchor.DestinationId ?? (newPlaces.TryGetValue(anchor.SourceId, out var added) ? added : throw RouteInvalid());
            if (!places.TryGetValue(id, out var place)) throw RouteInvalid();
            if (anchor.DestinationId.HasValue && (anchor.RegionId != place.RegionId
                || !TripCompositionFieldValues.Equal(TripCompositionFieldValues.Element(anchor.Location), TripCompositionFieldValues.Pair(place.Location))))
                throw TripCompositionOrdering.Stale();
            map.Add(anchor.SourceId, id);
        }
        var clone = TripCloneCoordinator.CreateSegmentClone(sourceSegment, destination.Id, destination.UserId, map);
        clone.FromPlace = clone.FromPlaceId is { } from ? places[from] : null;
        clone.ToPlace = clone.ToPlaceId is { } to ? places[to] : null;
        foreach (var w in clone.Waypoints) w.Place = places[w.PlaceId];
        try { TripCloneCoordinator.ValidateCopyRoute(clone); }
        catch (InvalidOperationException) { throw RouteInvalid(); }
        if (sourceSegment.RouteGeometry == null)
        {
            // Compare the complete effective anchor chain, including every missing endpoint/location state.
            object Coordinates(Segment s) => new[] { TripCompositionFieldValues.Pair(s.FromPlace?.Location) }
                .Concat(s.Waypoints.OrderBy(w => w.Position).Select(w => TripCompositionFieldValues.Pair(w.Place.Location)))
                .Append(TripCompositionFieldValues.Pair(s.ToPlace?.Location)).ToArray();
            if (!TripCompositionFieldValues.Equal(TripCompositionFieldValues.Element(Coordinates(sourceSegment)), Coordinates(clone))) throw RouteInvalid();
        }
        var idToSave = existing?.Id ?? clone.Id;
        var proposal = new SegmentRouteProposal(idToSave, clone.FromPlaceId, clone.ToPlaceId,
            clone.Waypoints.OrderBy(w => w.Position).Select(w => new SegmentWaypointProposal(w.PlaceId, w.Position, w.RouteVertexIndex)).ToArray(),
            clone.RouteGeometry, new(clone.Mode, clone.TransportProfileId, clone.EstimatedDurationSource, clone.EstimatedDuration?.TotalMinutes, true))
        { PreservedMeasurements = new(clone.EstimatedDistanceKm, clone.EstimatedDuration, clone.EstimatedDurationSource) };
        if (existing != null) proposal = await RequireRouteOriginalAsync(item, existing, proposal, true, token);
        var result = existing == null
            ? await SegmentRouteReconciler.ReconcileNewLockedAsync(db, new(clone.Id, destination.UserId, destination.Id, 0), proposal, token)
            : await SegmentRouteReconciler.ReconcileLockedAsync(db, proposal, false, token);
        if (!result.Succeeded) throw RouteInvalid();
        var saved = db.Segments.Local.Single(s => s.Id == idToSave);
        SegmentRouteProvenance.Clear(saved); SegmentRouteProvenance.Capture(sourceSegment)?.ApplyTo(saved);
        if (existing == null) { saved.Notes = Wayfarer.Util.RichNotes.Normalize(sourceSegment.Notes); saved.Title = sourceSegment.Title; if (!destination.Segments.Contains(saved)) destination.Segments.Add(saved); }
        return saved;
    }

    /// <summary>Authored route changes use the existing reconciliation core and cannot submit trusted preservation/provenance.</summary>
    private async Task EditRouteAsync(TripCompositionItem item, TripCompositionField field, Segment target, CancellationToken token)
    {
        if (field.Operation is not ("edit" or "clear")) throw TripCompositionFieldValues.Invalid();
        var edit = field.Operation == "clear" ? new TripCompositionRouteInput(target.FromPlaceId, target.ToPlaceId,
            target.Waypoints.OrderBy(w => w.Position).Select(w => new SegmentWaypointProposal(w.PlaceId, w.Position, null)).ToArray(),
            null, target.Mode, target.TransportProfileId, target.EstimatedDurationSource.ToString(),
            target.EstimatedDurationSource == EstimatedDurationSource.Manual ? target.EstimatedDuration?.TotalMinutes : null)
            : routeInputs[item];
        if (edit.Waypoints == null || edit.Waypoints.Count > 128 || edit.Waypoints.Any(w => w == null)) throw RouteInvalid();
        var ids = edit.Waypoints.Select(w => w.PlaceId).Concat(new[] { edit.FromPlaceId, edit.ToPlaceId }.OfType<Guid>()).Distinct().ToArray();
        if (item.Anchors == null || item.Anchors.Count != ids.Length || item.Anchors.Select(a => a.DestinationId).Distinct().Count() != ids.Length) throw RouteInvalid();
        foreach (var id in ids)
        {
            var place = destination.Regions.SelectMany(r => r.Places).SingleOrDefault(p => p.Id == id) ?? throw RouteInvalid();
            var anchor = item.Anchors.SingleOrDefault(a => a.DestinationId == id) ?? throw RouteInvalid();
            if (anchor.RegionId != place.RegionId || !TripCompositionFieldValues.Equal(TripCompositionFieldValues.Element(anchor.Location), TripCompositionFieldValues.Pair(place.Location)))
                throw TripCompositionOrdering.Stale();
        }
        if (!Enum.TryParse<EstimatedDurationSource>(edit.DurationSource, out var durationSource)) throw RouteInvalid();
        if (edit.Mode == null) throw RouteInvalid();
        if (edit.Mode != target.Mode || edit.TransportProfileId != target.TransportProfileId)
        {
            var mode = await new TransportProfileCatalog(db).ResolveEditorModeAsync(edit.Mode, target.Mode, token);
            if (mode == null) throw RouteInvalid();
            var profile = string.IsNullOrEmpty(mode) ? null : await db.Set<TransportProfile>().AsNoTracking().SingleOrDefaultAsync(p => p.Key == mode, token);
            if (profile?.Id != edit.TransportProfileId) throw RouteInvalid();
        }
        var geometry = edit.Geometry;
        var proposal = new SegmentRouteProposal(target.Id, edit.FromPlaceId, edit.ToPlaceId, edit.Waypoints,
            geometry, new(edit.Mode, edit.TransportProfileId, durationSource, edit.ManualDurationMinutes, true));
        proposal = await RequireRouteOriginalAsync(item, target, proposal, false, token);
        proposal = TripEditorSegmentMutationService.PreserveSavedMeasurements(target, proposal);
        var result = await SegmentRouteReconciler.ReconcileLockedAsync(db, proposal, false, token);
        if (!result.Succeeded) throw RouteInvalid();
    }

    /// <summary>Uses the ordinary route original and destructive dependency proof, without issuing replacement consent on refusal.</summary>
    private async Task<SegmentRouteProposal> RequireRouteOriginalAsync(TripCompositionItem item, Segment target, SegmentRouteProposal proposal, bool adoptingSource, CancellationToken token)
    {
        var canonical = await SegmentRouteReconciler.LoadAggregateAsync(db, target.Id, token) ?? throw RouteInvalid();
        if (!routeTokens.MatchesRoute(item.ConditionToken, destination.UserId, destination.Id, canonical))
            throw new TripMetadataMutationRejected(409, "composition-route-stale", "The route or its dependencies changed. Review its current original route evidence.");
        var measurement = proposal.Measurement!;
        var editor = new EditorSegmentSaveRequest(proposal.FromPlaceId, proposal.ToPlaceId,
            proposal.Waypoints.Select(w => w.PlaceId).ToArray(), proposal.Waypoints.Select(w => w.RouteVertexIndex).ToArray(),
            measurement.Mode, null, measurement.ManualDurationMinutes, measurement.DurationSource, null, proposal.RouteGeometry, null,
            OriginalRouteToken: item.ConditionToken);
        if (adoptingSource && TripEditorSegmentMutationService.RequiresRouteClearConfirmation(canonical, editor))
            throw new TripMetadataMutationRejected(409, "composition-route-review-required",
                "This anchor change requires clearing the custom route. Clear or repair it with the ordinary route controls, then review the source selection again.");
        var confirmed = TripEditorSegmentMutationService.ApplyConfirmedRouteClear(canonical, editor,
            measurement.TransportProfileId, destination.UserId, item.RouteClearToken, routeConsent);
        if (confirmed == null)
            throw new TripMetadataMutationRejected(409, "composition-route-review-required", "Review and confirm the route/dependency consequences using the existing route controls before applying this selection.");
        return proposal with { RouteGeometry = confirmed.Route, Waypoints = proposal.Waypoints.Select((w, i) =>
            w with { RouteVertexIndex = confirmed.WaypointRouteVertexIndices[i] }).ToArray() };
    }

    private static TripMetadataMutationRejected RouteInvalid() => new(400, "composition-route-incompatible",
        "The complete route, endpoints or ordered Via are incompatible. Choose/create separate source-coordinate Places, or explicitly repair the Place/route with ordinary controls and review again. A misaligned source route itself must be repaired.");

}
