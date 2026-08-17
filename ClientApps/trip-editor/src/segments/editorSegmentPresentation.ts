import type { SegmentRouteWorkState } from '../components/segmentRouteWorkState';
import type { EditorSegment, EditorSegmentDraft, EditorTripState, GeoJsonLineString, Guid } from '../types';
import {
  classifySegmentOrientation,
  resolveSegmentAnchors,
  type ResolvedSegmentAnchors,
  type SegmentAnchorInput,
  type SegmentOrientation
} from './segmentPresentationResolver';

export type SegmentPresentationKey =
  | { kind: 'persisted'; id: Guid }
  | { kind: 'create-draft'; token: 'segment-create-draft' };

export type EditorSegmentPresentationSource = 'S' | 'D' | 'W';

export type EditorSegmentPresentation = {
  key: SegmentPresentationKey;
  source: EditorSegmentPresentationSource;
  segmentId: Guid | null;
  anchors: ResolvedSegmentAnchors;
  coordinates: Array<[number, number]>;
  orientation: SegmentOrientation;
  directionTrustworthy: boolean;
  hasCustomRoute: boolean;
};

export type EditorSegmentDraftPresentation = {
  key: SegmentPresentationKey;
  draft: EditorSegmentDraft;
  work: SegmentRouteWorkState | null;
};

/** Resolves persisted S without retaining previously derived labels. */
export function resolvePersistedSegmentPresentation(segment: EditorSegment, state: EditorTripState): EditorSegmentPresentation {
  return resolvePresentation(
    { kind: 'persisted', id: segment.id },
    segment.id,
    'S',
    segment,
    null,
    state
  );
}

/** Resolves W over D for the active editor/create key, with no create S fallback. */
export function resolveDraftSegmentPresentation(snapshot: EditorSegmentDraftPresentation, state: EditorTripState): EditorSegmentPresentation {
  return resolvePresentation(snapshot.key, snapshot.draft.id, snapshot.work ? 'W' : 'D', snapshot.draft, snapshot.work, state);
}

/** Creates one fresh positional projection from the selected S, D, or W snapshot. */
function resolvePresentation(
  key: SegmentPresentationKey,
  segmentId: Guid | null,
  source: EditorSegmentPresentationSource,
  value: EditorSegment | EditorSegmentDraft,
  work: SegmentRouteWorkState | null,
  state: EditorTripState
): EditorSegmentPresentation {
  const anchors = work ? workAnchors(work) : valueAnchors(value, state);
  const resolvedAnchors = resolveSegmentAnchors(anchors);
  const geometry = work
    ? work.nodes.map(node => [...node.coordinate] as [number, number])
    : selectedGeometry(value, anchors);
  const hasCustomRoute = work ? work.origin === 'custom' || work.changedCustom : Boolean(value.route);
  const orientation = !hasCustomRoute && geometry.length >= 2
    ? 'forward'
    : classifySegmentOrientation(anchors, geometry, anchors.some(anchor => anchor.role === 'via'));
  return {
    key,
    source,
    segmentId,
    anchors: resolvedAnchors,
    coordinates: orientation === 'reversed' ? [...geometry].reverse().map(point => [...point] as [number, number]) : geometry,
    orientation,
    directionTrustworthy: orientation !== 'ambiguous',
    hasCustomRoute
  };
}

/** Builds semantic anchors directly from the current form or persisted aggregate order. */
function valueAnchors(value: EditorSegment | EditorSegmentDraft, state: EditorTripState): SegmentAnchorInput[] {
  const identities = [value.fromPlaceId, ...value.waypointPlaceIds, value.toPlaceId];
  return identities.map((placeId, position) => {
    const place = placeId ? state.placesById[placeId] : null;
    const role = position === 0 ? 'start' as const : position === identities.length - 1 ? 'end' as const : 'via' as const;
    const waypointIndex = role === 'via' ? value.waypointRouteVertexIndices[position - 1] : null;
    const routePointCount = value.route?.coordinates.length ?? 0;
    return {
      position,
      placeId,
      name: place?.name ?? null,
      role,
      location: place?.location ? [place.location.longitude, place.location.latitude] : null,
      routeVertexIndex: role === 'start' ? 0 : role === 'end' ? Math.max(0, routePointCount - 1) : waypointIndex
    };
  });
}

/** Projects fixed W anchors while anonymous route nodes retain their edit affordances. */
function workAnchors(work: SegmentRouteWorkState): SegmentAnchorInput[] {
  const anchors = work.nodes.filter(node => node.kind === 'anchor');
  return anchors.map((anchor, position) => ({
    position,
    placeId: anchor.placeId,
    name: anchor.placeName,
    role: anchor.role === 'from' ? 'start' : anchor.role === 'to' ? 'end' : 'via',
    location: [...anchor.coordinate],
    routeVertexIndex: work.nodes.indexOf(anchor)
  }));
}

/** Uses the authoritative effective route for S and semantic all-anchor fallback for D. */
function selectedGeometry(value: EditorSegment | EditorSegmentDraft, anchors: readonly SegmentAnchorInput[]): Array<[number, number]> {
  const effective = 'effectiveRoute' in value ? value.effectiveRoute : null;
  const route: GeoJsonLineString | null = value.route ?? effective;
  if (route?.coordinates.length) return route.coordinates.map(point => [...point]);
  return anchors.flatMap(anchor => anchor.location ? [[...anchor.location] as [number, number]] : []);
}
