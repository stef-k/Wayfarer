import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const componentPath = new URL('../../ClientApps/trip-editor/src/components/SegmentWaypointEditor.vue', import.meta.url);
const managerPath = new URL('../../ClientApps/trip-editor/src/components/SegmentManager.vue', import.meta.url);
const draftsPath = new URL('../../ClientApps/trip-editor/src/components/regionPlaceDrafts.ts', import.meta.url);

const sourceOrEmpty = async (path) => readFile(path, 'utf8').catch(() => '');

test('the Segment editor exposes visible intermediate-place controls', async () => {
  const source = await sourceOrEmpty(componentPath);
  assert.match(source, /<legend[^>]*>\s*Intermediate places\s*<\/legend>/i);
  assert.match(source, />\s*Add intermediate place\s*</i);
});

test('waypoint order can be changed with accessible move controls', async () => {
  const source = await sourceOrEmpty(componentPath);
  assert.match(source, /Move up/);
  assert.match(source, /Move down/);
});

test('indexed errors attach to a stable logical waypoint row', async () => {
  const source = await sourceOrEmpty(draftsPath);
  assert.match(source, /waypointRows/);
  assert.match(source, /clientId/);
  assert.match(source, /submittedWaypointRows/);
});

test('Reset and Cancel operate on the complete waypoint-bearing draft', async () => {
  const source = await sourceOrEmpty(managerPath);
  assert.match(source, /persistedBaseline/);
  assert.match(source, /resetDraft[\s\S]*waypoint/i);
  assert.match(source, /cancelDraft[\s\S]*waypoint/i);
});

test('an unsaved waypoint draft cannot emit a misleading endpoint-only map preview', async () => {
  const source = await sourceOrEmpty(managerPath);
  assert.match(source, /waypointPlaceIds\.length[^\n]*routeDraftPreviewChanged/);
});
