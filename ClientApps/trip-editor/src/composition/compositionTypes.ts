import type { PlanningAggregate } from '../planning/planningTypes';
import type { TripCredit } from '../types';

/** Captured/current API graph. Missing optional values remain absent until an explicit field original is constructed. */
export type Kind = 'trip' | 'region' | 'place' | 'area' | 'segment';
export type Side = 'a' | 'b';
export interface Content {
  id: string; name?: string; title?: string | null; notes?: string | null; displayOrder?: number | null;
  center?: number[] | null; location?: number[] | null; coverImageUrl?: string | null;
  iconName?: string | null; markerColor?: string | null; fillHex?: string | null; geometryGeoJson?: string | null;
  address?: string | null; resolvedFeatureName?: string | null; resolvedFeatureType?: string | null;
  addressEnrichmentProvider?: string | null; addressEnrichmentStorageMode?: string | null; addressEnrichedAt?: string | null;
  fromPlaceId?: string | null; toPlaceId?: string | null; waypoints?: Waypoint[]; mode?: string;
  transportProfileId?: string | null; routeJson?: string | null; hasCustomRoute?: boolean;
  estimatedDistanceKm?: number | null; estimatedDurationTicks?: number | null; estimatedDurationMinutes?: number | null;
  estimatedDurationSource?: string; routeInstructionsJson?: string | null; routeProvider?: string | null;
  routeProviderConfigurationId?: string | null; routeProviderConfigurationVersion?: number | null;
  routeTransportProfileId?: string | null; routeGeneratedAt?: string | null; routeAttribution?: string | null;
  routeStorageMode?: string | null; routeMappingMode?: string | null;
}
export interface Waypoint { placeId: string; position: number; routeVertexIndex?: number | null }
export interface Region extends Content { places?: Content[]; areas?: Content[] }
export interface Graph extends Content {
  regions: Region[]; segments: Content[]; isPublic: boolean; centerLat?: number | null; centerLon?: number | null;
  zoom?: number | null; tags?: { name: string; slug: string }[]; contributionPolicy?: string | null; credits: TripCredit[];
}
export interface Snapshot {
  graph: Graph; regionOrder: string[]; placeOrder: Record<string, string[]>; areaOrder: Record<string, string[]>; mediaOmitted: boolean;
  planning?: { trip: PlanningAggregate | null; regions: Record<string, PlanningAggregate | null>;
    places: Record<string, PlanningAggregate | null>; areas: Record<string, PlanningAggregate | null>; segments: Record<string, PlanningAggregate | null> };
}
export interface Mapping { source: Side; kind: Kind; sourceId: string; destinationId: string; decision: string }
export interface Workspace { compositionId: string; tripId: string; sourceA: Snapshot; sourceB: Snapshot; current: Snapshot; mappings: Mapping[] }
export interface SavedComparison { compositionId: string; tripId: string; name: string; createdAt: string }
export interface SourceOption { id: string; name: string; owned: boolean; contributionPolicy: string | null }
export interface Candidate { destinationId: string; name: string; distanceKm: number | null; sameName: boolean;
  geometry: string | null; mappedChildren: number; mappedAnchors: boolean; sameMode: boolean | null; sameProfile: boolean | null }
export interface Selection { side: Side; kind: Kind; id: string }
export type Decision = 'copy' | 'reuse' | 'skip' | 'remove';
export interface FieldChoice { operation: 'keep' | 'useSource' | 'edit' | 'clear'; value: unknown }
export interface AnchorChoice { sourceId: string; destinationId: string; regionId: string; approveCopy: boolean }
/** A local reviewed choice, never an operation record. Tokens are deliberately excluded from retained drafts. */
export interface Choice {
  kind: Kind; sourceId: string; decision: Decision; destinationId: string; mappingId: string; customName: boolean; name: string;
  separate: boolean; readd: boolean; regionId: string; newRegionName: string; position: 'append' | 'start' | 'after'; afterId: string;
  withChildren: boolean; children: Choice[]; anchors: AnchorChoice[]; fields: Record<string, FieldChoice>;
  places: 'keep' | 'delete'; areas: 'keep' | 'delete';
}
export interface ReviewDraft { side: Side; items: Choice[]; baseline: Snapshot; mappings: Mapping[] }
export interface OrderOriginal { id: string; displayOrder: number | null }
export interface Placement { expected: OrderOriginal[]; position: string; afterId?: string }
/** Exact domain transport, separate from the editable controls. */
export interface ActionItem {
  kind: Kind; sourceId: string; decision: Decision; destinationId?: string; expectedParentId?: string;
  expectedDecision?: { destinationId: string; decision: string }; fields?: Record<string, { operation: string; expected: unknown; value?: unknown }>;
  name?: string; separate?: boolean; readd?: boolean; regionId?: string; newRegionName?: string;
  expectedRegions?: OrderOriginal[]; regionPlacement?: Placement; placement?: Placement; children?: ActionItem[];
  anchors?: { sourceId: string; destinationId: string | null; regionId: string | null; location: number[] | null }[];
  original?: unknown; conditionToken?: string; routeClearToken?: string; places?: string; areas?: string;
}
export interface Action { source: Side; items: ActionItem[] }
