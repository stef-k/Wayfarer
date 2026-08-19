import { reactive } from 'vue';
import type { ExternalRouteProposal, Guid } from '../types';

export interface SegmentRouteProposalState {
  accepting: boolean;
  acceptanceController: AbortController | null;
  acceptanceContext: string | null;
  acceptanceProposalId: string | null;
  acceptanceRequestId: number;
  generating: boolean;
  error: string | null;
  proposal: ExternalRouteProposal | null;
  controller: AbortController | null;
  contextKey: string;
  requestId: number;
}

export interface RouteAcceptanceContext {
  segmentId: Guid;
  transportProfileId: Guid | null;
  anchorFingerprint: string;
  routeFingerprint: string;
  draftRevision: number;
}

/** Owns independent request/proposal state keyed strictly by Segment identity. */
export const createSegmentRouteProposalStore = (): {
  states: Record<Guid, SegmentRouteProposalState>;
  get: (segmentId: Guid, contextKey: string) => SegmentRouteProposalState;
  begin: (segmentId: Guid, contextKey: string, controller: AbortController) => number;
  complete: (segmentId: Guid, requestId: number, proposal: ExternalRouteProposal) => boolean;
  fail: (segmentId: Guid, requestId: number, message: string) => boolean;
  beginAcceptance: (segmentId: Guid, proposalId: string, context: RouteAcceptanceContext, controller: AbortController) => number | null;
  completeAcceptance: (segmentId: Guid, requestId: number, proposalId: string, context: RouteAcceptanceContext) => boolean;
  invalidateAcceptance: (segmentId: Guid) => void;
  invalidate: (segmentId: Guid, reason: string) => void;
  invalidateProfile: (segmentId: Guid, contextKey: string) => boolean;
  discard: (segmentId: Guid) => void;
  dispose: () => void;
} => {
  const states = reactive<Record<Guid, SegmentRouteProposalState>>({});
  let nextRequestId = 0;
  const fresh = (contextKey: string, requestId = 0): SegmentRouteProposalState => ({
    accepting: false, acceptanceController: null, acceptanceContext: null, acceptanceProposalId: null,
    acceptanceRequestId: 0, generating: false, error: null, proposal: null, controller: null, contextKey, requestId
  });
  const serializeContext = (context: RouteAcceptanceContext): string => JSON.stringify(context);
  const invalidateAcceptance = (segmentId: Guid): void => {
    const current = states[segmentId];
    if (!current) return;
    current.acceptanceController?.abort();
    Object.assign(current, { accepting: false, acceptanceController: null, acceptanceContext: null,
      acceptanceProposalId: null, acceptanceRequestId: ++nextRequestId });
  };
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
    beginAcceptance: (segmentId, proposalId, context, controller) => {
      const current = states[segmentId];
      if (!current?.proposal || current.accepting || current.proposal.proposalId !== proposalId) return null;
      const requestId = ++nextRequestId;
      Object.assign(current, { accepting: true, acceptanceController: controller,
        acceptanceContext: serializeContext(context), acceptanceProposalId: proposalId, acceptanceRequestId: requestId });
      return requestId;
    },
    completeAcceptance: (segmentId, requestId, proposalId, context) => {
      const current = states[segmentId];
      if (!current || !current.accepting || current.acceptanceRequestId !== requestId ||
        current.acceptanceProposalId !== proposalId || current.acceptanceContext !== serializeContext(context)) return false;
      Object.assign(current, { accepting: false, acceptanceController: null, acceptanceContext: null,
        acceptanceProposalId: null });
      return true;
    },
    invalidateAcceptance,
    invalidate: (segmentId, _reason) => {
      const current = states[segmentId];
      if (!current) return;
      current.controller?.abort();
      current.acceptanceController?.abort();
      Object.assign(current, fresh(current.contextKey, ++nextRequestId));
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
      current.acceptanceController?.abort();
      Object.assign(current, fresh(current.contextKey, ++nextRequestId));
    },
    dispose: () => Object.values(states).forEach(current => {
      current.controller?.abort();
      current.acceptanceController?.abort();
      Object.assign(current, fresh(current.contextKey, ++nextRequestId));
    })
  };
};
