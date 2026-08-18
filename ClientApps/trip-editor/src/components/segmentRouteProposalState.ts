import { reactive } from 'vue';
import type { ExternalRouteProposal, Guid } from '../types';

export interface SegmentRouteProposalState {
  generating: boolean;
  error: string | null;
  proposal: ExternalRouteProposal | null;
  controller: AbortController | null;
  contextKey: string;
  requestId: number;
}

/** Owns independent request/proposal state keyed strictly by Segment identity. */
export const createSegmentRouteProposalStore = (): {
  states: Record<Guid, SegmentRouteProposalState>;
  get: (segmentId: Guid, contextKey: string) => SegmentRouteProposalState;
  begin: (segmentId: Guid, contextKey: string, controller: AbortController) => number;
  complete: (segmentId: Guid, requestId: number, proposal: ExternalRouteProposal) => boolean;
  fail: (segmentId: Guid, requestId: number, message: string) => boolean;
  invalidateProfile: (segmentId: Guid, contextKey: string) => boolean;
  discard: (segmentId: Guid) => void;
  dispose: () => void;
} => {
  const states = reactive<Record<Guid, SegmentRouteProposalState>>({});
  let nextRequestId = 0;
  const fresh = (contextKey: string, requestId = 0): SegmentRouteProposalState => ({
    generating: false, error: null, proposal: null, controller: null, contextKey, requestId
  });
  const get = (segmentId: Guid, contextKey: string): SegmentRouteProposalState => {
    states[segmentId] ??= fresh(contextKey);
    return states[segmentId];
  };
  return {
    states,
    get,
    begin: (segmentId, contextKey, controller) => {
      const current = get(segmentId, contextKey);
      current.controller?.abort();
      const requestId = ++nextRequestId;
      Object.assign(current, fresh(contextKey, requestId), { generating: true, controller });
      return requestId;
    },
    complete: (segmentId, requestId, proposal) => {
      const current = states[segmentId];
      if (!current || current.requestId !== requestId || proposal.segmentId !== segmentId) return false;
      Object.assign(current, { generating: false, proposal, controller: null });
      return true;
    },
    fail: (segmentId, requestId, message) => {
      const current = states[segmentId];
      if (!current || current.requestId !== requestId) return false;
      Object.assign(current, { generating: false, error: message, controller: null });
      return true;
    },
    invalidateProfile: (segmentId, contextKey) => {
      const current = get(segmentId, contextKey);
      if (current.contextKey === contextKey) return false;
      current.controller?.abort();
      Object.assign(current, fresh(contextKey, ++nextRequestId));
      return true;
    },
    discard: segmentId => {
      const current = states[segmentId];
      if (!current) return;
      current.controller?.abort();
      Object.assign(current, fresh(current.contextKey, ++nextRequestId));
    },
    dispose: () => Object.values(states).forEach(current => {
      current.controller?.abort();
      Object.assign(current, fresh(current.contextKey, ++nextRequestId));
    })
  };
};
