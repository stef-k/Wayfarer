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

export interface EditorTripMetadataUpdateRequest {
  name: string;
  notesHtml: string | null;
  isPublic: boolean;
  coverImage: { rawUrl: string | null } | null;
  center: EditorCoordinate | null;
  zoom: number | null;
}

export interface EditorTripTagsUpdateRequest {
  tags: string[];
}

export interface EditorShareProgressUpdateRequest {
  enabled: boolean;
}

export interface TagSuggestion {
  name: string;
  slug: string;
  count: number;
}

export interface EditorRegionSaveRequest {
  name: string;
  notesHtml: string | null;
  coverImage: { rawUrl: string | null } | null;
  center: EditorCoordinate | null;
}

export interface EditorRegionOrderRequest {
  regionIds: Guid[];
}

export interface EditorRegionOrderResult {
  regionOrder: Guid[];
}

export interface EditorPlaceSaveRequest {
  regionId?: Guid;
  name: string;
  notesHtml: string | null;
  address: string | null;
  location: EditorCoordinate | null;
  iconName: string;
  markerColor: string;
  reverseGeocode: boolean;
}

export interface EditorPlaceOrderRequest {
  placeIds: Guid[];
}

export interface EditorPlaceOrderResult {
  regionId: Guid;
  placeOrder: Guid[];
}

export interface EditorPlaceDeleteResult {
  placeId: Guid;
}

export interface EditorPlaceDraft {
  id: Guid | null;
  regionId: Guid | null;
  name: string;
  notesHtml: string;
  address: string;
  latitude: string | number;
  longitude: string | number;
  iconName: string;
  markerColor: string;
  reverseGeocode: boolean;
}

export interface EditorMutationResult<TData> {
  success: true;
  data: TData;
  affected: EditorAffectedSlices;
  deletedIds: EditorDeletedIds;
  warnings: EditorWarning[];
}

export interface EditorAffectedSlices {
  metadata: EditorTripMetadata | null;
  regions: EditorRegion[];
  regionOrder: Guid[] | null;
  places: EditorPlace[];
  placeOrdersByRegionId: Record<Guid, Guid[]>;
  areas: EditorArea[];
  areaOrdersByRegionId: Record<Guid, Guid[]>;
  segments: EditorSegment[];
  segmentOrder: Guid[] | null;
  tags: EditorTag[];
  tagOrder: string[] | null;
  visitProgress: EditorVisitProgress | null;
  options: EditorOptions | null;
}

export interface EditorDeletedIds {
  regions: Guid[];
  places: Guid[];
  areas: Guid[];
  segments: Guid[];
  tags: string[];
}

export interface EditorWarning {
  code: string;
  message: string;
  entityType: string | null;
  entityId: string | null;
}

export interface ValidationProblemDetails {
  title?: string;
  status?: number;
  errors?: Record<string, string[]>;
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
  tripIndexUrl: string;
  tilesUrl: string;
  antiforgeryToken: string;
}
