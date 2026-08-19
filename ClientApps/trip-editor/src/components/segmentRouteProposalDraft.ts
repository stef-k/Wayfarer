import type { SegmentDraftRoutePreview } from '../map/segmentRouteDraftPreviewLayer';
import type { AcceptedExternalRouteProposal, EditorSegmentDraft, EditorTripState, ExternalRouteProposal } from '../types';

/** Snapshots every draft authority that binds proposal preview and acceptance. */
export const routeProposalDraftContextKey = (
  draft: EditorSegmentDraft, state: EditorTripState, draftRevision: number
): string => JSON.stringify({
  segmentId: draft.id,
  transportProfileId: draft.transportProfileId,
  aggregateConcurrencyToken: draft.aggregateConcurrencyToken,
  anchorFingerprint: JSON.stringify([draft.fromPlaceId, ...draft.waypointPlaceIds, draft.toPlaceId]
    .map(id => id ? [id, state.placesById[id]?.location ?? null] : null)),
  routeFingerprint: JSON.stringify(draft.route),
  draftRevision
});

/** Converts provider coordinates into the existing draft GeoJSON and complete intermediate indices. */
export const applyAcceptedRouteProposal = (
  draft: EditorSegmentDraft,
  proposal: AcceptedExternalRouteProposal
): boolean => {
  if (proposal.segmentId !== draft.id) return false;
  draft.route = { type: 'LineString', coordinates: proposal.geometry.map(item => [item.longitude, item.latitude]) };
  draft.waypointRows.forEach((row, index) => { row.routeVertexIndex = proposal.waypointIndices[index + 1] ?? null; });
  return true;
};

/** Converts a non-persisted provider proposal into the existing map-preview ownership contract. */
export const toRouteProposalPreview = (
  draft: EditorSegmentDraft,
  proposal: ExternalRouteProposal,
  identity: string
): SegmentDraftRoutePreview => ({
  fromPlaceId: draft.fromPlaceId,
  identity: `${identity}-provider-proposal`,
  route: { type: 'LineString', coordinates: proposal.geometry.map(item => [item.longitude, item.latitude]) },
  segmentId: draft.id,
  toPlaceId: draft.toPlaceId
});

/** Keeps proposal preview/adoption state out of SegmentManager orchestration. */
export const createSegmentRouteProposalDraftController = (
  draft: EditorSegmentDraft,
  identity: () => string,
  emitPreview: (preview: SegmentDraftRoutePreview | null) => void
): {
  accept: (proposal: AcceptedExternalRouteProposal) => boolean;
  publishIfPresent: () => boolean;
  preview: (proposal: ExternalRouteProposal | null) => void;
} => {
  let current: ExternalRouteProposal | null = null;
  return {
    accept: proposal => {
      if (!applyAcceptedRouteProposal(draft, proposal)) return false;
      current = null;
      return true;
    },
    publishIfPresent: () => {
      if (!current) return false;
      emitPreview(toRouteProposalPreview(draft, current, identity()));
      return true;
    },
    preview: proposal => { current = proposal; }
  };
};
