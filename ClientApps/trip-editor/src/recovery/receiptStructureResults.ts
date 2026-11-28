import { digestPattern, keysAre, validDigests } from './receiptV2';
import { isId, type StructureRoute } from './structureRoutes';

type Wire = Record<string, unknown>;
export const object = (value: unknown): value is Wire => !!value && typeof value === 'object' && !Array.isArray(value);
export const digest = (value: unknown): boolean => typeof value === 'string' && digestPattern.test(value);
const nullableDigest = (value: unknown): boolean => value === null || digest(value);
const nullableId = (value: unknown): boolean => value === null || isId(value);
const integer = (value: unknown): value is number => typeof value === 'number' && Number.isSafeInteger(value);
const nullableInteger = (value: unknown): boolean => value === null || integer(value);
const count = (value: unknown): boolean => integer(value) && value >= 0;
export const utc = (value: unknown): value is string => typeof value === 'string' &&
  /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/u.test(value) && Number.isFinite(Date.parse(value));

/** Validates a complete array with distinct, well-formed member identities. */
function distinct<T>(value: unknown, valid: (row: unknown) => row is T, key: (row: T) => string): value is T[] {
  return Array.isArray(value) && value.every(valid) && new Set(value.map(key)).size === value.length;
}

const idList = (value: unknown): value is string[] => distinct(value, isId, id => id);
const item = (value: unknown): value is Wire => object(value) && keysAre(value, ['id', 'displayOrder']) &&
  isId(value.id) && nullableInteger(value.displayOrder);
const regionItem = (value: unknown): value is Wire => object(value) && keysAre(value, ['id', 'displayOrder', 'isCanonical']) &&
  isId(value.id) && integer(value.displayOrder) && typeof value.isCanonical === 'boolean';
const regionOrder = (value: unknown): value is Wire[] => distinct(value, regionItem, row => row.id as string) &&
  value.filter(row => row.isCanonical).length <= 1;
const vector = (value: unknown): value is Wire => object(value) && keysAre(value, ['regionId', 'items']) &&
  isId(value.regionId) && distinct(value.items, item, row => row.id as string);
const vectors = (value: unknown): value is Wire[] => distinct(value, vector, row => row.regionId as string);
const vectorHas = (value: Wire, id: unknown, order: unknown): boolean =>
  (value.items as Wire[]).some(row => row.id === id && row.displayOrder === order);

const tupleFields = ['resolvedFeatureName', 'resolvedFeatureType', 'addressEnrichmentProvider', 'addressEnrichmentStorageMode', 'addressEnrichedAt'];
const provenanceFields = ['routeInstructionsJson', 'routeProvider', 'routeProviderConfigurationId', 'routeProviderConfigurationVersion',
  'routeTransportProfileId', 'routeMappingMode', 'routeGeneratedAt', 'routeAttribution', 'routeStorageMode'];

function address(value: unknown): boolean {
  if (!object(value) || !keysAre(value, ['geocode', 'tupleState', 'tupleDigests', 'warningCodes']) ||
      !validDigests(value.tupleDigests, tupleFields) || !Array.isArray(value.warningCodes)) return false;
  if (value.geocode === 'resolved') return value.tupleState === 'replaced' && value.warningCodes.length === 0;
  return ['fallback', 'notRequested'].includes(value.geocode as string) && ['none', 'retained', 'cleared'].includes(value.tupleState as string) &&
    (value.geocode === 'fallback' ? value.warningCodes.length === 1 && value.warningCodes[0] === 'reverse-geocode-unavailable'
      : value.warningCodes.length === 0);
}

const waypoint = (value: unknown): value is Wire => object(value) && keysAre(value, ['placeId', 'position', 'routeVertexIndex']) &&
  isId(value.placeId) && count(value.position) && nullableInteger(value.routeVertexIndex);
const association = (value: unknown): value is Wire => object(value) && keysAre(value, ['segmentId', 'placeId']) &&
  isId(value.segmentId) && isId(value.placeId);
