import assert from 'node:assert/strict';
import test from 'node:test';
import { preserveWaypointRouteChange } from '../../ClientApps/trip-editor/src/segments/segmentWaypointRoutePreservation.ts';

const locations = {
  a: { longitude: 0, latitude: 0 },
  b: { longitude: 5, latitude: 2 },
  c: { longitude: 10, latitude: 0 },
  d: { longitude: 20, latitude: 0 }
};

const proposal = (overrides = {}) => ({
  fromPlaceId: 'a',
  toPlaceId: 'd',
  waypointPlaceIds: ['c'],
  waypointRouteVertexIndices: [3],
  route: { type: 'LineString', coordinates: [[0, 0], [2, 0], [6, 0], [10, 0], [20, 0]] },
  proposedWaypointPlaceIds: ['b', 'c'],
  placeLocations: locations,
  ...overrides
});

test('adds B to custom A to C geometry while preserving every original coordinate', () => {
  const input = proposal({
    toPlaceId: 'c', waypointPlaceIds: [], waypointRouteVertexIndices: [],
    route: { type: 'LineString', coordinates: [[0, 0], [3, 0], [10, 0]] },
    proposedWaypointPlaceIds: ['b']
  });
  const result = preserveWaypointRouteChange(input);
  assert.equal(result.kind, 'addition');
  assert.deepEqual(result.route.coordinates, [[0, 0], [3, 0], [5, 2], [10, 0]]);
  assert.deepEqual(result.waypointRouteVertexIndices, [2]);
});

test('restricts nearest-leg selection to B semantic interval', () => {
  const result = preserveWaypointRouteChange(proposal({
    waypointPlaceIds: ['c'], waypointRouteVertexIndices: [2],
    route: { type: 'LineString', coordinates: [[0, 0], [5, 8], [10, 0], [5, 2.01], [20, 0]] }
  }));
  assert.equal(result.kind, 'addition');
  assert.deepEqual(result.route.coordinates, [[0, 0], [5, 2], [5, 8], [10, 0], [5, 2.01], [20, 0]]);
  assert.deepEqual(result.waypointRouteVertexIndices, [1, 3]);
});

test('pure removal preserves geometry and surviving numeric indices', () => {
  const route = { type: 'LineString', coordinates: [[0, 0], [5, 2], [10, 0], [20, 0]] };
  const result = preserveWaypointRouteChange(proposal({
    waypointPlaceIds: ['b', 'c'], waypointRouteVertexIndices: [1, 2], route,
    proposedWaypointPlaceIds: ['c']
  }));
  assert.equal(result.kind, 'removal');
  assert.deepEqual(result.route, route);
  assert.deepEqual(result.waypointRouteVertexIndices, [2]);
});

test('reuses the lowest eligible exact anonymous coordinate', () => {
  const result = preserveWaypointRouteChange(proposal({
    waypointPlaceIds: ['c'], waypointRouteVertexIndices: [4],
    route: { type: 'LineString', coordinates: [[0, 0], [5, 2], [5, 2], [7, 0], [10, 0], [20, 0]] }
  }));
  assert.equal(result.kind, 'addition');
  assert.equal(result.reusedExistingVertex, true);
  assert.deepEqual(result.waypointRouteVertexIndices, [1, 4]);
  assert.equal(result.route.coordinates.length, 6);
});

test('increments only indices strictly after an actual insertion point', () => {
  const result = preserveWaypointRouteChange(proposal());
  assert.equal(result.kind, 'addition');
  assert.deepEqual(result.waypointRouteVertexIndices, [3, 4]);
});

test('fails conservatively for malformed and ambiguous mappings', () => {
  const malformed = preserveWaypointRouteChange(proposal({ waypointRouteVertexIndices: [99] }));
  const ambiguous = preserveWaypointRouteChange(proposal({ proposedWaypointPlaceIds: ['b', 'b', 'c'] }));
  assert.equal(malformed.kind, 'unsafe');
  assert.equal(ambiguous.kind, 'unsafe');
});

test('classifies before, between, after, and zero-waypoint insertions', () => {
  const cases = [
    proposal({ proposedWaypointPlaceIds: ['b', 'c'] }),
    proposal({ waypointPlaceIds: ['b'], waypointRouteVertexIndices: [2], proposedWaypointPlaceIds: ['b', 'c'] }),
    proposal({ waypointPlaceIds: [], waypointRouteVertexIndices: [], proposedWaypointPlaceIds: ['b'] })
  ];
  assert.deepEqual(cases.map(item => preserveWaypointRouteChange(item).kind), ['addition', 'addition', 'addition']);
});

