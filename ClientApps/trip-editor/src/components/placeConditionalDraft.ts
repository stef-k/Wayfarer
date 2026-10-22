import { reactive, ref } from 'vue';
import type { EditorMutationResult, EditorPlace, EditorPlaceConditionalRequest, EditorPlaceDraft, EditorPlaceOriginalValues, EditorPlaceSaveRequest, EditorPlaceVisitSummary, EditorVisitProgress } from '../types';
import { buildPlaceRequest, emptyPlaceDraft, toPlaceDraft } from './regionPlaceDrafts';
import { completeSegment } from './segmentNotesDraft';

const scalars = ['name', 'notesHtml', 'iconName', 'markerColor'] as const;
const fields = [...scalars, 'regionId', 'address', 'location', 'reverseGeocode'] as const;
type Field = typeof fields[number];
const same = (a: unknown, b: unknown): boolean => JSON.stringify(a) === JSON.stringify(b);

/** Copies one logical field, treating the coordinate pair as a single authored choice. */
const assign = (target: EditorPlaceDraft, source: EditorPlaceDraft, field: Field): void => {
  if (field === 'location') { target.latitude = source.latitude; target.longitude = source.longitude; }
  else Object.assign(target, { [field]: source[field] });
};

/** Owns Place drafts, selected originals and session identity independently of mutable parent props. */
export const createPlaceDraftState = () => {
  const draft = reactive(emptyPlaceDraft());
  const baseline = reactive(emptyPlaceDraft());
  const originals = reactive<Partial<EditorPlaceOriginalValues>>({});
  const pending = ref(false);
  const uncertain = ref(false);
  const session = ref(0);

  /** Adopts a clean field without inventing comparison evidence from displayed defaults. */
  const adopt = (field: Field, incoming: EditorPlaceDraft, next: EditorPlace): void => {
    assign(draft, incoming, field);
    assign(baseline, incoming, field);
    if (scalars.includes(field as typeof scalars[number])) {
      const scalar = field as typeof scalars[number];
      delete originals[scalar];
      if (next.originalValues && Object.hasOwn(next.originalValues, scalar))
        Object.assign(originals, { [scalar]: next.originalValues[scalar] });
    }
  };

  /** Explicit reset/discard retires late replies; it makes no assertion about an earlier server commit. */
  const reset = (next: EditorPlace | null): void => {
    session.value++;
    pending.value = uncertain.value = false;
    Object.assign(draft, toPlaceDraft(next, null));
    Object.assign(baseline, toPlaceDraft(next, null));
    for (const key of Object.keys(originals)) delete originals[key as keyof EditorPlaceOriginalValues];
    if (next?.originalValues) Object.assign(originals, next.originalValues);
  };

  /** Clean fields follow refreshed props; dirty values and the original parent remain pinned. */
  const refresh = (next: EditorPlace | null): void => {
    if (!next || next.id !== draft.id || next.regionId !== baseline.regionId || pending.value || uncertain.value) return;
    const current = buildPlaceRequest(draft);
    const before = buildPlaceRequest(baseline);
    const incoming = toPlaceDraft(next, null);
    for (const field of fields) if (field !== 'regionId' && same(current[field], before[field])) adopt(field, incoming, next);
  };

  /** Structural intent retains one complete lifecycle PUT, with any simultaneously authored scalars. */
  const structural = (): boolean => {
    const current = buildPlaceRequest(draft);
    const before = buildPlaceRequest(baseline);
    return fields.filter(field => !scalars.includes(field as typeof scalars[number])).some(field => !same(current[field], before[field]));
  };

  /** Selects dirty scalars with exact nullable originals and the session's original parent identity. */
  const request = (): EditorPlaceConditionalRequest => {
    if (!originals.regionId) throw new Error('Original Place parent is unavailable. Keep your draft and reload before saving.');
    const result: EditorPlaceConditionalRequest = { expected: { regionId: originals.regionId }, changes: {} };
    const current = buildPlaceRequest(draft);
    const before = buildPlaceRequest(baseline);
    for (const field of scalars) {
      if (same(current[field], before[field])) continue;
      if (!Object.hasOwn(originals, field)) throw new Error('Original Place values are unavailable. Keep your draft and reload before saving.');
      Object.assign(result.expected, { [field]: originals[field] });
      Object.assign(result.changes, { [field]: current[field] });
    }
    return result;
  };

  /** Advances accepted bases before Vue applies parent props, retaining newer edits including old-baseline reversals. */
  const acknowledge = (next: EditorPlace, submitted: Partial<EditorPlaceSaveRequest>): void => {
    const current = buildPlaceRequest(draft);
    const before = buildPlaceRequest(baseline);
    const retained = { ...draft };
    const incoming = toPlaceDraft(next, null);
    for (const field of fields) {
      const sent = Object.hasOwn(submitted, field);
      if (!sent && !same(current[field], before[field])) continue;
      adopt(field, incoming, next);
      if (sent && !same(current[field], submitted[field])) assign(draft, retained, field);
    }
    draft.id = baseline.id = next.id;
    originals.regionId = next.originalValues!.regionId;
  };

  return { draft, baseline, pending, uncertain, reset, refresh, structural, request, acknowledge, session: () => session.value };
};

