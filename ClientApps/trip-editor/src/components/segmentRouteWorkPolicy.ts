import type { EditorSegmentDraft } from '../types';

type SegmentRouteDraft = Pick<EditorSegmentDraft, 'route'> & Partial<Pick<EditorSegmentDraft, 'waypointPlaceIds' | 'waypointRows'>>;

/** Route-work eligibility is validated by the anchor-aware constructor without hiding waypoint drafts. */
export const canMutateSegmentRoute = (_draft: SegmentRouteDraft): boolean => true;

/** Invokes route work; malformed aggregate state is rejected by the anchor-aware constructor. */
export const invokeSegmentRouteAction = (draft: SegmentRouteDraft, action: () => void): boolean => {
  if (!canMutateSegmentRoute(draft)) return false;
  action();
  return true;
};
