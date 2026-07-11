export type Guid = string;
export type ViewerMode = 'private' | 'public' | 'embed';
export type EntityType = 'trip' | 'region' | 'place' | 'area' | 'segment';

export interface ViewerSelection {
  type: EntityType;
  id: Guid;
}

export interface TripViewerState {
  viewerMode: ViewerMode;
  trip: ViewerTrip;
  regionsById: Record<Guid, ViewerRegion>;
  regionOrder: Guid[];
  placesById: Record<Guid, ViewerPlace>;
  placeOrderByRegionId: Record<Guid, Guid[]>;
  areasById: Record<Guid, ViewerArea>;
  areaOrderByRegionId: Record<Guid, Guid[]>;
  segmentsById: Record<Guid, ViewerSegment>;
  segmentOrder: Guid[];
  tagsBySlug: Record<string, ViewerTag>;
  tagOrder: string[];
  visitProgress: ViewerVisitProgress;
  permissions: ViewerPermissions;
  actions: ViewerActions;
  map: ViewerMap;
}

export interface ViewerTrip {
  id: Guid;
  name: string;
  notes: ViewerNotes;
  isPublic: boolean;
  shareProgressEnabled: boolean;
  ownerDisplayName: string | null;
  coverImage: ViewerCoverImage | null;
  center: ViewerCoordinate | null;
  zoom: number | null;
  updatedAt: string;
  privateUrl: string | null;
  publicUrl: string;
  publicEmbedUrl: string;
}

export interface ViewerNotes {
  displayHtml: string;
  plainText: string;
  hasRenderableContent: boolean;
  hasTextContent: boolean;
  hasMediaContent: boolean;
}

export interface ViewerCoverImage {
  displayUrl: string;
  copyUrl: string | null;
  rawUrl: string | null;
}

export interface ViewerCoordinate {
  latitude: number;
  longitude: number;
}

export interface ViewerRegion {
  id: Guid;
  tripId: Guid;
  name: string;
  notes: ViewerNotes;
  coverImage: ViewerCoverImage | null;
  center: ViewerCoordinate | null;
  displayOrder: number;
  placeIds: Guid[];
  areaIds: Guid[];
}

export interface ViewerPlace {
  id: Guid;
  tripId: Guid;
  regionId: Guid;
  name: string;
  notes: ViewerNotes;
  address: string;
  location: ViewerCoordinate | null;
  iconName: string;
  markerColor: string;
  displayOrder: number;
  visitSummary: ViewerPlaceVisitSummary;
}

export interface ViewerArea {
  id: Guid;
  tripId: Guid;
  regionId: Guid;
  name: string;
  notes: ViewerNotes;
  fillHex: string | null;
  geometry: GeoJsonPolygon | null;
  displayOrder: number;
}

export interface ViewerSegment {
  id: Guid;
  tripId: Guid;
  fromPlaceId: Guid | null;
  toPlaceId: Guid | null;
  mode: string;
  estimatedDistanceKm: number | null;
  estimatedDurationMinutes: number | null;
  notes: ViewerNotes;
  route: GeoJsonLineString | null;
  fallbackStart: ViewerCoordinate | null;
  fallbackEnd: ViewerCoordinate | null;
  displayOrder: number;
}

export interface ViewerTag {
  id: Guid;
  name: string;
  slug: string;
}

export interface ViewerVisitProgress {
  canDisplayProgress: boolean;
  canDisplayCounts: boolean;
  canDisplayHistory: boolean;
  totalPlaces: number;
  visitedPlaces: number;
  percentVisited: number;
  placeSummariesByPlaceId: Record<Guid, ViewerPlaceVisitSummary>;
  historyRows: ViewerVisitHistoryRow[];
}

export interface ViewerPlaceVisitSummary {
  placeId: Guid;
  visitCount: number;
  isVisited: boolean;
  firstVisitAt: string | null;
  lastVisitAt: string | null;
}

export interface ViewerVisitHistoryRow {
  visitId: Guid;
  placeId: Guid;
  regionId: Guid;
  startedAt: string;
  endedAt: string | null;
  durationMinutes: number | null;
}

export interface ViewerPermissions {
  canViewPrivateState: boolean;
  canViewPublicState: boolean;
  canViewEmbedState: boolean;
  isOwner: boolean;
  canReadNotes: boolean;
  canReadVisitCounts: boolean;
  canReadVisitHistory: boolean;
  canToggleShareProgress: boolean;
  canUseReadableMode: boolean;
  canPrint: boolean;
}

export interface ViewerAction {
  allowed: boolean;
  url: string | null;
  method: string | null;
  requiresAuthentication: boolean;
}

export interface ViewerActions {
  edit: ViewerAction;
  clone: ViewerAction;
  exportWayfarerKml: ViewerAction;
  exportGoogleMyMapsKml: ViewerAction;
  exportPdf: ViewerAction;
  share: ViewerAction;
  copyPublicUrl: ViewerAction;
  copyCoverUrl: ViewerAction;
  copyMapSnapshotUrl: ViewerAction;
  fullscreen: ViewerAction;
  openCanonical: ViewerAction;
  readable: ViewerAction;
  print: ViewerAction;
}

export interface ViewerMap {
  initialView: ViewerMapInitialView;
  acceptedQueryParameters: string[];
  emittedQueryParameters: string[];
  tileUrlTemplate: string;
  tileAttribution: string;
}

export interface ViewerMapInitialView {
  latitude: number;
  longitude: number;
  zoom: number;
  source: string;
  canonicalQuery: string;
}

export interface GeoJsonLineString {
  type: 'LineString';
  coordinates: Array<[number, number]>;
}

export interface GeoJsonPolygon {
  type: 'Polygon';
  coordinates: Array<Array<[number, number]>>;
}
