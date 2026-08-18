import type { ExternalRouteProposal, Guid } from '../types';

export interface SegmentRouteProposalState {
  generating: boolean;
  error: string | null;
  proposal: ExternalRouteProposal | null;
  controller: AbortController | null;
  profileId: Guid | null;
}

/** Owns independent request/proposal state keyed strictly by Segment identity. */
export const createSegmentRouteProposalStore = (): {
  states: Record<Guid, SegmentRouteProposalState>;
  get: (segmentId: Guid, profileId: Guid | null) => SegmentRouteProposalState;
  invalidateProfile: (segmentId: Guid, profileId: Guid | null) => boolean;
  discard: (segmentId: Guid) => void;
} => {
  const states: Record<Guid, SegmentRouteProposalState> = {};
  const fresh = (profileId: Guid | null): SegmentRouteProposalState => ({
    generating: false, error: null, proposal: null, controller: null, profileId
  });
  const get = (segmentId: Guid, profileId: Guid | null): SegmentRouteProposalState =>
    states[segmentId] ??= fresh(profileId);
  return {
    states,
    get,
    invalidateProfile: (segmentId, profileId) => {
      const current = get(segmentId, profileId);
      if (current.profileId === profileId) return false;
      current.controller?.abort();
      Object.assign(current, fresh(profileId));
      return true;
    },
    discard: segmentId => {
      const current = states[segmentId];
      if (!current) return;
      current.controller?.abort();
      Object.assign(current, fresh(current.profileId));
    }
  };
};
