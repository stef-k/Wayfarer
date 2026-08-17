/** Semantic role supplied by the authoritative ordered Segment aggregate. */
export type SegmentAnchorRole = 'start' | 'via' | 'end';

/** Framework-neutral input used to derive transient route presentation. */
export type SegmentAnchorInput = {
  position: number;
  placeId: string | null;
  name: string | null;
  role: SegmentAnchorRole;
  location: readonly [number, number] | null;
  routeVertexIndex?: number | null;
};

/** A positional anchor whose label and text are recalculated for this resolution. */
export type ResolvedSegmentAnchor = SegmentAnchorInput & {
  label: string;
  roleText: string;
  displayName: string;
};

/** One marker-adjacent badge, coalesced by canonical Place identity. */
export type ResolvedSegmentBadge = {
  placeId: string;
  location: readonly [number, number];
  label: string;
};

export type ResolvedSegmentAnchors = {
  anchors: ResolvedSegmentAnchor[];
  badges: ResolvedSegmentBadge[];
  compactTrail: string;
  accessibleName: string;
};

export type SegmentOrientation = 'forward' | 'reversed' | 'ambiguous';

export type ProjectedPoint = readonly [number, number];

export type ProjectedChevron = { x: number; y: number; angle: number };

export type PresentationRectangle = { left: number; top: number; right: number; bottom: number };
export type RouteBadgePlacement = { left: number; top: number; width: number; height: number; offsetIndex: number; fallback: boolean };

const routeBadgeOffsets: readonly [number, number][] = [
  [10, -18], [-34, -18], [10, -48], [-34, -48], [18, -34], [-42, -34]
];

/** Chooses the first bounded route-badge position clear of controls and prior active badges. */
export const placeRouteBadge = (
  anchor: ProjectedPoint,
  size: Readonly<{ width: number; height: number }>,
  mapBounds: PresentationRectangle,
  controlBounds: readonly PresentationRectangle[],
  placedBounds: readonly PresentationRectangle[]
): RouteBadgePlacement => {
  const candidate = (offset: readonly [number, number], offsetIndex: number, fallback = false): RouteBadgePlacement => ({
    left: anchor[0] + offset[0], top: anchor[1] + offset[1], width: size.width, height: size.height, offsetIndex, fallback
  });
  const clear = (placement: RouteBadgePlacement): boolean => {
    const rectangle = withEdges(placement);
    return rectangle.left >= mapBounds.left && rectangle.top >= mapBounds.top
      && rectangle.right <= mapBounds.right && rectangle.bottom <= mapBounds.bottom
      && ![...controlBounds, ...placedBounds].some(blocker => intersects(rectangle, blocker));
  };
  for (let index = 0; index < routeBadgeOffsets.length; index += 1) {
    const placement = candidate(routeBadgeOffsets[index], index);
    if (clear(placement)) return placement;
  }
  return candidate(routeBadgeOffsets[0], -1, true);
};

/** Places one combined no-clear pill at a clear affected anchor or clamps it to the map surface. */
export const placeCombinedRouteBadge = (
  anchors: readonly ProjectedPoint[],
  size: Readonly<{ width: number; height: number }>,
  mapBounds: PresentationRectangle,
  controlBounds: readonly PresentationRectangle[],
  placedBounds: readonly PresentationRectangle[]
): RouteBadgePlacement => {
  for (const anchor of anchors) {
    const placement = placeRouteBadge(anchor, size, mapBounds, controlBounds, placedBounds);
    if (!placement.fallback) return { ...placement, fallback: true };
  }
  const preferred = placeRouteBadge(anchors[0], size, mapBounds, controlBounds, placedBounds);
  return {
    ...preferred,
    left: Math.max(mapBounds.left, Math.min(preferred.left, mapBounds.right - size.width)),
    top: Math.max(mapBounds.top, Math.min(preferred.top, mapBounds.bottom - size.height)),
    fallback: true
  };
};

const withEdges = (rectangle: Readonly<{ left: number; top: number; width: number; height: number }>): PresentationRectangle => ({
  left: rectangle.left, top: rectangle.top, right: rectangle.left + rectangle.width, bottom: rectangle.top + rectangle.height
});
const intersects = (left: PresentationRectangle, right: PresentationRectangle): boolean => left.left < right.right
  && left.right > right.left && left.top < right.bottom && left.bottom > right.top;

/** Reverses custom geometry and remaps existing waypoint indices in one unsaved draft operation. */
export function reverseSegmentDraftRoute(draft: EditorSegmentDraft): boolean {
  if (!draft.route || draft.route.coordinates.length < 2) return false;
  const pointCount = draft.route.coordinates.length;
  draft.route = { type: 'LineString', coordinates: [...draft.route.coordinates].reverse().map(point => [...point]) };
  draft.waypointRouteVertexIndices = draft.waypointRouteVertexIndices.map(index => index === null ? null : pointCount - 1 - index);
  draft.waypointRows.forEach((row, index) => { row.routeVertexIndex = draft.waypointRouteVertexIndices[index]; });
  return true;
}

