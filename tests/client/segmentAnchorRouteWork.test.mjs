import assert from 'node:assert/strict';
import test from 'node:test';
import {
  clearAnonymousNodes,
  cloneSegmentRouteWorkState,
  constructSegmentRouteWorkState,
  insertAnonymousNode,
  moveAnonymousNode,
  projectSegmentRouteWork,
  removeAnonymousNode
} from '../../ClientApps/trip-editor/src/components/segmentRouteWorkState.ts';
import { beginSegmentRouteMapWork } from '../../ClientApps/trip-editor/src/components/segmentRouteMapWork.ts';

const places = {
  a: { id: 'a', name: 'Alpha', location: { longitude: 10, latitude: 20 } },
  b: { id: 'b', name: 'Bravo', location: { longitude: 11, latitude: 21 } },
  c: { id: 'c', name: 'Charlie', location: { longitude: 12, latitude: 22 } },
  d: { id: 'd', name: 'Delta', location: { longitude: 13, latitude: 23 } }
};
const editorState = { placesById: places };

const draft = ({ route = null, from = 'a', to = 'c', waypoints = [['via-b', 'b', null]] } = {}) => ({
  fromPlaceId: from,
  toPlaceId: to,
  waypointPlaceIds: waypoints.map(([, placeId]) => placeId),
  waypointRouteVertexIndices: waypoints.map(([, , index]) => index),
  waypointRows: waypoints.map(([clientId, placeId, routeVertexIndex]) => ({ clientId, placeId, routeVertexIndex })),
  route
});

const construct = value => {
  const result = constructSegmentRouteWorkState(value, editorState);
  assert.equal(result.ok, true, result.message);
  return result.state;
};

test('constructs custom work with stable anchors and editable anonymous vertices', () => {
  const state = construct(draft({
    route: { type: 'LineString', coordinates: [[10, 20], [10.5, 20.5], [11, 21], [12, 22]] },
    waypoints: [['via-b', 'b', 2]]
  }));
  assert.deepEqual(state.nodes.map(node => [node.kind, node.key]), [
    ['anchor', 'from'], ['anonymous', 'anonymous:1'], ['anchor', 'waypoint:via-b'], ['anchor', 'to']
  ]);
  assert.deepEqual(projectSegmentRouteWork(state)?.waypointRouteVertexIndices, [2]);
});

test('constructs fallback work and unchanged Done preserves null custom state', () => {
  const state = construct(draft());
  assert.deepEqual(state.nodes.map(node => node.key), ['from', 'waypoint:via-b', 'to']);
  assert.deepEqual(projectSegmentRouteWork(state), {
    route: null,
    waypointRouteVertexIndices: [null],
    unchangedFallback: true,
    changedCustom: false
  });
});

test('rejects malformed mappings instead of guessing', () => {
  const result = constructSegmentRouteWorkState(draft({
    route: { type: 'LineString', coordinates: [[10, 20], [11, 21], [12, 22]] },
    waypoints: [['via-b', 'b', null]]
  }), editorState);
  assert.equal(result.ok, false);
  assert.match(result.message, /missing waypoint index/i);
});

test('insert move and remove edit only anonymous nodes and continuously shift indices', () => {
  const state = construct(draft({ waypoints: [['via-b', 'b', null], ['via-c', 'c', null]], to: 'd' }));
  const inserted = insertAnonymousNode(state, 'from');
  assert.ok(inserted);
  assert.equal(moveAnonymousNode(state, inserted.key, [10.25, 20.25]), true);
  assert.deepEqual(projectSegmentRouteWork(state)?.waypointRouteVertexIndices, [2, 3]);
  assert.equal(removeAnonymousNode(state, inserted.key), true);
  assert.deepEqual(projectSegmentRouteWork(state)?.waypointRouteVertexIndices, [1, 2]);
});

test('anchors are immutable and cannot be removed', () => {
  const state = construct(draft());
  const before = cloneSegmentRouteWorkState(state);
  assert.equal(moveAnonymousNode(state, 'waypoint:via-b', [50, 50]), false);
  assert.equal(removeAnonymousNode(state, 'from'), false);
  assert.deepEqual(state, before);
});

