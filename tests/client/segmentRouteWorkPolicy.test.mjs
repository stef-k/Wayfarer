import assert from 'node:assert/strict';
import test from 'node:test';
import { canMutateSegmentRoute, invokeSegmentRouteAction } from '../../ClientApps/trip-editor/src/components/segmentRouteWorkPolicy.js';

test('waypoint-bearing custom and fallback routes disable every route mutation', () => {
  assert.equal(canMutateSegmentRoute({ waypointPlaceIds: ['waypoint'], route: { coordinates: [[0, 0], [1, 1]] } }), false);
  assert.equal(canMutateSegmentRoute({ waypointPlaceIds: ['waypoint'], route: null }), false);
});

test('disabled waypoint-bearing controls cannot invoke draw or clear actions', () => {
  let activations = 0;
  const draft = { waypointPlaceIds: ['waypoint'], route: { coordinates: [[0, 0], [1, 1]] } };
  assert.equal(invokeSegmentRouteAction(draft, () => { activations += 1; }), false);
  assert.equal(invokeSegmentRouteAction(draft, () => { activations += 1; }), false);
  assert.equal(activations, 0);
});

test('zero-waypoint controls retain existing route behavior', () => {
  let activations = 0;
  const draft = { waypointPlaceIds: [], route: null };
  assert.equal(canMutateSegmentRoute(draft), true);
  assert.equal(invokeSegmentRouteAction(draft, () => { activations += 1; }), true);
  assert.equal(activations, 1);
});
