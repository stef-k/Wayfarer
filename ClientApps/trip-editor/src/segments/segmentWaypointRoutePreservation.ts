import type { EditorCoordinate, GeoJsonLineString, Guid } from '../types';

const coordinateTolerance = 0.0000001;
const maximumRouteCoordinates = 1_000;
const antipodalTolerance = 1e-12;

type RouteChangeInput = {
  fromPlaceId: Guid | null;
  toPlaceId: Guid | null;
  proposedFromPlaceId?: Guid | null;
  proposedToPlaceId?: Guid | null;
  waypointPlaceIds: Guid[];
  waypointRouteVertexIndices: Array<number | null>;
  proposedWaypointPlaceIds: Guid[];
  route: GeoJsonLineString | null;
  placeLocations: Record<Guid, EditorCoordinate | null | undefined>;
};

type PreservedRouteChange = {
  kind: 'addition' | 'removal';
  route: GeoJsonLineString;
  waypointRouteVertexIndices: number[];
  addedPlaceId?: Guid;
  reusedExistingVertex?: boolean;
};

type UnsafeRouteChange = { kind: 'unsafe'; reason: string };

export type WaypointRouteChangeResult = PreservedRouteChange | UnsafeRouteChange;

/**
 * Classifies and projects one waypoint collection edit without inventing route geometry.
 * It owns only validation, bounded spherical leg choice, coordinate insertion, and index preservation.
 */
export function preserveWaypointRouteChange(input: RouteChangeInput): WaypointRouteChangeResult {
  const validated = validateMapping(input);
  if (!validated) return unsafe('The current custom route mapping is invalid.');
  if ((input.proposedFromPlaceId !== undefined && input.proposedFromPlaceId !== input.fromPlaceId)
    || (input.proposedToPlaceId !== undefined && input.proposedToPlaceId !== input.toPlaceId)) {
    return unsafe('Segment endpoints changed.');
  }
  if (hasDuplicates(input.proposedWaypointPlaceIds)) return unsafe('Waypoint identities are ambiguous.');

  const retainedPositions = subsequencePositions(input.waypointPlaceIds, input.proposedWaypointPlaceIds);
  const added = input.proposedWaypointPlaceIds.filter(id => !input.waypointPlaceIds.includes(id));
  if (added.length === 0 && retainedPositions) {
    if (input.proposedWaypointPlaceIds.length >= input.waypointPlaceIds.length) return unsafe('The waypoint collection did not change.');
    return {
      kind: 'removal',
      route: cloneRoute(validated.route),
      waypointRouteVertexIndices: retainedPositions.map(index => validated.indices[index])
    };
  }
  if (added.length !== 1 || input.proposedWaypointPlaceIds.length !== input.waypointPlaceIds.length + 1
    || !isSubsequence(input.waypointPlaceIds, input.proposedWaypointPlaceIds)) {
    return unsafe('The proposal is not one unambiguous order-preserving addition.');
  }

  const addedPosition = input.proposedWaypointPlaceIds.indexOf(added[0]);
  const coordinate = coordinateFor(input.placeLocations, added[0]);
  if (!coordinate) return unsafe('The added Place has no valid saved coordinate.');
  const lowerIndex = addedPosition === 0 ? 0 : validated.indices[addedPosition - 1];
  const upperIndex = addedPosition === validated.indices.length ? validated.route.coordinates.length - 1 : validated.indices[addedPosition];
  if (lowerIndex >= upperIndex) return unsafe('The semantic insertion interval is invalid.');

  const occupied = new Set([0, validated.route.coordinates.length - 1, ...validated.indices]);
  const exactIndex = validated.route.coordinates.findIndex((candidate, index) =>
    index > lowerIndex && index < upperIndex && !occupied.has(index) && coordinatesMatch(candidate, coordinate));
  if (exactIndex >= 0) {
    return additionResult(validated.route, validated.indices, added[0], addedPosition, exactIndex, false);
  }
  if (validated.route.coordinates.length >= maximumRouteCoordinates) return unsafe('The custom route has no capacity for another coordinate.');

  const legStart = nearestLegStart(coordinate, validated.route.coordinates, lowerIndex, upperIndex);
  if (legStart === null) return unsafe('The permitted route interval has non-unique or invalid geometry.');
  const insertedIndex = legStart + 1;
  const coordinates = validated.route.coordinates.map(item => [...item] as [number, number]);
  coordinates.splice(insertedIndex, 0, coordinate);
  return additionResult({ type: validated.route.type, coordinates }, validated.indices, added[0], addedPosition, insertedIndex, true);
}

function validateMapping(input: RouteChangeInput): { route: GeoJsonLineString; indices: number[] } | null {
  const route = input.route;
  if (!route || route.type !== 'LineString' || route.coordinates.length < 2 || route.coordinates.length > maximumRouteCoordinates
    || !input.fromPlaceId || !input.toPlaceId || input.waypointPlaceIds.length !== input.waypointRouteVertexIndices.length
    || hasDuplicates(input.waypointPlaceIds) || route.coordinates.some(coordinate => !validCoordinate(coordinate))) return null;
  const from = coordinateFor(input.placeLocations, input.fromPlaceId);
  const to = coordinateFor(input.placeLocations, input.toPlaceId);
  if (!from || !to || !coordinatesMatch(route.coordinates[0], from) || !coordinatesMatch(route.coordinates.at(-1)!, to)) return null;
  if (input.waypointRouteVertexIndices.some(index => !Number.isInteger(index))) return null;
  const indices = input.waypointRouteVertexIndices as number[];
  if (indices.some((index, position) => index <= 0 || index >= route.coordinates.length - 1
    || (position > 0 && index <= indices[position - 1])
    || !coordinatesMatch(route.coordinates[index], coordinateFor(input.placeLocations, input.waypointPlaceIds[position]) ?? [Number.NaN, Number.NaN]))) return null;
  return { route, indices };
}

