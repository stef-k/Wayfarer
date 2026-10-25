import type { EditorMutationResult } from '../types';
import { completePlace } from './placeStructuralEditing';
import { completeSegment } from './segmentNotesDraft';
import { completeVisitProgress } from './placeConditionalDraft';

type Effects = { count: number; ids: string[]; hasMore: boolean };
type Association = { segmentId: string; placeId: string };
/** Full effect/order identities are public approval scope; private material remains inside the opaque token. */
export type PlaceDeletionPreview = {
  contractVersion: number; tripId: string; placeId: string; regionId: string; placeName: string;
  deletedSegments: Effects; updatedSegments: Effects;
  detachedWaypoints: { count: number; ids: Association[]; hasMore: boolean };
  unlinkedVisits: number; deletedCandidates: number; placeOrderIds: string[]; segmentOrderIds: string[] | null;
  confirmation: { token: string; expiresAt: string };
};
export type PlaceDeletionResult = { tripId: string; placeId: string; regionId: string;
  updatedSegmentIds: string[]; detachedWaypointAssociations: Association[] };
const same = (a: unknown, b: unknown): boolean => JSON.stringify(a) === JSON.stringify(b);
const ids = (value: unknown): value is string[] => Array.isArray(value) && value.every(id => typeof id === 'string') && new Set(value).size === value.length;
const setEqual = (a: string[], b: string[]): boolean => ids(a) && ids(b) && same([...a].sort(), [...b].sort());
const count = (value: number): boolean => Number.isInteger(value) && value >= 0;

/** A malformed, foreign or expired preview never reaches the consent dialog. */
export const validatePlaceDeletionPreview = (p: PlaceDeletionPreview, tripId: string, placeId: string, regionId: string): void => {
  if (!p || p.contractVersion !== 1 || p.tripId !== tripId || p.placeId !== placeId || p.regionId !== regionId || typeof p.placeName !== 'string'
    || ![p.deletedSegments, p.updatedSegments].every(e => e && ids(e.ids) && e.count === e.ids.length && e.hasMore === false)
    || p.deletedSegments.ids.some(id => p.updatedSegments.ids.includes(id)) || !count(p.unlinkedVisits) || !count(p.deletedCandidates)
    || !ids(p.placeOrderIds) || p.placeOrderIds.includes(placeId) || p.segmentOrderIds !== null && !ids(p.segmentOrderIds)
    || (p.deletedSegments.count > 0) !== (p.segmentOrderIds !== null)
    || !p.detachedWaypoints || !Array.isArray(p.detachedWaypoints.ids) || p.detachedWaypoints.hasMore !== false
    || p.detachedWaypoints.count !== p.detachedWaypoints.ids.length
    || !setEqual(p.detachedWaypoints.ids.map(w => w.segmentId), p.updatedSegments.ids)
    || p.detachedWaypoints.ids.some(w => w.placeId !== placeId)
    || !p.confirmation || typeof p.confirmation.token !== 'string' || !p.confirmation.token
    || !Number.isFinite(Date.parse(p.confirmation.expiresAt)) || Date.parse(p.confirmation.expiresAt) <= Date.now())
    throw new Error('Deletion preview is incomplete or expired. Keep the draft and review a fresh preview.');
};

/** Checks exact approved identities and every affected slice before any shared state adoption. */
export const validatePlaceDeletionAcknowledgement = (result: EditorMutationResult<PlaceDeletionResult>, p: PlaceDeletionPreview): void => {
  const a = result?.affected;
  const d = result?.data;
  const projectedSegments = p.segmentOrderIds ?? p.updatedSegments.ids;
  if (result?.success !== true || !d || d.tripId !== p.tripId || d.placeId !== p.placeId || d.regionId !== p.regionId
    || !setEqual(d.updatedSegmentIds, p.updatedSegments.ids) || !same(d.detachedWaypointAssociations, p.detachedWaypoints.ids)
    || !result.deletedIds || !same(result.deletedIds.places, [p.placeId]) || !setEqual(result.deletedIds.segments, p.deletedSegments.ids)
    || !['regions', 'areas', 'tags'].every(key => same(result.deletedIds[key as 'regions'], []))
    || !a || !Array.isArray(a.places) || !setEqual(a.places.map(x => x.id), p.placeOrderIds)
    || a.places.some(x => !completePlace(x) || x.tripId !== p.tripId || x.regionId !== p.regionId
      || x.displayOrder !== p.placeOrderIds.indexOf(x.id) + 1 || x.originalValues?.displayOrder !== x.displayOrder)
    || !same(a.placeOrdersByRegionId, { [p.regionId]: p.placeOrderIds })
    || !Array.isArray(a.segments) || !setEqual(a.segments.map(x => x.id), projectedSegments)
    || a.segments.some(x => !completeSegment(x) || x.tripId !== p.tripId || [x.fromPlaceId, x.toPlaceId, ...x.waypointPlaceIds].includes(p.placeId))
    || !same(a.segmentOrder, p.segmentOrderIds) || !completeVisitProgress(a.visitProgress!)
    || !a.metadata || !Number.isFinite(Date.parse(a.metadata.updatedAt))
    || !['regions', 'areas', 'tags'].every(key => same(a[key as 'regions'], [])) || !same(a.areaOrdersByRegionId, {})
    || [a.regionOrder, a.tagOrder, a.options].some(x => x !== null) || !same(result.warnings, []))
    throw new Error('The deletion acknowledgement is incomplete. Outcome unknown; keep the operation held without retry.');
};

/** Consequences describe route destruction, retained history and anonymous custom vertices explicitly. */
export const placeDeletionMessage = (p: PlaceDeletionPreview): string =>
  `Permanently delete “${p.placeName}” and its saved content. Delete ${p.deletedSegments.count} endpoint Segment(s), update ${p.updatedSegments.count} surviving waypoint route(s), and detach ${p.detachedWaypoints.count} waypoint association(s). Custom route vertices remain anonymous. Retain and unlink ${p.unlinkedVisits} visit history record(s); remove ${p.deletedCandidates} visit candidate(s). Remaining Place and affected Segment ordering will be reconciled. This cannot be undone.`;