test('rejects reorder, substitution, endpoint replacement, and ambiguous batches', () => {
  const base = proposal({ waypointPlaceIds: ['b', 'c'], waypointRouteVertexIndices: [1, 3] });
  const results = [
    preserveWaypointRouteChange({ ...base, proposedWaypointPlaceIds: ['c', 'b'] }),
    preserveWaypointRouteChange({ ...base, proposedWaypointPlaceIds: ['b', 'x'] }),
    preserveWaypointRouteChange({ ...base, proposedFromPlaceId: 'x' }),
    preserveWaypointRouteChange({ ...base, proposedWaypointPlaceIds: ['x', 'b', 'c', 'y'] })
  ];
  assert.ok(results.every(result => result.kind === 'unsafe'));
});

test('uses the lowest eligible leg for equal distance and handles zero-length legs', () => {
  const result = preserveWaypointRouteChange(proposal({
    toPlaceId: 'c', waypointPlaceIds: [], waypointRouteVertexIndices: [], proposedWaypointPlaceIds: ['b'],
    route: { type: 'LineString', coordinates: [[0, 0], [0, 0], [10, 0]] },
    placeLocations: { ...locations, b: { longitude: 0, latitude: 1 } }
  }));
  assert.equal(result.kind, 'addition');
  assert.deepEqual(result.route.coordinates.slice(0, 3), [[0, 0], [0, 1], [0, 0]]);
});

test('handles dateline, high-latitude, long, closed, repeated, self-intersecting, and far routes deterministically', () => {
  const routes = [
    [[179, 80], [-179, 80]],
    [[-170, 0], [0, 70], [170, 0]],
    [[0, 0], [10, 10], [0, 10], [10, 0]],
    [[0, 0], [5, 0], [0, 0]],
    [[0, 0], [0, 0], [10, 0]]
  ];
  for (const coordinates of routes) {
    const from = coordinates[0];
    const to = coordinates.at(-1);
    const result = preserveWaypointRouteChange(proposal({
      toPlaceId: 'z', waypointPlaceIds: [], waypointRouteVertexIndices: [], proposedWaypointPlaceIds: ['b'],
      route: { type: 'LineString', coordinates },
      placeLocations: { a: { longitude: from[0], latitude: from[1] }, b: { longitude: 40, latitude: 40 }, z: { longitude: to[0], latitude: to[1] } }
    }));
    assert.equal(result.kind, 'addition');
    assert.equal(result.route.coordinates.length, coordinates.length + 1);
  }
});

test('rejects an antipodal non-unique leg conservatively', () => {
  const result = preserveWaypointRouteChange(proposal({
    toPlaceId: 'z', waypointPlaceIds: [], waypointRouteVertexIndices: [], proposedWaypointPlaceIds: ['b'],
    route: { type: 'LineString', coordinates: [[0, 0], [180, 0]] },
    placeLocations: { a: locations.a, b: locations.b, z: { longitude: 180, latitude: 0 } }
  }));
  assert.equal(result.kind, 'unsafe');
});

test('does not reuse occupied endpoint or waypoint anchors', () => {
  const endpoint = preserveWaypointRouteChange(proposal({
    toPlaceId: 'c', waypointPlaceIds: [], waypointRouteVertexIndices: [], proposedWaypointPlaceIds: ['b'],
    route: { type: 'LineString', coordinates: [[0, 0], [10, 0]] },
    placeLocations: { ...locations, b: locations.a }
  }));
  const occupied = preserveWaypointRouteChange(proposal({
    placeLocations: { ...locations, b: locations.c }
  }));
  assert.equal(endpoint.kind, 'addition');
  assert.equal(endpoint.reusedExistingVertex, false);
  assert.equal(occupied.kind, 'addition');
  assert.equal(occupied.reusedExistingVertex, false);
});

test('supports sequential add, removal, and exact anonymous re-add without deduplication', () => {
  const first = preserveWaypointRouteChange(proposal({
    toPlaceId: 'c', waypointPlaceIds: [], waypointRouteVertexIndices: [], proposedWaypointPlaceIds: ['b'],
    route: { type: 'LineString', coordinates: [[0, 0], [10, 0]] }
  }));
  const removed = preserveWaypointRouteChange({
    ...proposal(), toPlaceId: 'c', waypointPlaceIds: ['b'], waypointRouteVertexIndices: first.waypointRouteVertexIndices,
    route: first.route, proposedWaypointPlaceIds: []
  });
  const readded = preserveWaypointRouteChange({
    ...proposal(), toPlaceId: 'c', waypointPlaceIds: [], waypointRouteVertexIndices: [], route: removed.route,
    proposedWaypointPlaceIds: ['b']
  });
  assert.equal(removed.kind, 'removal');
  assert.deepEqual(removed.route, first.route);
  assert.equal(readded.reusedExistingVertex, true);
  assert.deepEqual(readded.route, first.route);
});
