import type { EditorPlace, EditorPlaceOrderResult, EditorPlaceSaveRequest, EditorMutationResult, EditorTripState, EditorPlaceVisitSummary, EditorPlaceOriginalValues } from '../types';

/** Checks visit projections used by both the Place and optional complete-save progress slice. */
export const completeVisitSummary = (visit: EditorPlaceVisitSummary): boolean => Boolean(visit && typeof visit.placeId === 'string'
  && Number.isInteger(visit.visitCount) && visit.visitCount >= 0 && typeof visit.isVisited === 'boolean'
  && [visit.firstVisitAt, visit.lastVisitAt].every(date => date === null || typeof date === 'string'));

/** Requires the complete Place shape before any parent, baseline, preview or Saved adoption. */
export const completePlace = (value: EditorPlace): boolean => {
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

/** Exact public material base; private route/enrichment facts are reviewed in the prepared conditions. */
export interface PlaceMaterialOriginal {
  id: string; regionId: string; name: string; notes: string | null; address: string | null;
  location: EditorPlace['location']; iconName: string | null; markerColor: string | null; displayOrder: number | null;
  resolvedFeatureName: string | null; resolvedFeatureType: string | null;
}

/** Exact combined intent and retained originals submitted unchanged through preparation and apply. */
export interface PlaceStructureRequest {
  expected: Record<string, unknown>;
  changes: Partial<EditorPlaceSaveRequest>;
  placeIdsByRegion?: Record<string, string[]>;
  conditionToken?: string;
  reviewed?: boolean;
}

/** Coherent public review projection plus opaque private conditions; never a replacement baseline. */
export interface PlaceStructurePreview {
  contract: string; operation: string; tripId: string; targetId: string; requiresReview: boolean;
  conditionToken: string; place: PlaceMaterialOriginal | null;
  placesByRegion: Record<string, PlaceMaterialOriginal[]>; affectedSegmentIds: string[];
}

/** Never substitutes display defaults for nullable storage evidence. Missing originals hold dispatch. */
export const placeMaterialOriginal = (place: EditorPlace): PlaceMaterialOriginal => {
  const original = place.originalValues;
  if (!original || !Object.hasOwn(original, 'displayOrder') || !Object.hasOwn(original, 'address'))
    throw new Error('Original Place content is unavailable. Keep your draft and explicitly review a fresh starting view.');
  return JSON.parse(JSON.stringify({ id: place.id, regionId: original.regionId, name: original.name, notes: original.notesHtml,
    address: original.address!, location: place.location, iconName: original.iconName, markerColor: original.markerColor,
    displayOrder: original.displayOrder!, resolvedFeatureName: place.resolvedFeatureName ?? null, resolvedFeatureType: place.resolvedFeatureType ?? null }));
};

/** Captures every potential destination once, before membership/order intent is authored. */
export const placeOrderOriginals = (state: EditorTripState): Record<string, { id: string; displayOrder: number | null }[]> =>
  Object.fromEntries(Object.keys(state.regionsById).map(regionId => [regionId,
    Object.values(state.placesById).filter(p => p.regionId === regionId).map(p => {
      if (!p.originalValues || !Object.hasOwn(p.originalValues, 'displayOrder')) throw new Error('Original Place order is unavailable.');
      return { id: p.id, displayOrder: p.originalValues.displayOrder! };
    }).sort((a, b) => a.id.localeCompare(b.id))]));

/** Selects authored fields; move uses the entire moved-target base and exact source/destination sets. */
export const placeStructureRequest = (current: EditorPlaceSaveRequest, before: EditorPlaceSaveRequest,
  original: PlaceMaterialOriginal, orders: ReturnType<typeof placeOrderOriginals>): PlaceStructureRequest => {
  const changes: Partial<EditorPlaceSaveRequest> = {};
  for (const key of Object.keys(current) as (keyof EditorPlaceSaveRequest)[])
    if (JSON.stringify(current[key]) !== JSON.stringify(before[key])) Object.assign(changes, { [key]: current[key] });
  const moved = Object.hasOwn(changes, 'regionId');
  if (moved) {
    const source = orders[original.regionId];
    const destination = orders[current.regionId!];
    if (!source || !destination) throw new Error('Original move membership is unavailable. Keep your draft.');
    return { expected: { place: original, placesByRegion: { [original.regionId]: source, [current.regionId!]: destination } }, changes,
      placeIdsByRegion: { [original.regionId]: source.filter(p => p.id !== original.id).sort(storedOrder).map(p => p.id),
        [current.regionId!]: [...destination].sort(storedOrder).map(p => p.id).concat(original.id) } };
  }
  const expected: Record<string, unknown> = { regionId: original.regionId };
  for (const key of Object.keys(changes)) {
    if (key === 'reverseGeocode') continue;
    expected[key] = original[key === 'notesHtml' ? 'notes' : key as keyof PlaceMaterialOriginal];
  }
  if (changes.reverseGeocode) { expected.address = original.address; expected.location = original.location; }
  return { expected, changes };
};

/** Matches server ordering while retaining the exact sparse/null original values as evidence. */
const storedOrder = (a: { id: string; displayOrder: number | null }, b: { id: string; displayOrder: number | null }): number =>
  Number(a.displayOrder === null) - Number(b.displayOrder === null) || (a.displayOrder ?? 0) - (b.displayOrder ?? 0) || a.id.localeCompare(b.id);

/** Checks the full public material projection before it can support explicit dependency review. */
const validMaterial = (place: PlaceMaterialOriginal): boolean => Boolean(place && typeof place.id === 'string' && place.id
  && typeof place.regionId === 'string' && place.regionId && typeof place.name === 'string'
  && ['notes', 'address', 'iconName', 'markerColor', 'resolvedFeatureName', 'resolvedFeatureType'].every(key => Object.hasOwn(place, key)
    && (place[key as keyof PlaceMaterialOriginal] === null || typeof place[key as keyof PlaceMaterialOriginal] === 'string'))
  && (place.displayOrder === null || Number.isInteger(place.displayOrder))
  && (place.location === null || place.location && Number.isFinite(place.location.latitude) && Math.abs(place.location.latitude) <= 90
    && Number.isFinite(place.location.longitude) && Math.abs(place.location.longitude) <= 180));

/** A malformed prepare acknowledgement never becomes an apply token or an adopted baseline. */
export const validatePlaceStructurePreview = (value: PlaceStructurePreview, tripId: string, targetId: string, operation: string): void => {
  if (value?.contract !== 'bd2-1' || value.operation !== operation || value.tripId !== tripId || value.targetId !== targetId
    || value.requiresReview !== true || typeof value.conditionToken !== 'string' || !value.conditionToken
    || !Array.isArray(value.affectedSegmentIds) || value.affectedSegmentIds.some(id => typeof id !== 'string')
    || new Set(value.affectedSegmentIds).size !== value.affectedSegmentIds.length
    || !value.placesByRegion || typeof value.placesByRegion !== 'object' || Array.isArray(value.placesByRegion)
    || Object.values(value.placesByRegion).some(places => !Array.isArray(places) || places.some(p => !validMaterial(p)))
    || (operation === 'order' ? value.place !== null : !value.place || !validMaterial(value.place) || value.place.id !== targetId))
    throw new Error('The prepared Place conditions are incomplete. Keep your draft; no apply was sent.');
};

/** Complete vector acknowledgements must cover exactly the submitted scope and preserve unrelated slices. */
export const validatePlaceOrderAcknowledgement = (result: EditorMutationResult<EditorPlaceOrderResult>, tripId: string,
  regionId: string, ids: string[], originals: PlaceMaterialOriginal[]): void => {
  const affected = result?.affected;
  const orders = affected?.placeOrdersByRegionId;
  const same = (a: unknown, b: unknown): boolean => JSON.stringify(a) === JSON.stringify(b);
  if (result?.contract !== 'bd2-1' || result.success !== true || result.data?.regionId !== regionId || !same(result.data.placeOrder, ids)
    || !orders || Object.keys(orders).length !== 1 || !same(orders[regionId], ids)
    || !Array.isArray(affected.places) || affected.places.length !== ids.length || new Set(affected.places.map(p => p.id)).size !== ids.length
    || affected.places.some(p => !completePlace(p) || p.tripId !== tripId || p.regionId !== regionId || !ids.includes(p.id)
      || p.displayOrder !== ids.indexOf(p.id) + 1 || !same(placeMaterialOriginal(p), { ...originals.find(o => o.id === p.id), displayOrder: ids.indexOf(p.id) + 1 }))
    || ['regions', 'areas', 'segments', 'tags'].some(key => !Array.isArray(affected[key as keyof typeof affected]) || (affected[key as 'regions']).length !== 0)
    || Object.keys(affected.areaOrdersByRegionId ?? { invalid: [] }).length !== 0
    || [affected.metadata, affected.regionOrder, affected.segmentOrder, affected.visitProgress, affected.tagOrder, affected.options].some(v => v !== null)
    || !result.deletedIds || Object.values(result.deletedIds).some(ids => !Array.isArray(ids) || ids.length !== 0)
    || !Array.isArray(result.warnings)) throw new Error('The Place order acknowledgement is incomplete. Keep the unknown outcome held.');
};