/** Checks visit projections used by both the Place and optional complete-save progress slice. */
const completeVisitSummary = (visit: EditorPlaceVisitSummary): boolean => Boolean(visit && typeof visit.placeId === 'string'
  && Number.isInteger(visit.visitCount) && visit.visitCount >= 0 && typeof visit.isVisited === 'boolean'
  && [visit.firstVisitAt, visit.lastVisitAt].every(date => date === null || typeof date === 'string'));

/** Complete Place saves may refresh visit progress; scalar acknowledgements cannot publish it. */
const completeVisitProgress = (progress: EditorVisitProgress): boolean => Boolean(progress
  && Number.isInteger(progress.totalPlaces) && progress.totalPlaces >= 0
  && Number.isInteger(progress.visitedPlaces) && progress.visitedPlaces >= 0 && progress.visitedPlaces <= progress.totalPlaces
  && Number.isFinite(progress.percentVisited) && progress.percentVisited >= 0 && progress.percentVisited <= 100
  && progress.placeSummariesByPlaceId && typeof progress.placeSummariesByPlaceId === 'object' && !Array.isArray(progress.placeSummariesByPlaceId)
  && Object.entries(progress.placeSummariesByPlaceId).every(([id, visit]) => completeVisitSummary(visit) && visit.placeId === id)
  && Array.isArray(progress.historyRows) && progress.historyRows.every(row => row && typeof row.visitId === 'string'
    && typeof row.placeId === 'string' && typeof row.regionId === 'string' && typeof row.startedAt === 'string'
    && (row.endedAt === null || typeof row.endedAt === 'string')
    && (row.durationMinutes === null || Number.isFinite(row.durationMinutes))));

/** Requires the complete Place shape before any parent, baseline, preview or Saved adoption. */
const completePlace = (value: EditorPlace): boolean => {
  const original = value?.originalValues;
  const coordinate = value?.location;
  const visit = value?.visitSummary;
  return Boolean(value && typeof value.id === 'string' && typeof value.tripId === 'string' && typeof value.regionId === 'string'
    && ['name', 'notesHtml', 'address', 'iconName', 'markerColor'].every(key => typeof value[key as keyof EditorPlace] === 'string')
    && ['resolvedFeatureName', 'resolvedFeatureType'].every(key => !Object.hasOwn(value, key)
      || value[key as keyof EditorPlace] === null || typeof value[key as keyof EditorPlace] === 'string')
    && Number.isInteger(value.displayOrder) && value.capabilities && typeof value.capabilities.canEdit === 'boolean'
    && typeof value.capabilities.canDelete === 'boolean'
    && (coordinate === null || coordinate && Number.isFinite(coordinate.latitude) && Math.abs(coordinate.latitude) <= 90
      && Number.isFinite(coordinate.longitude) && Math.abs(coordinate.longitude) <= 180)
    && completeVisitSummary(visit) && visit.placeId === value.id
    && original && original.regionId === value.regionId && original.name === value.name
    && ['notesHtml', 'iconName', 'markerColor'].every(key => Object.hasOwn(original, key)
      && (original[key as keyof EditorPlaceOriginalValues] === null || typeof original[key as keyof EditorPlaceOriginalValues] === 'string'))
    && (original.notesHtml ?? '') === value.notesHtml
    && (original.iconName?.trim() ? original.iconName : 'marker') === value.iconName
    && (original.markerColor?.trim() ? original.markerColor : 'bg-blue') === value.markerColor);
};