/** Converts a zero-based anchor position to locale-independent ASCII bijective base-26. */
export function alphabeticAnchorLabel(position: number): string {
  if (!Number.isSafeInteger(position) || position < 0) {
    throw new TypeError('Anchor position must be a non-negative integer.');
  }

  let remaining = position + 1;
  let label = '';
  while (remaining > 0) {
    remaining -= 1;
    label = String.fromCharCode(65 + (remaining % 26)) + label;
    remaining = Math.floor(remaining / 26);
  }
  return label;
}

/** Derives fresh labels, role text, trails, and canonical-Place badge projection. */
export function resolveSegmentAnchors(inputs: readonly SegmentAnchorInput[]): ResolvedSegmentAnchors {
  const seenPositions = new Set<number>();
  let viaNumber = 0;
  const anchors = inputs.map((input, index): ResolvedSegmentAnchor => {
    if (!Number.isSafeInteger(input.position) || input.position < 0 || seenPositions.has(input.position)) {
      throw new TypeError('Anchor positions must be unique non-negative integers.');
    }
    if (input.position !== index) {
      throw new TypeError('Anchor positions must be complete and ordered from zero.');
    }
    seenPositions.add(input.position);
    if (input.role === 'via') viaNumber += 1;
    return {
      ...input,
      label: alphabeticAnchorLabel(input.position),
      roleText: input.role === 'start' ? 'Start' : input.role === 'end' ? 'End' : `Via ${viaNumber}`,
      displayName: resolvedAnchorName(input)
    };
  });

  const badgeByPlace = new Map<string, ResolvedSegmentBadge>();
  anchors.forEach(anchor => {
    if (!anchor.placeId || !anchor.location) return;
    const existing = badgeByPlace.get(anchor.placeId);
    if (existing) {
      existing.label = `${existing.label}/${anchor.label}`;
      return;
    }
    badgeByPlace.set(anchor.placeId, { placeId: anchor.placeId, location: anchor.location, label: anchor.label });
  });

  const compactTrail = anchors.map(anchor => `${anchor.label} ${anchor.displayName}`).join(' → ');
  return {
    anchors,
    badges: [...badgeByPlace.values()],
    compactTrail,
    accessibleName: accessibleJourneyName(anchors)
  };
}

/** Classifies loaded custom geometry against semantic anchors without mutating either input. */
export function classifySegmentOrientation(
  anchors: readonly SegmentAnchorInput[],
  coordinates: readonly (readonly [number, number])[],
  hasWaypoints: boolean
): SegmentOrientation {
  if (anchors.length < 2 || coordinates.length < 2 || anchors.some(anchor => !anchor.location)) {
    return 'ambiguous';
  }
  if (hasWaypoints) {
    const waypointAnchors = anchors.filter(anchor => anchor.role === 'via');
    const indices = waypointAnchors.map(anchor => anchor.routeVertexIndex);
    if (indices.some(index => !Number.isSafeInteger(index) || (index as number) <= 0 || (index as number) >= coordinates.length - 1)) {
      return 'ambiguous';
    }
    const numeric = indices as number[];
    const matches = waypointAnchors.every((anchor, index) => coordinatesMatch(coordinates[numeric[index]], anchor.location!));
    if (!matches) return 'ambiguous';
    const forward = coordinatesMatch(coordinates[0], anchors[0].location!)
      && coordinatesMatch(coordinates.at(-1)!, anchors.at(-1)!.location!)
      && numeric.every((value, index) => index === 0 || value > numeric[index - 1]);
    const reversed = coordinatesMatch(coordinates[0], anchors.at(-1)!.location!)
      && coordinatesMatch(coordinates.at(-1)!, anchors[0].location!)
      && numeric.every((value, index) => index === 0 || value < numeric[index - 1]);
    if (forward && anchors[0].placeId === anchors.at(-1)!.placeId) return 'forward';
    return forward === reversed ? 'ambiguous' : forward ? 'forward' : 'reversed';
  }

  const start = anchors[0].location!;
  const end = anchors.at(-1)!.location!;
  const routeStart = coordinates[0];
  const routeEnd = coordinates.at(-1)!;
  const forward = haversineKm(routeStart, start) <= 0.25 && haversineKm(routeEnd, end) <= 0.25;
  const reversed = haversineKm(routeStart, end) <= 0.25 && haversineKm(routeEnd, start) <= 0.25;
  if (forward && anchors[0].placeId === anchors.at(-1)!.placeId) return 'forward';
  return forward === reversed ? 'ambiguous' : forward ? 'forward' : 'reversed';
}

