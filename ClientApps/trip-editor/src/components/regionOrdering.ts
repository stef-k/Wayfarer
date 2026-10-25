import type { EditorRegion, EditorTripState, EditorMutationResult, EditorRegionOrderRequest, EditorRegionOrderResult } from '../types';

/** Exact material content of reordered Regions; canonical content/children are outside the order footprint. */
export interface RegionOrderMaterial {
  id: string; displayOrder: number; name: string; notes: string | null;
  coverImageUrl: string | null; center: EditorRegion['center'];
}
/** Complete original normal membership plus explicit canonical absence or identity/stored order. */
export interface RegionOrderOriginals {
  regions: RegionOrderMaterial[];
  canonical: { id: string; displayOrder: number } | null;
}
/** Preparation checks client originals and publishes bound conditions, never a replacement base. */
export interface RegionOrderPreview extends RegionOrderOriginals {
  contract: string; operation: string; tripId: string; requiresReview: boolean; conditionToken: string;
}

const same = (a: unknown, b: unknown): boolean => JSON.stringify(a) === JSON.stringify(b);
const empty = (value: unknown): boolean => Array.isArray(value) && value.length === 0;
const sorted = (rows: RegionOrderMaterial[]): RegionOrderMaterial[] => [...rows].sort((a, b) => a.id.localeCompare(b.id));

/** Copies original storage evidence instead of cover/display Notes defaults. */
const material = (region: EditorRegion): RegionOrderMaterial => {
  const original = region?.originalValues;
  if (!original || !['name', 'notesHtml', 'coverImage', 'center'].every(key => Object.hasOwn(original, key)) || !Number.isInteger(region.displayOrder))
    throw new Error('Original Region material is unavailable. Keep the order and explicitly review a fresh starting view.');
  return { id: region.id, displayOrder: region.displayOrder, name: original.name, notes: original.notesHtml,
    coverImageUrl: original.coverImage?.rawUrl ?? null, center: original.center };
};

/** Captures the full authoritative state synchronously before a prompt, preparation or later prop refresh. */
export const regionOrderOriginals = (state: EditorTripState): RegionOrderOriginals | null => {
  const regions = Object.values(state.regionsById);
  const canonical = regions.filter(r => r.name === 'Unassigned Places');
  if (canonical.length > 1 || regions.some(r => r.tripId !== state.tripId) || regions.length !== state.regionOrder.length
    || new Set(state.regionOrder).size !== regions.length || regions.some(r => !state.regionOrder.includes(r.id))) return null;
  try {
    // Preserve the visible starting sequence as well as exact content; later props cannot supply a missing original.
    return JSON.parse(JSON.stringify({ regions: state.regionOrder.map(id => state.regionsById[id]).filter(r => r.name !== 'Unassigned Places').map(material),
      canonical: canonical.length ? { id: canonical[0].id, displayOrder: canonical[0].displayOrder } : null }));
  } catch { return null; }
};

/** Scope and echoed originals must match before the opaque prepared token can be used. */
export const validateRegionOrderPreview = (value: RegionOrderPreview, tripId: string, expected: RegionOrderOriginals): void => {
  if (value?.contract !== 'bd2-1' || value.operation !== 'region-order' || value.tripId !== tripId || value.requiresReview !== true
    || typeof value.conditionToken !== 'string' || !value.conditionToken || !Array.isArray(value.regions)
    || !same(sorted(value.regions), sorted(expected.regions)) || !same(value.canonical, expected.canonical))
    throw new Error('The Region order preparation is incomplete. Keep your order; no apply was sent.');
};

/** Validates the exact scope, material preservation, vector and empty unrelated effects before state/Saved adoption. */
export const validateRegionOrderAcknowledgement = (result: EditorMutationResult<EditorRegionOrderResult>, tripId: string, request: EditorRegionOrderRequest): void => {
  const affected = result?.affected;
  const { expected, regionIds } = request;
  const ordered = regionIds.map((id, index) => ({ id, displayOrder: index + 1 }));
  if (expected.canonical) ordered.push(expected.canonical);
  const order = ordered.sort((a, b) => a.displayOrder - b.displayOrder || a.id.localeCompare(b.id)).map(r => r.id);
  const capabilities = ['canEdit', 'canRename', 'canDelete', 'canReorder', 'canMove', 'canAddChildren', 'canTargetForSearchAdd'] as const;
  if (result?.contract !== 'bd2-1' || result.success !== true || result.data?.tripId !== tripId || !same(result.data.regionOrder, order)
    || !same(affected?.regionOrder, order) || !Array.isArray(affected.regions) || affected.regions.length !== regionIds.length
    || new Set(affected.regions.map(r => r.id)).size !== regionIds.length
    || affected.regions.some(r => r.tripId !== tripId || !regionIds.includes(r.id) || r.isShadow !== false || typeof r.notesHtml !== 'string'
      || (r.originalValues?.notesHtml ?? '') !== r.notesHtml || r.originalValues?.name !== r.name || !same(r.originalValues.center, r.center)
      || !(r.coverImage === null || r.coverImage && typeof r.coverImage.rawUrl === 'string' && typeof r.coverImage.proxiedUrl === 'string')
      || (r.coverImage?.rawUrl ?? null) !== (r.originalValues?.coverImage?.rawUrl ?? null)
      || !r.capabilities || !capabilities.every(key => typeof r.capabilities[key] === 'boolean')
      || !same(material(r), { ...expected.regions.find(o => o.id === r.id), displayOrder: regionIds.indexOf(r.id) + 1 }))
    || !['places', 'areas', 'segments', 'tags'].every(key => empty(affected[key as keyof typeof affected]))
    || Object.keys(affected.placeOrdersByRegionId ?? { invalid: [] }).length !== 0
    || Object.keys(affected.areaOrdersByRegionId ?? { invalid: [] }).length !== 0
    || [affected.metadata, affected.segmentOrder, affected.tagOrder, affected.visitProgress, affected.options].some(v => v !== null)
    || !['regions', 'places', 'areas', 'segments', 'tags'].every(key => empty(result.deletedIds?.[key as keyof typeof result.deletedIds]))
    || Object.keys(result.deletedIds ?? {}).length !== 5 || !empty(result.warnings))
    throw new Error('The Region order acknowledgement is incomplete. The outcome is unknown; keep the order held without replay.');
};
