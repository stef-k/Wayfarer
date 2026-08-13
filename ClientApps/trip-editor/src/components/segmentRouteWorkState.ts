import type { EditorSegmentDraft, EditorTripState, GeoJsonLineString, Guid } from '../types';

export type RouteCoordinate = [number, number];

export type SegmentRouteAnchorNode = {
  kind: 'anchor';
  key: string;
  role: 'from' | 'waypoint' | 'to';
  placeId: Guid;
  placeName: string;
  waypointClientId: string | null;
  coordinate: RouteCoordinate;
};

export type SegmentRouteAnonymousNode = {
  kind: 'anonymous';
  key: string;
  coordinate: RouteCoordinate;
};

export type SegmentRouteWorkNode = SegmentRouteAnchorNode | SegmentRouteAnonymousNode;

export type SegmentRouteWorkState = {
  nodes: SegmentRouteWorkNode[];
  origin: 'custom' | 'fallback';
  changedCustom: boolean;
  cleared: boolean;
  nextAnonymousId: number;
};

export type SegmentRouteProjection = {
  route: GeoJsonLineString | null;
  waypointRouteVertexIndices: Array<number | null>;
  unchangedFallback: boolean;
  changedCustom: boolean;
};

export type SegmentRouteWorkResult =
  | { ok: true; state: SegmentRouteWorkState }
  | { ok: false; message: string };

const coordinateTolerance = 0.0000001;

/** Reconstructs semantic route work without inferring anchor identity from proximity. */
export function constructSegmentRouteWorkState(draft: EditorSegmentDraft, editorState: EditorTripState): SegmentRouteWorkResult {
  const anchors = buildAnchors(draft, editorState);
  if (!anchors.ok) return anchors;

  if (draft.route === null) {
    if (anchors.nodes.length < 2) return fail('Route work requires at least two located anchors.');
    return success(anchors.nodes, 'fallback');
  }

  const coordinates = validCoordinates(draft.route);
  if (!coordinates) return fail('The saved custom route is malformed. Reload or repair the Segment before editing its route.');
  if (anchors.nodes.length === 0 && draft.waypointRows.length === 0) {
    return success(coordinates.map((coordinate, index) => anonymous(index + 1, coordinate)), 'custom');
  }
  if (anchors.nodes.length < 2) return fail('The custom route is missing a required endpoint anchor.');

  const waypointIndices = draft.waypointRows.map(row => row.routeVertexIndex);
  if (waypointIndices.some(index => index === null)) return fail('The custom route has a missing waypoint index.');
  const numericIndices = waypointIndices as number[];
  if (!strictlyIncreasingInterior(numericIndices, coordinates.length)) return fail('The custom route has an invalid waypoint index order.');

  const anchorByIndex = new Map<number, SegmentRouteAnchorNode>([[0, anchors.nodes[0]], [coordinates.length - 1, anchors.nodes.at(-1)!]]);
  numericIndices.forEach((index, position) => anchorByIndex.set(index, anchors.nodes[position + 1]));
  for (const [index, anchor] of anchorByIndex) {
    if (!sameCoordinate(coordinates[index], anchor.coordinate)) return fail('A custom route anchor no longer matches its saved Place. Reload before route editing.');
  }

  let anonymousId = 0;
  const nodes = coordinates.map((coordinate, index) => anchorByIndex.get(index) ?? anonymous(++anonymousId, coordinate));
  return { ok: true, state: { nodes, origin: 'custom', changedCustom: false, cleared: false, nextAnonymousId: anonymousId + 1 } };
}

/** Inserts one anonymous midpoint after an existing route node. */
export function insertAnonymousNode(state: SegmentRouteWorkState, afterKey: string): SegmentRouteAnonymousNode | null {
  const index = state.nodes.findIndex(node => node.key === afterKey);
  if (index < 0 || index >= state.nodes.length - 1) return null;
  const left = state.nodes[index].coordinate;
  const right = state.nodes[index + 1].coordinate;
  const node = anonymous(state.nextAnonymousId++, [(left[0] + right[0]) / 2, (left[1] + right[1]) / 2]);
  state.nodes.splice(index + 1, 0, node);
  markCustom(state);
  return node;
}

/** Moves only an anonymous route point after finite range validation. */
export function moveAnonymousNode(state: SegmentRouteWorkState, key: string, coordinate: RouteCoordinate): boolean {
  const node = state.nodes.find(candidate => candidate.key === key);
  if (!node || node.kind !== 'anonymous' || !isCoordinate(coordinate)) return false;
  node.coordinate = [...coordinate];
  markCustom(state);
  return true;
}

/** Removes only an anonymous node while retaining a valid LineString. */
export function removeAnonymousNode(state: SegmentRouteWorkState, key: string): boolean {
  const index = state.nodes.findIndex(node => node.key === key);
  if (index < 0 || state.nodes[index].kind !== 'anonymous' || state.nodes.length <= 2) return false;
  state.nodes.splice(index, 1);
  markCustom(state);
  return true;
}