const visitEffects = (value: unknown): boolean => object(value) && keysAre(value, ['unlinkedEvents', 'deletedCandidates']) &&
  count(value.unlinkedEvents) && count(value.deletedCandidates);

/** Validates a saved Segment without accepting raw route, instructions or provider metadata. */
function segment(value: unknown): value is Wire {
  return object(value) && keysAre(value, ['id', 'displayOrder', 'fromPlaceId', 'toPlaceId', 'waypoints', 'route',
    'estimatedDistanceKm', 'estimatedDurationTicks', 'estimatedDurationSource', 'transportProfileId', 'provenanceDigests']) &&
    isId(value.id) && integer(value.displayOrder) && nullableId(value.fromPlaceId) && nullableId(value.toPlaceId) &&
    distinct(value.waypoints, waypoint, row => row.placeId as string) &&
    value.waypoints.every((row, index) => index === 0 || (row.position as number) > ((value.waypoints as Wire[])[index - 1].position as number)) &&
    object(value.route) && keysAre(value.route, ['storedDigest', 'effectiveDigest']) &&
    nullableDigest(value.route.storedDigest) && nullableDigest(value.route.effectiveDigest) &&
    (value.route.storedDigest === null || value.route.storedDigest === value.route.effectiveDigest) &&
    (value.estimatedDistanceKm === null || typeof value.estimatedDistanceKm === 'number' && Number.isFinite(value.estimatedDistanceKm)) &&
    (value.estimatedDurationTicks === null || typeof value.estimatedDurationTicks === 'string' && /^-?(?:0|[1-9]\d*)$/u.test(value.estimatedDurationTicks)) &&
    ['Automatic', 'Manual'].includes(value.estimatedDurationSource as string) &&
    nullableId(value.transportProfileId) && validDigests(value.provenanceDigests, provenanceFields);
}

function segmentEffects(value: unknown): value is Wire {
  if (!object(value) || !keysAre(value, ['deletedSegmentIds', 'removedWaypoints', 'updatedSegments', 'segmentOrder']) ||
      !idList(value.deletedSegmentIds) || !distinct(value.removedWaypoints, association, row => `${row.segmentId}/${row.placeId}`) ||
      !distinct(value.updatedSegments, segment, row => row.id as string)) return false;
  const deleted = new Set(value.deletedSegmentIds);
  const updated = new Map(value.updatedSegments.map(row => [row.id, row]));
  return value.updatedSegments.every(row => !deleted.has(row.id as string)) &&
    value.removedWaypoints.every(row => deleted.has(row.segmentId as string) || updated.has(row.segmentId) &&
      !(updated.get(row.segmentId)!.waypoints as Wire[]).some(waypoint => waypoint.placeId === row.placeId)) &&
    (deleted.size === 0 ? value.segmentOrder === null
      : distinct(value.segmentOrder, item, row => row.id as string) &&
        value.segmentOrder.every(row => integer(row.displayOrder) && !deleted.has(row.id as string)) &&
        value.updatedSegments.every(row => vectorHas({ items: value.segmentOrder }, row.id, row.displayOrder)));
}

function planningLoss(value: unknown, deleted: Partial<Record<'place' | 'area' | 'segment', string[]>>): boolean {
  const valid = (row: unknown): row is Wire => object(row) && keysAre(row, ['type', 'id', 'planningDigest']) &&
    isId(row.id) && digest(row.planningDigest) && ['place', 'area', 'segment'].includes(row.type as string) &&
    deleted[row.type as keyof typeof deleted]?.includes(row.id) === true;
  return distinct(value, valid, row => `${row.type}/${row.id}`) &&
    value.every((row, i) => !i || `${value[i - 1].type}/${value[i - 1].id}` < `${row.type}/${row.id}`);
}

function movements(value: unknown, source: string, destination: unknown, orders: unknown): value is Wire[] {
  if (!vectors(orders)) return false;
  const valid = (row: unknown): row is Wire => object(row) && keysAre(row, ['id', 'fromRegionId', 'toRegionId', 'displayOrder']) &&
    isId(row.id) && row.fromRegionId === source && isId(row.toRegionId) && row.toRegionId === destination && integer(row.displayOrder);
  return distinct(value, valid, row => row.id as string) &&
    (value.length === 0 ? orders.length === 0 : orders.length === 1 && orders[0].regionId === destination &&
      value.every(row => vectorHas(orders[0], row.id, row.displayOrder)));
}

