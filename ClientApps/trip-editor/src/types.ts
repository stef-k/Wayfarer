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

export interface EditorExternalRoutingCapability {
  available: boolean;
  unavailableReason: string | null;
  providerDisplayName: string | null;
  mappedProfileLabel: string | null;
  disclosure: string | null;
  attribution: string | null;
}

export interface ExternalRouteProposal {
  proposalId: Guid;
  segmentId: Guid;
  geometry: Array<{ longitude: number; latitude: number }>;
  waypointIndices: number[];
  protectedContext: string;
  expiresAt: string;
}

export interface AcceptedExternalRouteProposal {
  proposalId: Guid;
  segmentId: Guid;
  geometry: Array<{ longitude: number; latitude: number }>;
  waypointIndices: number[];
  aggregateConcurrencyToken?: string | null;
}

export interface EditorPlace {
  id: Guid;
  tripId: Guid;
  regionId: Guid;
  name: string;
  notesHtml: string;
  address: string;
  resolvedFeatureName?: string | null;
  resolvedFeatureType?: string | null;
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
  waypointPlaceIds: Guid[];
  waypointRouteVertexIndices: Array<number | null>;
  mode: string;
  transportProfileId: Guid | null;
  hasCustomRoute: boolean;
  estimatedDistanceKm: number | null;
  estimatedDurationMinutes: number | null;
  estimatedDurationSource: 'Automatic' | 'Manual';
  notesHtml: string;
  route: GeoJsonLineString | null;
  effectiveRoute: GeoJsonLineString | null;
  aggregateConcurrencyToken: string;
  displayOrder: number;
  capabilities: EditorEntityCapabilities;
  externalRouting?: EditorExternalRoutingCapability | null;
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
  transportModes: Array<{ value: string; label: string; speedKmh: number | null }>;
  areaDefaults: { name: string; fillHex: string };
  tag: { maxTags: number; suggestionTake: number; allowedPatternDescription: string };
  limits: { nominatimSearchLimit: number; sidebarSearchMinCharacters: number };
}

export interface EditorGeocodeSearchResponse {
  query: string;
  attribution: string;
  results: EditorGeocodeSearchResult[];
}

export interface EditorGeocodeSearchResult {
  id: string;
  provider: string;
  name: string;
  displayName: string;
  address: string;
  category: string | null;
  type: string | null;
  latitude: number;
  longitude: number;
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

export interface EditorLifecycleDependencySample {
  count: number;
  ids: Guid[];
  hasMore: boolean;
}

export interface EditorLifecycleWaypointAssociation {
  segmentId: Guid;
  placeId: Guid;
}

export interface EditorLifecycleAssociationSample {
  count: number;
  ids: EditorLifecycleWaypointAssociation[];
  hasMore: boolean;
}

export interface EditorLifecycleConflict {
  code: string;
  operation: string;
  targetId: Guid;
  endpointSegments: EditorLifecycleDependencySample;
  waypointOnlySegments: EditorLifecycleDependencySample;
  waypointAssociations: EditorLifecycleAssociationSample;
  deletedPlaces: EditorLifecycleDependencySample;
  deletedAreas: EditorLifecycleDependencySample;
  confirmationToken: string;
  expiresAt: string;
}

export interface EditorAreaSaveRequest {
  name: string;
  notesHtml: string | null;
  fillHex: string;
  geometry: GeoJsonPolygon | null;
}

export interface EditorAreaOrderRequest {
  areaIds: Guid[];
}

export interface EditorAreaOrderResult {
  regionId: Guid;
  areaOrder: Guid[];
}

export interface EditorAreaDeleteResult {
  areaId: Guid;
}

export interface EditorSegmentSaveRequest {
  fromPlaceId: Guid | null;
  toPlaceId: Guid | null;
  waypointPlaceIds: Guid[];
  waypointRouteVertexIndices: Array<number | null>;
  mode: string | null;
  estimatedDistanceKm: number | null;
  estimatedDurationMinutes: number | null;
  estimatedDurationSource: 'Automatic' | 'Manual';
  notesHtml: string | null;
  route: GeoJsonLineString | null;
  aggregateConcurrencyToken: string | null;
}

export interface EditorSegmentOrderRequest {
  segmentIds: Guid[];
}

export interface EditorSegmentOrderResult {
  segmentOrder: Guid[];
}

export interface EditorSegmentDeleteResult {
  segmentId: Guid;
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

export interface EditorAreaDraft {
  id: Guid | null;
  regionId: Guid | null;
  name: string;
  notesHtml: string;
  fillHex: string;
  geometry: GeoJsonPolygon | null;
}

export interface EditorSegmentDraft {
  id: Guid | null;
  fromPlaceId: Guid | null;
  toPlaceId: Guid | null;
  waypointPlaceIds: Guid[];
  waypointRouteVertexIndices: Array<number | null>;
  /** Client-only logical rows; identifiers never cross the API boundary. */
  waypointRows: EditorSegmentWaypointDraftRow[];
  mode: string;
  transportProfileId: Guid | null;
  estimatedDistanceKm: string | number;
  estimatedDurationMinutes: string | number;
  estimatedDurationSource: 'Automatic' | 'Manual';
  notesHtml: string;
  route: GeoJsonLineString | null;
  effectiveRoute: GeoJsonLineString | null;
  aggregateConcurrencyToken: string | null;
}

export interface EditorSegmentWaypointDraftRow {
  clientId: string;
  placeId: Guid;
  routeVertexIndex: number | null;
}

export interface EditorSegmentConflict {
  code: string;
  operation: string;
  currentSegment: EditorSegment;
  warning: string;
  expiresAt: string | null;
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
  code?: string;
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
