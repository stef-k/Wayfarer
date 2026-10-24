import type { EditorMutationResult, EditorPlace, EditorPlaceSaveRequest, EditorRegion, EditorRegionSaveRequest, EditorTripState } from '../types';
import { validatePlaceAcknowledgement } from '../components/placeConditionalDraft';

/** Stored orders are evidence, independent of rendered list positions. */
export type RegionCreationOriginal = { id: string; displayOrder: number; isCanonical: boolean };
export type PlaceCreationOriginal = { id: string; displayOrder: number | null };
export type RegionCreationRequest = { expected: { regions: RegionCreationOriginal[] }; value: EditorRegionSaveRequest };
export type PlaceCreationRequest = { expected: { regionId: string; places: PlaceCreationOriginal[] }; value: EditorPlaceSaveRequest };

/** Copies the full authoritative starting view, never a filtered search result or a later refresh. */
export const regionCreationOriginals = (state: EditorTripState): RegionCreationOriginal[] => {
  const regions = Object.values(state.regionsById);
  if (regions.length !== state.regionOrder.length || new Set(state.regionOrder).size !== regions.length
    || regions.some(region => !state.regionOrder.includes(region.id) || region.tripId !== state.tripId || !Number.isInteger(region.displayOrder)))
    throw new Error('Original Region membership and stored orders are unavailable. Reload before starting creation.');
  return regions.map(region => ({ id: region.id, displayOrder: region.displayOrder, isCanonical: region.name === 'Unassigned Places' }));
};

/** Captures every selectable destination once; only the chosen destination becomes a server dependency. */
export const placeCreationOriginals = (state: EditorTripState): Record<string, PlaceCreationOriginal[]> => {
  const result: Record<string, PlaceCreationOriginal[]> = {};
  for (const region of Object.values(state.regionsById)) {
    const places = Object.values(state.placesById).filter(place => place.regionId === region.id);
    const ids = state.placeOrderByRegionId[region.id];
    if (!Array.isArray(ids) || ids.length !== places.length || new Set(ids).size !== places.length
      || places.some(place => !ids.includes(place.id) || place.tripId !== state.tripId || !place.originalValues
        || !Object.hasOwn(place.originalValues, 'displayOrder')
        || place.originalValues.displayOrder !== null && !Number.isInteger(place.originalValues.displayOrder)))
      throw new Error('Original Place membership and nullable stored orders are unavailable. Reload before starting creation.');
    result[region.id] = places.map(place => ({ id: place.id, displayOrder: place.originalValues!.displayOrder! }));
  }
  return result;
};

const same = (a: unknown, b: unknown): boolean => JSON.stringify(a) === JSON.stringify(b);
const guid = (id: string): boolean => /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id) && !/^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(id);
/** Uses stored nullable order and GUID tie-breaks, including nulls after the new numeric append. */
const orderedIds = (rows: { id: string; displayOrder: number | null }[]): string[] => [...rows].sort((a, b) =>
  a.displayOrder === null ? b.displayOrder === null ? a.id.localeCompare(b.id) : 1
    : b.displayOrder === null ? -1 : a.displayOrder - b.displayOrder || a.id.localeCompare(b.id)).map(row => row.id);
const empty = (value: unknown): boolean => Array.isArray(value) && value.length === 0;

/** Checks the operation contract and exact acknowledged creation footprint before any state adoption. */
export const validateRegionCreation = (result: EditorMutationResult<EditorRegion>, tripId: string, submitted: RegionCreationRequest): void => {
  const next = result?.data;
  const affected = result?.affected;
  const originals = submitted.expected.regions;
  const normal = originals.filter(region => !region.isCanonical);
  const expectedOrder = normal.length ? Math.max(...normal.map(region => region.displayOrder)) + 1 : 1;
  const ids = originals.map(region => region.id);
  const nullableText = (value: unknown): boolean => value === null || typeof value === 'string';
  if (result?.contract !== 'bd2-1' || result.success !== true || !next || !guid(next.id) || ids.includes(next.id)
    || next.tripId !== tripId || next.isShadow !== false || next.name !== submitted.value.name.trim()
    || typeof next.notesHtml !== 'string' || next.displayOrder !== expectedOrder
    || !next.originalValues || next.originalValues.name !== next.name || (next.originalValues.notesHtml ?? '') !== next.notesHtml
    || !nullableText(next.originalValues.notesHtml) || !same(next.center, submitted.value.center)
    || !same(next.originalValues.center, next.center) || !same(next.originalValues.coverImage, submitted.value.coverImage?.rawUrl?.trim() ? { rawUrl: submitted.value.coverImage.rawUrl.trim() } : null)
    || (next.coverImage?.rawUrl ?? null) !== (submitted.value.coverImage?.rawUrl?.trim() || null)
    || !next.capabilities || !['canEdit', 'canDelete', 'canAddChildren'].every(key => typeof next.capabilities[key as keyof typeof next.capabilities] === 'boolean')
    || !Array.isArray(affected?.regions) || affected.regions.length !== 1 || !same(affected.regions[0], next)
    || !same(affected.regionOrder, orderedIds([...originals, { id: next.id, displayOrder: expectedOrder }]))
    || !same(affected.placeOrdersByRegionId, { [next.id]: [] }) || !same(affected.areaOrdersByRegionId, { [next.id]: [] })
    || !['places', 'areas', 'segments', 'tags'].every(key => empty(affected[key as keyof typeof affected]))
    || [affected.metadata, affected.segmentOrder, affected.tagOrder, affected.visitProgress, affected.options].some(value => value !== null)
    || !Object.values(result.deletedIds ?? {}).every(empty) || Object.keys(result.deletedIds ?? {}).length !== 5
    || !empty(result.warnings)) throw new Error('The Region creation acknowledgement is incomplete. Keep the draft; the creation outcome is unknown.');
};

/** Place scalar validation is reused, with creation-specific identity and exact append checks added. */
export const validatePlaceCreation = (result: EditorMutationResult<EditorPlace>, tripId: string, submitted: PlaceCreationRequest): void => {
  validatePlaceAcknowledgement(result, tripId, null, submitted.expected.regionId, false);
  const originals = submitted.expected.places;
  const ids = originals.map(place => place.id);
  const orders = originals.map(place => place.displayOrder).filter(value => value !== null);
  const expectedOrder = (orders.length ? Math.max(...orders) : 0) + 1;
  if (result.contract !== 'bd2-1' || !guid(result.data.id) || ids.includes(result.data.id)
    || result.data.displayOrder !== expectedOrder || result.data.originalValues?.displayOrder !== expectedOrder
    || !same(result.affected.placeOrdersByRegionId[submitted.expected.regionId], orderedIds([...originals, { id: result.data.id, displayOrder: expectedOrder }]))
    || result.data.name !== submitted.value.name.trim() || !same(result.data.location, submitted.value.location)
    || result.data.iconName !== submitted.value.iconName || result.data.markerColor !== submitted.value.markerColor)
    throw new Error('The Place creation acknowledgement is incomplete. Keep the draft; the creation outcome is unknown.');
};
