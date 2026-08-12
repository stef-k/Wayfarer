import type { EditorSegmentDraft } from '../types';

type SegmentRouteDraft = Pick<EditorSegmentDraft, 'route'> & Partial<Pick<EditorSegmentDraft, 'waypointPlaceIds' | 'waypointRows'>>;

/** Returns whether the current hidden aggregate can safely enter route work before #409. */
export const canMutateSegmentRoute = (draft: SegmentRouteDraft): boolean => (draft.waypointRows?.length ?? draft.waypointPlaceIds?.length ?? 0) === 0;

/** Invokes route work only when the authoritative draft has no intermediate-place anchors. */
export const invokeSegmentRouteAction = (draft: SegmentRouteDraft, action: () => void): boolean => {
  if (!canMutateSegmentRoute(draft)) return false;
  action();
  return true;
};
