import type {
  GeoJsonLineString,
  GeoJsonPolygon,
  Guid,
  TripViewerState,
  ViewerAction,
  ViewerActions,
  ViewerArea,
  ViewerCoordinate,
  ViewerCoverImage,
  ViewerMap,
  ViewerMapInitialView,
  ViewerMode,
  ViewerNotes,
  ViewerPermissions,
  ViewerPlace,
  ViewerPlaceVisitSummary,
  ViewerRegion,
  ViewerSegment,
  ViewerTag,
  ViewerTrip,
  ViewerVisitHistoryRow,
  ViewerVisitProgress
} from './types';

const viewerModes = new Set<ViewerMode>(['private', 'public', 'embed']);

export function normalizeViewerState(raw: unknown): TripViewerState {
  const source = objectValue(raw);
  const viewerMode = stringValue(source, 'viewerMode') as ViewerMode;
  if (!viewerModes.has(viewerMode)) {
    throw new Error('Trip Viewer state returned an invalid viewer mode.');
  }

  return {
    viewerMode,
    trip: readTrip(requiredObject(source, 'trip')),
    regionsById: readRecord(source, 'regionsById', readRegion),
    regionOrder: readGuidArray(source, 'regionOrder'),
    placesById: readRecord(source, 'placesById', readPlace),
    placeOrderByRegionId: readGuidArrayRecord(source, 'placeOrderByRegionId'),
    areasById: readRecord(source, 'areasById', readArea),
    areaOrderByRegionId: readGuidArrayRecord(source, 'areaOrderByRegionId'),
    segmentsById: readRecord(source, 'segmentsById', readSegment),
    segmentOrder: readGuidArray(source, 'segmentOrder'),
    tagsBySlug: readRecord(source, 'tagsBySlug', readTag),
    tagOrder: stringArrayValue(source, 'tagOrder'),
    visitProgress: readVisitProgress(requiredObject(source, 'visitProgress')),
    permissions: readPermissions(requiredObject(source, 'permissions')),
    actions: readActions(requiredObject(source, 'actions')),
    map: readMap(requiredObject(source, 'map'))
  };
}

function readTrip(source: Record<string, unknown>): ViewerTrip {
  return {
    id: stringValue(source, 'id'),
    name: stringValue(source, 'name'),
    notes: readNotes(requiredObject(source, 'notes')),
    isPublic: booleanValue(source, 'isPublic'),
    shareProgressEnabled: booleanValue(source, 'shareProgressEnabled'),
    ownerDisplayName: nullableStringValue(source, 'ownerDisplayName'),
    coverImage: readNullableObject(source, 'coverImage', readCoverImage),
    center: readNullableObject(source, 'center', readCoordinate),
    zoom: nullableNumberValue(source, 'zoom'),
    updatedAt: stringValue(source, 'updatedAt'),
    privateUrl: nullableStringValue(source, 'privateUrl'),
    publicUrl: stringValue(source, 'publicUrl'),
    publicEmbedUrl: stringValue(source, 'publicEmbedUrl')
  };
}

function readRegion(source: Record<string, unknown>): ViewerRegion {
  return {
    id: stringValue(source, 'id'),
    tripId: stringValue(source, 'tripId'),
    name: stringValue(source, 'name'),
    notes: readNotes(requiredObject(source, 'notes')),
    coverImage: readNullableObject(source, 'coverImage', readCoverImage),
    center: readNullableObject(source, 'center', readCoordinate),
    displayOrder: numberValue(source, 'displayOrder'),
    placeIds: readGuidArray(source, 'placeIds'),
    areaIds: readGuidArray(source, 'areaIds')
  };
}

function readPlace(source: Record<string, unknown>): ViewerPlace {
  return {
    id: stringValue(source, 'id'),
    tripId: stringValue(source, 'tripId'),
    regionId: stringValue(source, 'regionId'),
    name: stringValue(source, 'name'),
    notes: readNotes(requiredObject(source, 'notes')),
    address: stringValue(source, 'address'),
    location: readNullableObject(source, 'location', readCoordinate),
    iconName: stringValue(source, 'iconName'),
    markerColor: stringValue(source, 'markerColor'),
    displayOrder: numberValue(source, 'displayOrder'),
    visitSummary: readVisitSummary(requiredObject(source, 'visitSummary'))
  };
}

function readArea(source: Record<string, unknown>): ViewerArea {
  return {
    id: stringValue(source, 'id'),
    tripId: stringValue(source, 'tripId'),
    regionId: stringValue(source, 'regionId'),
    name: stringValue(source, 'name'),
    notes: readNotes(requiredObject(source, 'notes')),
    fillHex: nullableStringValue(source, 'fillHex'),
    geometry: readGeometry(source, 'geometry', 'Polygon'),
    displayOrder: numberValue(source, 'displayOrder')
  };
}