/** Places static chevrons along a route measured in already-projected CSS pixels. */
export function placeProjectedChevrons(points: readonly ProjectedPoint[], active: boolean): ProjectedChevron[] {
  const metrics = projectedMetrics(points);
  if (!metrics || metrics.length < 24) return [];
  if (metrics.length < 48) {
    return active ? sampleChevron(metrics, metrics.length / 2) : [];
  }
  const spacing = active ? 72 : 120;
  const cap = active ? 8 : 4;
  const count = Math.min(cap, Math.max(1, Math.floor((metrics.length - 48) / spacing) + 1));
  const interval = metrics.length - 48;
  return Array.from({ length: count }, (_, index) => 24 + ((index + 1) * interval) / (count + 1))
    .flatMap(distance => sampleChevron(metrics, distance));
}

type ProjectedMetrics = { points: readonly ProjectedPoint[]; cumulative: number[]; length: number };

/** Measures a finite projected polyline once for deterministic cue sampling. */
function projectedMetrics(points: readonly ProjectedPoint[]): ProjectedMetrics | null {
  if (points.length < 2 || points.some(([x, y]) => !Number.isFinite(x) || !Number.isFinite(y))) return null;
  const cumulative = [0];
  for (let index = 1; index < points.length; index += 1) {
    cumulative.push(cumulative[index - 1] + Math.hypot(points[index][0] - points[index - 1][0], points[index][1] - points[index - 1][1]));
  }
  return { points, cumulative, length: cumulative.at(-1)! };
}

/** Samples one position and its local six-pixel tangent, omitting degenerate cues. */
function sampleChevron(metrics: ProjectedMetrics, distance: number): ProjectedChevron[] {
  const point = interpolateProjected(metrics, distance);
  const before = interpolateProjected(metrics, Math.max(0, distance - 6));
  const after = interpolateProjected(metrics, Math.min(metrics.length, distance + 6));
  const dx = after[0] - before[0];
  const dy = after[1] - before[1];
  if (!Number.isFinite(dx) || !Number.isFinite(dy) || Math.hypot(dx, dy) < 4) return [];
  return [{ x: point[0], y: point[1], angle: Math.atan2(dy, dx) * 180 / Math.PI }];
}

/** Interpolates a point at a cumulative projected distance. */
function interpolateProjected(metrics: ProjectedMetrics, distance: number): ProjectedPoint {
  for (let index = 1; index < metrics.cumulative.length; index += 1) {
    if (distance > metrics.cumulative[index]) continue;
    const legStart = metrics.cumulative[index - 1];
    const legLength = metrics.cumulative[index] - legStart;
    const ratio = legLength === 0 ? 0 : (distance - legStart) / legLength;
    return [
      metrics.points[index - 1][0] + (metrics.points[index][0] - metrics.points[index - 1][0]) * ratio,
      metrics.points[index - 1][1] + (metrics.points[index][1] - metrics.points[index - 1][1]) * ratio
    ];
  }
  return metrics.points.at(-1)!;
}

/** Applies the merged #388 per-axis tolerance. */
function coordinatesMatch(left: readonly [number, number], right: readonly [number, number]): boolean {
  return Math.abs(left[0] - right[0]) <= 0.0000001 && Math.abs(left[1] - right[1]) <= 0.0000001;
}

/** Measures legacy endpoint proximity in kilometres. */
function haversineKm(left: readonly [number, number], right: readonly [number, number]): number {
  const radians = (degrees: number): number => degrees * Math.PI / 180;
  const latitudeDelta = radians(right[1] - left[1]);
  const longitudeDelta = radians(right[0] - left[0]);
  const a = Math.sin(latitudeDelta / 2) ** 2
    + Math.cos(radians(left[1])) * Math.cos(radians(right[1])) * Math.sin(longitudeDelta / 2) ** 2;
  return 6371.0088 * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
}

/** Supplies role-specific language without implying a valid linked Place. */
function resolvedAnchorName(anchor: SegmentAnchorInput): string {
  const name = anchor.name?.trim();
  if (name) return name;
  if (anchor.role === 'start') return 'Unlinked start';
  if (anchor.role === 'end') return 'Unlinked end';
  return 'Unnamed waypoint';
}

/** Produces the keyboard-authoritative Segment name from the derived semantic order. */
function accessibleJourneyName(anchors: readonly ResolvedSegmentAnchor[]): string {
  const start = anchors.find(anchor => anchor.role === 'start')?.displayName ?? 'Unlinked start';
  const end = [...anchors].reverse().find(anchor => anchor.role === 'end')?.displayName ?? 'Unlinked end';
  const vias = anchors.filter(anchor => anchor.role === 'via').map(anchor => anchor.displayName);
  return `Segment from ${start}${vias.length ? ` via ${vias.join(', then ')}` : ''} to ${end}`;
}
import type { EditorSegmentDraft } from '../types';
