import type { Guid, GeoJsonLineString, EditorEntityCapabilities, EditorExternalRoutingCapability } from '../types';

/** Complete published Segment, retaining existing route and measurement contracts. */
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
  /** Server-published nullable Notes, independent of the displayed empty-string default. */
  originalValues?: { notesHtml: string | null };
  externalRouting?: EditorExternalRoutingCapability | null;
}

/** Protected Notes-only intent never carries an aggregate route draft or a raw row version. */
export interface EditorSegmentNotesConditionalRequest {
  expected: { notesHtml: string | null };
  changes: { notesHtml: string };
}
