import { computed, shallowRef } from 'vue';
import { buildSegmentRequest } from './regionPlaceDrafts';
import type { SegmentDraftRoutePreview } from '../map/segmentRouteDraftPreviewLayer';
import type { EditorSegmentDraft, EditorTripState, ExternalRouteProposal } from '../types';

/** Snapshots every draft authority that binds proposal preview and Save. */
export const routeProposalDraftContextKey = (
  draft: EditorSegmentDraft, state: EditorTripState, draftRevision: number
): string => JSON.stringify({
  segmentId: draft.id,
  transportProfileId: draft.transportProfileId,
  aggregateConcurrencyToken: draft.aggregateConcurrencyToken,
  anchorFingerprint: JSON.stringify([draft.fromPlaceId, ...draft.waypointPlaceIds, draft.toPlaceId]
    .map(id => id ? [id, state.placesById[id]?.location ?? null] : null)),
  routeFingerprint: JSON.stringify([draft.route, draft.waypointRows?.map(row => [row.placeId, row.routeVertexIndex])]),
  draftRevision
});

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

/** Keeps pending proposal state separate from normal draft fields until canonical Save succeeds. */
export const createSegmentRouteProposalDraftController = (
  draft: EditorSegmentDraft,
  identity: () => string,
  emitPreview: (preview: SegmentDraftRoutePreview | null) => void,
  persistedBaseline: () => EditorSegmentDraft
) => {
  const current = shallowRef<ExternalRouteProposal | null>(null);
  const durationAtPreview = shallowRef('');
  const manualOverride = (): boolean => current.value !== null && draft.estimatedDurationSource === 'Manual'
    && (JSON.stringify([draft.estimatedDurationSource, draft.estimatedDurationMinutes]) !== durationAtPreview.value
      // Missing estimates must also preserve Manual edits that predate generation.
      || (current.value.durationSeconds == null
        && (draft.estimatedDurationSource !== persistedBaseline().estimatedDurationSource
          || Number(draft.estimatedDurationMinutes) !== Number(persistedBaseline().estimatedDurationMinutes))));
  return {
    hasProposal: computed(() => current.value !== null),
    manualOverride: computed(manualOverride),
    buildRequest: () => {
      const request = buildSegmentRequest(draft);
      const proposal = current.value;
      if (!proposal) return request;
      request.route = { type: 'LineString', coordinates: proposal.geometry.map(item => [item.longitude, item.latitude]) };
      request.waypointRouteVertexIndices = proposal.waypointIndices.slice(1, -1);
      request.proposal = { proposalId: proposal.proposalId, protectedContext: proposal.protectedContext,
        manualDurationOverride: manualOverride() };
      if (proposal.distanceMetres != null) request.estimatedDistanceKm = proposal.distanceMetres / 1000;
      if (proposal.durationSeconds != null && !manualOverride()) {
        request.estimatedDurationMinutes = proposal.durationSeconds / 60;
        request.estimatedDurationSource = 'Automatic';
      }
      return request;
    },
    publishIfPresent: (): boolean => {
      if (!current.value) return false;
      emitPreview(toRouteProposalPreview(draft, current.value, identity()));
      return true;
    },
    preview: (proposal: ExternalRouteProposal | null): void => {
      current.value = proposal;
      durationAtPreview.value = JSON.stringify([draft.estimatedDurationSource, draft.estimatedDurationMinutes]);
    }
  };
};