test('changed fallback becomes custom and Clear returns an anchor-only null proposal', () => {
  const state = construct(draft());
  insertAnonymousNode(state, 'from');
  assert.equal(projectSegmentRouteWork(state)?.changedCustom, true);
  clearAnonymousNodes(state);
  assert.deepEqual(projectSegmentRouteWork(state), {
    route: null,
    waypointRouteVertexIndices: [null],
    unchangedFallback: false,
    changedCustom: false
  });
});

test('closed loops retain distinct positional identities for one saved Place', () => {
  const state = construct(draft({ from: 'a', to: 'a' }));
  assert.deepEqual(state.nodes.map(node => [node.key, node.placeId]), [['from', 'a'], ['waypoint:via-b', 'b'], ['to', 'a']]);
});

test('zero-waypoint custom and fallback routes remain compatible', () => {
  const fallback = construct(draft({ waypoints: [] }));
  assert.equal(projectSegmentRouteWork(fallback)?.route, null);
  const custom = construct(draft({ route: { type: 'LineString', coordinates: [[10, 20], [11, 20.5], [12, 22]] }, waypoints: [] }));
  assert.equal(custom.nodes[1].kind, 'anonymous');
  assert.deepEqual(projectSegmentRouteWork(custom)?.waypointRouteVertexIndices, []);
  const optional = construct(draft({ route: { type: 'LineString', coordinates: [[10, 20], [11, 20.5]] }, to: null, waypoints: [] }));
  assert.deepEqual(optional.nodes.map(node => node.kind), ['anchor', 'anonymous']);
  const unlinked = construct(draft({ route: { type: 'LineString', coordinates: [[9, 19], [11, 20.5]] }, from: null, to: null, waypoints: [] }));
  assert.deepEqual(unlinked.nodes.map(node => node.kind), ['anonymous', 'anonymous']);
});

const beginLifecycle = draftValue => {
  let mapOptions;
  let cleaned = 0;
  const lifecycle = { work: null, stopEdit: null };
  const editorSurface = { enterMapWork: options => { mapOptions = options; return true; } };
  const routeEditor = {
    setSegmentRouteWorkState: state => { lifecycle.work = structuredClone(state); },
    startSegmentRouteWork: options => { lifecycle.work = structuredClone(options.initialState); return () => { cleaned += 1; }; }
  };
  const error = beginSegmentRouteMapWork('segment', draftValue, editorSurface, routeEditor, lifecycle, editorState, () => undefined);
  assert.equal(error, null);
  return { cleaned: () => cleaned, lifecycle, mapOptions };
};

test('Done atomically transfers changed geometry and indices without persistence', () => {
  const value = draft();
  const work = beginLifecycle(value);
  work.mapOptions.routePointEditor.insertAfter('from');
  work.mapOptions.done();
  assert.equal(value.route.coordinates.length, 4);
  assert.deepEqual(value.waypointRouteVertexIndices, [2]);
  assert.equal(value.waypointRows[0].routeVertexIndex, 2);
  assert.equal(work.cleaned(), 1);
});

test('Cancel rollback restores exact pre-work route and indices', () => {
  const value = draft({
    route: { type: 'LineString', coordinates: [[10, 20], [10.5, 20.5], [11, 21], [12, 22]] },
    waypoints: [['via-b', 'b', 2]]
  });
  const before = structuredClone(value);
  const work = beginLifecycle(value);
  work.mapOptions.routePointEditor.remove('anonymous:1');
  work.mapOptions.rollback(work.mapOptions.snapshot());
  work.mapOptions.cancel();
  assert.deepEqual(value, before);
  assert.equal(work.cleaned(), 1);
});

test('Clear retains anchors and Done transfers null geometry and null indices', () => {
  const value = draft({
    route: { type: 'LineString', coordinates: [[10, 20], [10.5, 20.5], [11, 21], [12, 22]] },
    waypoints: [['via-b', 'b', 2]]
  });
  const work = beginLifecycle(value);
  work.mapOptions.clear();
  work.mapOptions.done();
  assert.equal(value.route, null);
  assert.deepEqual(value.waypointRouteVertexIndices, [null]);
  assert.deepEqual(value.waypointRows.map(row => row.routeVertexIndex), [null]);
});