/** Restores every fixed anchor and removes all anonymous route points. */
export function clearAnonymousNodes(state: SegmentRouteWorkState): void {
  state.nodes = state.nodes.filter(node => node.kind === 'anchor');
  state.cleared = true;
  state.changedCustom = false;
}

/** Derives geometry and waypoint indices atomically from one work-state snapshot. */
export function projectSegmentRouteWork(state: SegmentRouteWorkState): SegmentRouteProjection | null {
  if (state.nodes.length < 2 || state.nodes.some(node => !isCoordinate(node.coordinate))) return null;
  const waypointIndices = state.nodes
    .map((node, index) => node.kind === 'anchor' && node.role === 'waypoint' ? index : null)
    .filter((index): index is number => index !== null);
  if (state.cleared || (state.origin === 'fallback' && !state.changedCustom)) {
    return { route: null, waypointRouteVertexIndices: waypointIndices.map(() => null), unchangedFallback: !state.cleared, changedCustom: false };
  }
  return {
    route: { type: 'LineString', coordinates: state.nodes.map(node => [...node.coordinate]) },
    waypointRouteVertexIndices: waypointIndices,
    unchangedFallback: false,
    changedCustom: true
  };
}

/** Produces the current map geometry without changing persistence semantics. */
export const workStateGeometry = (state: SegmentRouteWorkState): GeoJsonLineString => ({
  type: 'LineString',
  coordinates: state.nodes.map(node => [...node.coordinate])
});

/** Clones work state so lifecycle snapshots never share mutable node coordinates. */
export const cloneSegmentRouteWorkState = (state: SegmentRouteWorkState): SegmentRouteWorkState =>
  JSON.parse(JSON.stringify(state)) as SegmentRouteWorkState;

function buildAnchors(draft: EditorSegmentDraft, state: EditorTripState): { ok: true; nodes: SegmentRouteAnchorNode[] } | { ok: false; message: string } {
  if (draft.waypointRows.length !== draft.waypointPlaceIds.length || draft.waypointRows.length !== draft.waypointRouteVertexIndices.length) {
    return fail('Waypoint identity and route-index state do not match. Reload before route editing.');
  }
  if (draft.waypointRows.some((row, index) => row.placeId !== draft.waypointPlaceIds[index] || row.routeVertexIndex !== draft.waypointRouteVertexIndices[index])) {
    return fail('Waypoint route state is stale. Reload before route editing.');
  }
  if (draft.waypointRows.length > 0 && (!draft.fromPlaceId || !draft.toPlaceId)) return fail('Waypoint routes require From and To Places.');

  const specifications = [
    ...(draft.fromPlaceId ? [{ role: 'from' as const, key: 'from', placeId: draft.fromPlaceId, waypointClientId: null }] : []),
    ...draft.waypointRows.map(row => ({ role: 'waypoint' as const, key: `waypoint:${row.clientId}`, placeId: row.placeId, waypointClientId: row.clientId })),
    ...(draft.toPlaceId ? [{ role: 'to' as const, key: 'to', placeId: draft.toPlaceId, waypointClientId: null }] : [])
  ];
  const nodes: SegmentRouteAnchorNode[] = [];
  for (const specification of specifications) {
    const place = state.placesById[specification.placeId];
    if (!place?.location || !isCoordinate([place.location.longitude, place.location.latitude])) return fail('A route anchor is missing its saved Place location.');
    nodes.push({ kind: 'anchor', ...specification, placeName: place.name, coordinate: [place.location.longitude, place.location.latitude] });
  }
  return { ok: true, nodes };
}

function validCoordinates(route: GeoJsonLineString): RouteCoordinate[] | null {
  if (route.type !== 'LineString' || route.coordinates.length < 2 || !route.coordinates.every(isCoordinate)) return null;
  return route.coordinates.map(coordinate => [...coordinate]);
}

function isCoordinate(coordinate: RouteCoordinate): boolean {
  return Number.isFinite(coordinate[0]) && coordinate[0] >= -180 && coordinate[0] <= 180
    && Number.isFinite(coordinate[1]) && coordinate[1] >= -90 && coordinate[1] <= 90;
}

function strictlyIncreasingInterior(indices: number[], pointCount: number): boolean {
  return indices.every((index, position) => Number.isInteger(index) && index > 0 && index < pointCount - 1
    && (position === 0 || index > indices[position - 1]));
}

function sameCoordinate(left: RouteCoordinate, right: RouteCoordinate): boolean {
  return Math.abs(left[0] - right[0]) <= coordinateTolerance && Math.abs(left[1] - right[1]) <= coordinateTolerance;
}

function anonymous(id: number, coordinate: RouteCoordinate): SegmentRouteAnonymousNode {
  return { kind: 'anonymous', key: `anonymous:${id}`, coordinate: [...coordinate] };
}

function markCustom(state: SegmentRouteWorkState): void {
  state.changedCustom = true;
  state.cleared = false;
}

function success(nodes: SegmentRouteWorkNode[], origin: 'custom' | 'fallback'): SegmentRouteWorkResult {
  return { ok: true, state: { nodes, origin, changedCustom: false, cleared: false, nextAnonymousId: 1 } };
}

function fail(message: string): { ok: false; message: string } {
  return { ok: false, message };
}