function regionDeleted(value: Wire, route: StructureRoute, request: Wire): boolean {
  const { target } = route;
  if (!keysAre(value, ['id', 'places', 'areas', 'destinationMode', 'destinationRegionId', 'movedPlaces', 'movedAreas',
    'deletedPlaceIds', 'deletedAreaIds', 'planningDigest', 'deletedDependentPlanning', 'regionOrder',
    'placeOrders', 'areaOrders', 'segmentEffects', 'visitEffects']) ||
      value.id !== target.id || !['keep', 'delete'].includes(value.places as string) || !['keep', 'delete'].includes(value.areas as string) ||
      value.places !== request.places || value.areas !== request.areas || !nullableDigest(value.planningDigest) ||
      !idList(value.deletedPlaceIds) || !idList(value.deletedAreaIds) || !regionOrder(value.regionOrder) ||
      value.regionOrder.some(row => row.id === target.id) || !segmentEffects(value.segmentEffects) || !visitEffects(value.visitEffects) ||
      !movements(value.movedPlaces, target.id, value.destinationRegionId, value.placeOrders) ||
      !movements(value.movedAreas, target.id, value.destinationRegionId, value.areaOrders)) return false;
  if (value.places === 'keep' ? value.deletedPlaceIds.length > 0 : value.movedPlaces.length > 0) return false;
  if (value.areas === 'keep' ? value.deletedAreaIds.length > 0 : value.movedAreas.length > 0) return false;
  const moved = value.movedPlaces.length + value.movedAreas.length;
  if (moved ? !['create', 'existing'].includes(value.destinationMode as string) ||
      !value.regionOrder.some(row => row.id === value.destinationRegionId && row.isCanonical) :
      value.destinationMode !== 'not-required' || value.destinationRegionId !== null) return false;
  return planningLoss(value.deletedDependentPlanning, {
    place: value.deletedPlaceIds, area: value.deletedAreaIds, segment: value.segmentEffects.deletedSegmentIds as string[]
  });
}

function placeSaved(value: Wire, route: StructureRoute, request: Wire, fields: string[]): boolean {
  if (!keysAre(value, ['id', 'fromRegionId', 'regionId', 'fieldDigests', 'placeOrders', 'segmentEffects', 'addressEffect']) ||
      value.id !== route.target.id || !isId(value.fromRegionId) || !isId(value.regionId) || !vectors(value.placeOrders) ||
      !segmentEffects(value.segmentEffects) || (value.segmentEffects.deletedSegmentIds as unknown[]).length ||
      !validDigests(value.fieldDigests, fields)) return false;
  const original = request.expected as Wire;
  if (object(original.place)) {
    const changes = request.changes as Wire;
    if (value.fromRegionId !== original.place.regionId || value.regionId !== changes.regionId ||
        value.placeOrders.length !== 2 || !value.placeOrders.some(row => row.regionId === value.fromRegionId) ||
        !value.placeOrders.some(row => row.regionId === value.regionId)) return false;
  } else if (value.fromRegionId !== original.regionId || value.regionId !== original.regionId || value.placeOrders.length !== 0) return false;
  return fields.includes('address') || fields.includes('location') ? address(value.addressEffect) : value.addressEffect === null;
}