function additionResult(route: GeoJsonLineString, indices: number[], addedPlaceId: Guid, addedPosition: number,
  addedIndex: number, inserted: boolean): WaypointRouteChangeResult {
  const remapped = indices.map(index => inserted && addedIndex <= index ? index + 1 : index);
  remapped.splice(addedPosition, 0, addedIndex);
  if (remapped.some((index, position) => position > 0 && index <= remapped[position - 1])) return unsafe('The inserted mapping is not strictly increasing.');
  return { kind: 'addition', route: cloneRoute(route), waypointRouteVertexIndices: remapped, addedPlaceId, reusedExistingVertex: !inserted };
}

function nearestLegStart(point: [number, number], coordinates: [number, number][], lower: number, upper: number): number | null {
  let selected: number | null = null;
  let shortest = Number.POSITIVE_INFINITY;
  for (let index = lower; index < upper; index += 1) {
    const distance = sphericalPointToSegmentDistance(point, coordinates[index], coordinates[index + 1]);
    if (distance === null) return null;
    if (distance < shortest) {
      shortest = distance;
      selected = index;
    }
  }
  return selected;
}

/** Returns angular point-to-minor-great-circle-segment distance, or null for non-unique cases. */
function sphericalPointToSegmentDistance(point: [number, number], start: [number, number], end: [number, number]): number | null {
  if (coordinatesMatch(start, end)) return angularDistance(point, start);
  const segmentDistance = angularDistance(start, end);
  if (!Number.isFinite(segmentDistance) || Math.abs(Math.PI - segmentDistance) <= antipodalTolerance) return null;
  const pointDistance = angularDistance(start, point);
  if (pointDistance === 0) return 0;
  const segmentBearing = initialBearing(start, end);
  const pointBearing = initialBearing(start, point);
  if (![segmentBearing, pointBearing].every(Number.isFinite)) return null;
  const bearingDelta = normalizeRadians(pointBearing - segmentBearing);
  const crossTrack = Math.asin(clamp(Math.sin(pointDistance) * Math.sin(bearingDelta), -1, 1));
  const alongTrack = Math.atan2(Math.sin(pointDistance) * Math.cos(bearingDelta), Math.cos(pointDistance));
  if (![crossTrack, alongTrack].every(Number.isFinite)) return null;
  return alongTrack >= 0 && alongTrack <= segmentDistance
    ? Math.abs(crossTrack)
    : Math.min(angularDistance(point, start), angularDistance(point, end));
}

function angularDistance(first: [number, number], second: [number, number]): number {
  const latitude1 = radians(first[1]);
  const latitude2 = radians(second[1]);
  const latitudeDelta = latitude2 - latitude1;
  const longitudeDelta = normalizeRadians(radians(second[0] - first[0]));
  const haversine = Math.sin(latitudeDelta / 2) ** 2
    + Math.cos(latitude1) * Math.cos(latitude2) * Math.sin(longitudeDelta / 2) ** 2;
  return 2 * Math.atan2(Math.sqrt(clamp(haversine, 0, 1)), Math.sqrt(Math.max(0, 1 - haversine)));
}

function initialBearing(first: [number, number], second: [number, number]): number {
  const latitude1 = radians(first[1]);
  const latitude2 = radians(second[1]);
  const longitudeDelta = normalizeRadians(radians(second[0] - first[0]));
  return Math.atan2(Math.sin(longitudeDelta) * Math.cos(latitude2),
    Math.cos(latitude1) * Math.sin(latitude2) - Math.sin(latitude1) * Math.cos(latitude2) * Math.cos(longitudeDelta));
}

function coordinateFor(locations: RouteChangeInput['placeLocations'], id: Guid): [number, number] | null {
  const value = locations[id];
  const coordinate: [number, number] = [value?.longitude ?? Number.NaN, value?.latitude ?? Number.NaN];
  return validCoordinate(coordinate) ? coordinate : null;
}

function validCoordinate(value: readonly number[]): value is [number, number] {
  return value.length === 2 && Number.isFinite(value[0]) && Number.isFinite(value[1])
    && value[0] >= -180 && value[0] <= 180 && value[1] >= -90 && value[1] <= 90;
}

function coordinatesMatch(left: readonly [number, number], right: readonly [number, number]): boolean {
  return Math.abs(left[0] - right[0]) <= coordinateTolerance && Math.abs(left[1] - right[1]) <= coordinateTolerance;
}

function subsequencePositions(needles: Guid[], values: Guid[]): number[] | null {
  const positions: number[] = [];
  let searchFrom = 0;
  for (const value of values) {
    const index = needles.indexOf(value, searchFrom);
    if (index < 0) return null;
    positions.push(index);
    searchFrom = index + 1;
  }
  return positions;
}

function isSubsequence(needles: Guid[], values: Guid[]): boolean {
  return subsequencePositions(values, needles) !== null;
}

function hasDuplicates(values: Guid[]): boolean { return new Set(values).size !== values.length; }
function cloneRoute(route: GeoJsonLineString): GeoJsonLineString { return { type: route.type, coordinates: route.coordinates.map(item => [...item]) }; }
function radians(degrees: number): number { return degrees * Math.PI / 180; }
function normalizeRadians(value: number): number { return Math.atan2(Math.sin(value), Math.cos(value)); }
function clamp(value: number, minimum: number, maximum: number): number { return Math.min(maximum, Math.max(minimum, value)); }
function unsafe(reason: string): UnsafeRouteChange { return { kind: 'unsafe', reason }; }
