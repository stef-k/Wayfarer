import assert from 'node:assert/strict';
import test from 'node:test';
import { canMutateSegmentRoute, invokeSegmentRouteAction } from '../../ClientApps/trip-editor/src/components/segmentRouteWorkPolicy.ts';

test('waypoint-bearing custom and fallback routes can enter anchor-aware route work', () => {
  assert.equal(canMutateSegmentRoute({ waypointPlaceIds: ['waypoint'], route: { coordinates: [[0, 0], [1, 1]] } }), true);
  assert.equal(canMutateSegmentRoute({ waypointPlaceIds: ['waypoint'], route: null }), true);
});

test('waypoint-bearing controls invoke the anchor-aware action boundary', () => {
  let activations = 0;
  const draft = { waypointPlaceIds: ['waypoint'], route: { coordinates: [[0, 0], [1, 1]] } };
  assert.equal(invokeSegmentRouteAction(draft, () => { activations += 1; }), true);
  assert.equal(invokeSegmentRouteAction(draft, () => { activations += 1; }), true);
  assert.equal(activations, 2);
});

test('zero-waypoint controls retain existing route behavior', () => {
  let activations = 0;
  const draft = { waypointPlaceIds: [], route: null };
  assert.equal(canMutateSegmentRoute(draft), true);
  assert.equal(invokeSegmentRouteAction(draft, () => { activations += 1; }), true);
  assert.equal(activations, 1);
});
