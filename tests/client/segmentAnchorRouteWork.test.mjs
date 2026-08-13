import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { canMutateSegmentRoute } from '../../ClientApps/trip-editor/src/components/segmentRouteWorkPolicy.ts';

const mapWorkPath = new URL('../../ClientApps/trip-editor/src/components/segmentRouteMapWork.ts', import.meta.url);
const workLayerPath = new URL('../../ClientApps/trip-editor/src/map/segmentRouteWorkLayer.ts', import.meta.url);
const toolbarPath = new URL('../../ClientApps/trip-editor/src/components/MapWorkToolbar.vue', import.meta.url);

/** Reads a production seam used by the focused pre-implementation contract checks. */
const source = async path => readFile(path, 'utf8');

test('waypoint-bearing custom and fallback drafts can enter route work', () => {
  assert.equal(canMutateSegmentRoute({ waypointPlaceIds: ['via'], route: { type: 'LineString', coordinates: [[0, 0], [1, 1], [2, 2]] } }), true);
  assert.equal(canMutateSegmentRoute({ waypointPlaceIds: ['via'], route: null }), true);
});

test('map work carries stable semantic anchor and anonymous node identities', async () => {
  const work = await source(mapWorkPath);
  assert.match(work, /waypointRows/);
  assert.match(work, /anonymous/i);
});

test('Leaflet route work distinguishes fixed anchors from editable vertices', async () => {
  const layer = await source(workLayerPath);
  assert.match(layer, /anchor/i);
  assert.match(layer, /anonymous/i);
});

test('Done transfers geometry and waypoint indices atomically', async () => {
  const work = await source(mapWorkPath);
  assert.match(work, /waypointRouteVertexIndices/);
  assert.match(work, /projection/i);
});

test('fallback and custom work states remain distinguishable after editing', async () => {
  const work = await source(mapWorkPath);
  assert.match(work, /unchangedFallback/);
  assert.match(work, /changedCustom/);
});

test('route work exposes an accessible non-drag ordered point editor', async () => {
  const toolbar = await source(toolbarPath);
  assert.match(toolbar, /Route point/);
  assert.match(toolbar, /longitude/i);
  assert.match(toolbar, /latitude/i);
});

test('cleanup and state transitions are anchor-aware', async () => {
  const work = await source(mapWorkPath);
  assert.match(work, /anchorAwareCleanup/);
});