function readSegment(source: Record<string, unknown>): ViewerSegment {
  return {
    id: stringValue(source, 'id'),
    tripId: stringValue(source, 'tripId'),
    fromPlaceId: nullableStringValue(source, 'fromPlaceId'),
    toPlaceId: nullableStringValue(source, 'toPlaceId'),
    mode: stringValue(source, 'mode'),
    estimatedDistanceKm: nullableNumberValue(source, 'estimatedDistanceKm'),
    estimatedDurationMinutes: nullableNumberValue(source, 'estimatedDurationMinutes'),
    notes: readNotes(requiredObject(source, 'notes')),
    route: readGeometry(source, 'route', 'LineString'),
    fallbackStart: readNullableObject(source, 'fallbackStart', readCoordinate),
    fallbackEnd: readNullableObject(source, 'fallbackEnd', readCoordinate),
    displayOrder: numberValue(source, 'displayOrder')
  };
}

function readTag(source: Record<string, unknown>): ViewerTag {
  return {
    id: stringValue(source, 'id'),
    name: stringValue(source, 'name'),
    slug: stringValue(source, 'slug')
  };
}

function readNotes(source: Record<string, unknown>): ViewerNotes {
  return {
    displayHtml: stringValue(source, 'displayHtml'),
    plainText: stringValue(source, 'plainText'),
    hasRenderableContent: booleanValue(source, 'hasRenderableContent'),
    hasTextContent: booleanValue(source, 'hasTextContent'),
    hasMediaContent: booleanValue(source, 'hasMediaContent')
  };
}

function readCoverImage(source: Record<string, unknown>): ViewerCoverImage {
  return {
    displayUrl: stringValue(source, 'displayUrl'),
    copyUrl: nullableStringValue(source, 'copyUrl'),
    rawUrl: nullableStringValue(source, 'rawUrl')
  };
}

function readCoordinate(source: Record<string, unknown>): ViewerCoordinate {
  return {
    latitude: numberValue(source, 'latitude'),
    longitude: numberValue(source, 'longitude')
  };
}

function readVisitProgress(source: Record<string, unknown>): ViewerVisitProgress {
  return {
    canDisplayProgress: booleanValue(source, 'canDisplayProgress'),
    canDisplayCounts: booleanValue(source, 'canDisplayCounts'),
    canDisplayHistory: booleanValue(source, 'canDisplayHistory'),
    totalPlaces: numberValue(source, 'totalPlaces'),
    visitedPlaces: numberValue(source, 'visitedPlaces'),
    percentVisited: numberValue(source, 'percentVisited'),
    placeSummariesByPlaceId: readRecord(source, 'placeSummariesByPlaceId', readVisitSummary),
    historyRows: readObjectArray(source, 'historyRows', readVisitHistoryRow)
  };
}

function readVisitSummary(source: Record<string, unknown>): ViewerPlaceVisitSummary {
  return {
    placeId: stringValue(source, 'placeId'),
    visitCount: numberValue(source, 'visitCount'),
    isVisited: booleanValue(source, 'isVisited'),
    firstVisitAt: nullableStringValue(source, 'firstVisitAt'),
    lastVisitAt: nullableStringValue(source, 'lastVisitAt')
  };
}

function readVisitHistoryRow(source: Record<string, unknown>): ViewerVisitHistoryRow {
  return {
    visitId: stringValue(source, 'visitId'),
    placeId: stringValue(source, 'placeId'),
    regionId: stringValue(source, 'regionId'),
    startedAt: stringValue(source, 'startedAt'),
    endedAt: nullableStringValue(source, 'endedAt'),
    durationMinutes: nullableNumberValue(source, 'durationMinutes')
  };
}

function readPermissions(source: Record<string, unknown>): ViewerPermissions {
  return {
    canViewPrivateState: booleanValue(source, 'canViewPrivateState'),
    canViewPublicState: booleanValue(source, 'canViewPublicState'),
    canViewEmbedState: booleanValue(source, 'canViewEmbedState'),
    isOwner: booleanValue(source, 'isOwner'),
    canReadNotes: booleanValue(source, 'canReadNotes'),
    canReadVisitCounts: booleanValue(source, 'canReadVisitCounts'),
    canReadVisitHistory: booleanValue(source, 'canReadVisitHistory'),
    canToggleShareProgress: booleanValue(source, 'canToggleShareProgress'),
    canUseReadableMode: booleanValue(source, 'canUseReadableMode'),
    canPrint: booleanValue(source, 'canPrint')
  };
}

