export type Guid = string;

export interface EditorTripState {
  tripId: Guid;
  metadata: EditorTripMetadata;
  regionsById: Record<Guid, EditorRegion>;
  regionOrder: Guid[];
  placesById: Record<Guid, EditorPlace>;
  placeOrderByRegionId: Record<Guid, Guid[]>;
  areasById: Record<Guid, EditorArea>;
  areaOrderByRegionId: Record<Guid, Guid[]>;
  segmentsById: Record<Guid, EditorSegment>;
  segmentOrder: Guid[];
  tagsBySlug: Record<string, EditorTag>;
  tagOrder: string[];
  visitProgress: EditorVisitProgress;
  options: EditorOptions;
  permissions: EditorPermissions;
}

export interface EditorTripMetadata {
  id: Guid;
  name: string;
  notesHtml: string;
  isPublic: boolean;
  shareProgressEnabled: boolean;
  center: EditorCoordinate | null;
  zoom: number | null;
  coverImage: EditorImageReference | null;
  updatedAt: string;
  publicUrl: string | null;
  progressPublicUrl: string | null;
}

export interface EditorRegion {
  id: Guid;
  tripId: Guid;
  name: string;
  notesHtml: string;
  coverImage: EditorImageReference | null;
  center: EditorCoordinate | null;
  displayOrder: number;
  isShadow: boolean;
  capabilities: EditorEntityCapabilities;
}

export interface EditorPlace {
  id: Guid;
  tripId: Guid;
  regionId: Guid;
  name: string;
  notesHtml: string;
  address: string;
  location: EditorCoordinate | null;
  iconName: string;
  markerColor: string;
  displayOrder: number;
  visitSummary: EditorPlaceVisitSummary;
  capabilities: EditorEntityCapabilities;
}

export interface EditorArea {
  id: Guid;
  tripId: Guid;
  regionId: Guid;
  name: string;
  notesHtml: string;
  fillHex: string;
  geometry: GeoJsonPolygon;
  displayOrder: number;
  capabilities: EditorEntityCapabilities;
}

export interface EditorSegment {
  id: Guid;
  tripId: Guid;
  fromPlaceId: Guid | null;
  toPlaceId: Guid | null;
  mode: string;
  estimatedDistanceKm: number | null;
  estimatedDurationMinutes: number | null;
  notesHtml: string;
  route: GeoJsonLineString | null;
  displayOrder: number;
  capabilities: EditorEntityCapabilities;
}

export interface EditorTag {
  id: Guid;
  name: string;
  slug: string;
}

export interface EditorVisitProgress {
  totalPlaces: number;
  visitedPlaces: number;
  percentVisited: number;
  placeSummariesByPlaceId: Record<Guid, EditorPlaceVisitSummary>;
  historyRows: EditorVisitHistoryRow[];
}

export interface EditorPlaceVisitSummary {
  placeId: Guid;
  visitCount: number;
  isVisited: boolean;
  firstVisitAt: string | null;
  lastVisitAt: string | null;
}

export interface EditorVisitHistoryRow {
  visitId: Guid;
  placeId: Guid;
  regionId: Guid;
  startedAt: string;
  endedAt: string | null;
  durationMinutes: number | null;
}

export interface EditorOptions {
  iconNames: string[];
  markerColorClasses: string[];
  glyphColorClasses: string[];
  transportModes: Array<{ value: string; label: string; speedKmh: number }>;
  areaDefaults: { name: string; fillHex: string };
  tag: { maxTags: number; suggestionTake: number; allowedPatternDescription: string };
  limits: { nominatimSearchLimit: number; sidebarSearchMinCharacters: number };
}

export interface EditorPermissions {
  canEditTrip: boolean;
  canEditMetadata: boolean;
  canEditRegions: boolean;
  canEditPlaces: boolean;
  canEditAreas: boolean;
  canEditSegments: boolean;
  canEditTags: boolean;
  canToggleShareProgress: boolean;
  canReadVisitProgress: boolean;
}

export interface EditorEntityCapabilities {
  canEdit: boolean;
  canRename: boolean;
  canDelete: boolean;
  canReorder: boolean;
  canMove: boolean;
  canAddChildren: boolean;
  canTargetForSearchAdd: boolean;
}

export interface EditorCoordinate {
  latitude: number;
  longitude: number;
}

export interface EditorImageReference {
  rawUrl: string;
  proxiedUrl: string;
}

export interface GeoJsonLineString {
  type: 'LineString';
  coordinates: Array<[number, number]>;
}

export interface GeoJsonPolygon {
  type: 'Polygon';
  coordinates: Array<Array<[number, number]>>;
}

export interface BootstrapConfig {
  tripId: Guid;
  tripName: string;
  editorEndpoint: string;
  tilesUrl: string;
  antiforgeryToken: string;
}
