import type { Guid, GeoJsonLineString, EditorEntityCapabilities, EditorExternalRoutingCapability, EditorSegmentWaypointDraftRow } from '../types';

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
  /** Original material route/dependency proof; a Notes refresh never supplies a replacement draft base. */
  originalRouteToken: string;
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

/** Route/create wire fields; the save owner selects the strict dialect and optional authored Notes. */
export interface EditorSegmentSaveRequest {
  proposal?: { proposalId: Guid; protectedContext: string; manualDurationOverride: boolean };
  fromPlaceId: Guid | null;
  toPlaceId: Guid | null;
  waypointPlaceIds: Guid[];
  waypointRouteVertexIndices: Array<number | null>;
  mode: string | null;
  estimatedDistanceKm: number | null;
  estimatedDurationMinutes: number | null;
  estimatedDurationSource: 'Automatic' | 'Manual';
  notesHtml?: string | null;
  route: GeoJsonLineString | null;
  aggregateConcurrencyToken?: string | null;
  /** Fail-closed C4 dialect omits both legacy required fields. */
  contract?: 'segment-route-notes-1';
  originalRouteToken?: string | null;
  expected?: { notesHtml: string | null };
  changes?: { notesHtml: string };
}

/** Component-owned route and Notes draft with independently pinned route evidence. */
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
  /** Pinned at authoritative open/reset, independently of nullable Notes originals. */
  originalRouteToken: string | null;
}