function readActions(source: Record<string, unknown>): ViewerActions {
  return {
    edit: readAction(requiredObject(source, 'edit')),
    clone: readAction(requiredObject(source, 'clone')),
    exportWayfarerKml: readAction(requiredObject(source, 'exportWayfarerKml')),
    exportGoogleMyMapsKml: readAction(requiredObject(source, 'exportGoogleMyMapsKml')),
    exportPdf: readAction(requiredObject(source, 'exportPdf')),
    share: readAction(requiredObject(source, 'share')),
    copyPublicUrl: readAction(requiredObject(source, 'copyPublicUrl')),
    copyCoverUrl: readAction(requiredObject(source, 'copyCoverUrl')),
    copyMapSnapshotUrl: readAction(requiredObject(source, 'copyMapSnapshotUrl')),
    fullscreen: readAction(requiredObject(source, 'fullscreen')),
    openCanonical: readAction(requiredObject(source, 'openCanonical')),
    readable: readAction(requiredObject(source, 'readable')),
    print: readAction(requiredObject(source, 'print'))
  };
}

function readAction(source: Record<string, unknown>): ViewerAction {
  return {
    allowed: booleanValue(source, 'allowed'),
    url: nullableStringValue(source, 'url'),
    method: nullableStringValue(source, 'method'),
    requiresAuthentication: booleanValue(source, 'requiresAuthentication')
  };
}

function readMap(source: Record<string, unknown>): ViewerMap {
  return {
    initialView: readMapInitialView(requiredObject(source, 'initialView')),
    acceptedQueryParameters: stringArrayValue(source, 'acceptedQueryParameters'),
    emittedQueryParameters: stringArrayValue(source, 'emittedQueryParameters'),
    tileUrlTemplate: stringValue(source, 'tileUrlTemplate'),
    tileAttribution: stringValue(source, 'tileAttribution')
  };
}

function readMapInitialView(source: Record<string, unknown>): ViewerMapInitialView {
  return {
    latitude: numberValue(source, 'latitude'),
    longitude: numberValue(source, 'longitude'),
    zoom: numberValue(source, 'zoom'),
    source: stringValue(source, 'source'),
    canonicalQuery: stringValue(source, 'canonicalQuery')
  };
}

function readRecord<T>(source: Record<string, unknown>, key: string, map: (value: Record<string, unknown>) => T): Record<Guid, T> {
  return Object.fromEntries(Object.entries(requiredObject(source, key)).map(([id, value]) => [id, map(objectValue(value))]));
}

function readGuidArrayRecord(source: Record<string, unknown>, key: string): Record<Guid, Guid[]> {
  return Object.fromEntries(Object.entries(requiredObject(source, key)).map(([id, value]) => [id, arrayValue(value).filter(isString)]));
}

function readObjectArray<T>(source: Record<string, unknown>, key: string, map: (value: Record<string, unknown>) => T): T[] {
  return arrayValue(readField(source, key)).map(value => map(objectValue(value)));
}

function readGuidArray(source: Record<string, unknown>, key: string): Guid[] {
  return arrayValue(readField(source, key)).filter(isString);
}

function stringArrayValue(source: Record<string, unknown>, key: string): string[] {
  return arrayValue(readField(source, key)).filter(isString);
}

function readNullableObject<T>(source: Record<string, unknown>, key: string, map: (value: Record<string, unknown>) => T): T | null {
  const value = readField(source, key);
  return value && typeof value === 'object' && !Array.isArray(value) ? map(value as Record<string, unknown>) : null;
}

function readGeometry<T extends GeoJsonLineString | GeoJsonPolygon>(source: Record<string, unknown>, key: string, type: T['type']): T | null {
  const value = readField(source, key);
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
  const geometry = value as { type?: unknown; coordinates?: unknown };
  return geometry.type === type && Array.isArray(geometry.coordinates) ? value as T : null;
}

function requiredObject(source: Record<string, unknown>, key: string): Record<string, unknown> {
  return objectValue(readField(source, key));
}

function objectValue(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error('Trip Viewer state returned an unexpected shape.');
  }

  return value as Record<string, unknown>;
}

function arrayValue(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

function stringValue(source: Record<string, unknown>, key: string): string {
  const value = readField(source, key);
  return typeof value === 'string' ? value : '';
}

function nullableStringValue(source: Record<string, unknown>, key: string): string | null {
  const value = readField(source, key);
  return typeof value === 'string' && value.length > 0 ? value : null;
}

function numberValue(source: Record<string, unknown>, key: string): number {
  const value = readField(source, key);
  return typeof value === 'number' && Number.isFinite(value) ? value : 0;
}

function nullableNumberValue(source: Record<string, unknown>, key: string): number | null {
  const value = readField(source, key);
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function booleanValue(source: Record<string, unknown>, key: string): boolean {
  return readField(source, key) === true;
}

function readField(source: Record<string, unknown>, key: string): unknown {
  const pascalKey = `${key.charAt(0).toUpperCase()}${key.slice(1)}`;
  return source[key] ?? source[pascalKey] ?? null;
}

function isString(value: unknown): value is string {
  return typeof value === 'string';
}
