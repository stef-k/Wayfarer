import assert from 'node:assert/strict';
import test from 'node:test';
import { build } from 'esbuild';

const { outputFiles: [bundle] } = await build({
  entryPoints: ['ClientApps/trip-editor/src/recovery/structureOwner.ts'],
  bundle: true, write: false, format: 'esm',
});
const { readStructureOwner } = await import(`data:text/javascript;base64,${Buffer.from(bundle.text).toString('base64')}`);

test('recovery qualifies absence only from the current protocol and a coherent owner graph', async t => {
  const tripId = '12345678-1234-4234-8234-123456789abc';
  const regionId = '23456789-2345-4345-8345-23456789abcd';
  const areaId = '456789ab-4567-4567-8567-456789abcdef';
  const account = 'owner-a';
  const endpoint = `/api/trips/${tripId}/editor`;
  const graph = {
    ownerId: account, tripId, metadata: { id: tripId }, visitProgress: {}, options: {},
    regionsById: { [regionId]: { id: regionId, tripId } }, regionOrder: [regionId],
    segmentsById: {}, segmentOrder: [],
    placesById: {}, placeOrderByRegionId: { [regionId]: [] },
    areasById: { [areaId]: { id: areaId, tripId, regionId, originalValues: { regionId,
      geometry: 'area-polygon-1:' + 'A'.repeat(64), fillHex: null, displayOrder: null } } },
    areaOrderByRegionId: { [regionId]: [areaId] },
  };
  let value = graph;
  let version = '3';
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    assert.equal(url, endpoint);
    assert.equal(init.cache, 'no-store');
    return Response.json(value, { headers: { 'X-Wayfarer-Trip-Dispatch': version } });
  });
  assert.deepEqual(await readStructureOwner(endpoint, account, tripId), graph);
  version = '2';
  await assert.rejects(readStructureOwner(endpoint, account, tripId), /values are unavailable/);
  version = '3';
  value = { ...graph, areasById: {} };
  await assert.rejects(readStructureOwner(endpoint, account, tripId), /comparisons are incomplete/);
});