/** Checks the exact successful result for the protected Web route and its frozen selected fields. */
export function validStructureResult(value: unknown, route: StructureRoute, request: Wire): value is Wire {
  if (!object(value)) return false;
  const { kind, target } = route;
  const changes = object(request.changes) ? request.changes : {};
  const fields = Object.keys(changes).filter(key => !['regionId', 'reverseGeocode'].includes(key) &&
    !(key === 'notesHtml' && changes[key] === null)).map(key => key === 'notesHtml' ? 'notes' : key);
  if (changes.reverseGeocode === true) {
    for (const field of ['address', 'location']) if (!fields.includes(field)) fields.push(field);
  }
  const parent = object(request.expected) ? request.expected.regionId : undefined;
  switch (kind) {
    case 'region-fields': case 'place-fields': case 'area-fields': case 'area-geometry':
      return keysAre(value, ['id', 'regionId', 'fieldDigests']) && value.id === target.id &&
        (kind === 'region-fields' ? value.regionId === null : isId(value.regionId) && value.regionId === parent) &&
        validDigests(value.fieldDigests, kind === 'area-geometry' ? ['geometry'] : fields);
    case 'region-create':
      return keysAre(value, ['id', 'displayOrder', 'fieldDigests', 'regionOrder']) && isId(value.id) && integer(value.displayOrder) &&
        validDigests(value.fieldDigests, ['name', 'notes', 'coverImage', 'center']) && regionOrder(value.regionOrder) &&
        value.regionOrder.some(row => row.id === value.id && row.displayOrder === value.displayOrder && !row.isCanonical);
    case 'region-order':
      return keysAre(value, ['regionOrder']) && regionOrder(value.regionOrder) &&
        sameIds(value.regionOrder.filter(row => !row.isCanonical).map(row => row.id), request.regionIds);
    case 'region-delete': return regionDeleted(value, route, request);
    case 'place-create': {
      const keys = ['id', 'regionId', 'displayOrder', 'fieldDigests', 'placeOrder', 'createdFallbackRegionId', 'regionOrder', 'addressEffect'];
      return keysAre(value, keys) && isId(value.id) && value.regionId === target.id && integer(value.displayOrder) &&
        value.createdFallbackRegionId === null && value.regionOrder === null && address(value.addressEffect) &&
        validDigests(value.fieldDigests, ['name', 'notes', 'iconName', 'markerColor', 'address', 'location']) &&
        vector(value.placeOrder) && value.placeOrder.regionId === target.id && vectorHas(value.placeOrder, value.id, value.displayOrder);
    }
    case 'place-save': return placeSaved(value, route, request, fields);
    case 'place-order': case 'area-order': {
      const key = kind === 'place-order' ? 'placeOrder' : 'areaOrder';
      const ordering = value[key];
      return keysAre(value, [key]) && vector(ordering) && ordering.regionId === target.id &&
        sameIds((ordering.items as Wire[]).map(row => row.id), kind === 'place-order' ? request.placeIds : request.areaIds);
    }
    case 'place-delete':
      return keysAre(value, ['id', 'regionId', 'planningDigest', 'deletedDependentPlanning', 'placeOrder', 'segmentEffects', 'visitEffects']) &&
        value.id === target.id && isId(value.regionId) && nullableDigest(value.planningDigest) &&
        vector(value.placeOrder) && value.placeOrder.regionId === value.regionId &&
        !(value.placeOrder.items as Wire[]).some(row => row.id === value.id) &&
        segmentEffects(value.segmentEffects) && visitEffects(value.visitEffects) &&
        planningLoss(value.deletedDependentPlanning, { segment: value.segmentEffects.deletedSegmentIds as string[] });
    case 'area-create':
      return keysAre(value, ['id', 'regionId', 'displayOrder', 'fieldDigests', 'areaOrder']) && isId(value.id) &&
        value.regionId === target.id && integer(value.displayOrder) && validDigests(value.fieldDigests, ['name', 'notes', 'fillHex', 'geometry']) &&
        vector(value.areaOrder) && value.areaOrder.regionId === target.id && vectorHas(value.areaOrder, value.id, value.displayOrder);
    case 'area-delete':
      return keysAre(value, ['id', 'regionId', 'planningDigest', 'areaOrder']) && value.id === target.id && isId(value.regionId) &&
        nullableDigest(value.planningDigest) && vector(value.areaOrder) && value.areaOrder.regionId === value.regionId &&
        !(value.areaOrder.items as Wire[]).some(row => row.id === value.id);
  }
}

const sameIds = (actual: unknown[], expected: unknown): boolean =>
  idList(expected) && actual.length === expected.length && actual.every((id, index) => id === expected[index]);