/** Place saves never publish Region/Area/Tag changes; only complete saves may publish lifecycle effects. */
export const validatePlaceAcknowledgement = (result: EditorMutationResult<EditorPlace>, tripId: string,
  placeId: string | null, regionId: string, scalar: boolean): void => {
  const next = result?.data;
  const affected = result?.affected;
  const unrelated = ['regions', 'areas', 'tags'] as const;
  const validOrder = (ids: string[]): boolean => Array.isArray(ids) && ids.every(id => typeof id === 'string') && new Set(ids).size === ids.length;
  const validOrders = (orders: Record<string, string[]>): boolean => Boolean(orders && typeof orders === 'object' && !Array.isArray(orders)
    && Object.values(orders).every(validOrder));
  if (result?.success !== true || !completePlace(next) || !next.id || next.tripId !== tripId || next.regionId !== regionId
    || placeId !== null && next.id !== placeId
    || !Array.isArray(affected?.places) || affected.places.length !== 1 || !same(affected.places[0], next)
    || !validOrders(affected.placeOrdersByRegionId) || !validOrders(affected.areaOrdersByRegionId)
    || placeId === null && !affected.placeOrdersByRegionId[next.regionId]?.includes(next.id)
    || affected.placeOrdersByRegionId[next.regionId] && !affected.placeOrdersByRegionId[next.regionId].includes(next.id)
    || unrelated.some(key => !Array.isArray(affected[key]) || affected[key].length !== 0)
    || Object.keys(affected.areaOrdersByRegionId).length !== 0
    || [affected.metadata, affected.regionOrder, affected.tagOrder, affected.options].some(value => value !== null)
    || !Array.isArray(affected.segments) || affected.segments.some(segment => !completeSegment(segment) || segment.tripId !== tripId
      || typeof segment.hasCustomRoute !== 'boolean' || typeof segment.capabilities.canDelete !== 'boolean'
      || ![segment.fromPlaceId, segment.toPlaceId, ...segment.waypointPlaceIds].includes(next.id))
    || new Set(affected.segments.map(segment => segment.id)).size !== affected.segments.length
    || affected.segmentOrder !== null && (!validOrder(affected.segmentOrder) || affected.segments.some(segment => !affected.segmentOrder!.includes(segment.id)))
    || affected.visitProgress !== null && !completeVisitProgress(affected.visitProgress)
    || scalar && (Object.keys(affected.placeOrdersByRegionId).length !== 0 || affected.segments.length !== 0
      || affected.segmentOrder !== null || affected.visitProgress !== null)
    || placeId === null && (affected.segments.length !== 0 || affected.segmentOrder !== null)
    || [...unrelated, 'places' as const, 'segments' as const].some(key => !Array.isArray(result.deletedIds?.[key]) || result.deletedIds[key].length !== 0)
    || !Array.isArray(result.warnings) || result.warnings.some(warning => typeof warning?.code !== 'string' || typeof warning.message !== 'string'
      || warning.entityType !== null && typeof warning.entityType !== 'string' || warning.entityId !== null && typeof warning.entityId !== 'string'))
    throw new Error('The Place save acknowledgement is incomplete. Keep your draft and review the unknown outcome.');
};
