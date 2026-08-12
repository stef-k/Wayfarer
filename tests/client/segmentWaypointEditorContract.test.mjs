import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const componentPath = new URL('../../ClientApps/trip-editor/src/components/SegmentWaypointEditor.vue', import.meta.url);
const managerPath = new URL('../../ClientApps/trip-editor/src/components/SegmentManager.vue', import.meta.url);
const notesPath = new URL('../../ClientApps/trip-editor/src/components/RichNotesEditor.vue', import.meta.url);
const surfacePath = new URL('../../ClientApps/trip-editor/src/components/SegmentEditorSurface.vue', import.meta.url);
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
  const component = await sourceOrEmpty(componentPath);
  const source = await sourceOrEmpty(draftsPath);
  assert.match(source, /waypointRows/);
  assert.match(source, /clientId/);
  assert.match(source, /submittedWaypointRows/);
  assert.match(component, /rowErrorId\(row\.clientId\)/);
  assert.match(component, /aria-invalid[^\n]*rowErrors\(row\)\.length/);
  assert.match(component, /aria-errormessage[^\n]*rowErrorId\(row\.clientId\)/);
  assert.match(component, /:id="rowErrorId\(row\.clientId\)"/);
  assert.match(component, /substitute[\s\S]*clearError[^\n]*row\.clientId/);
  assert.match(component, /remove[\s\S]*clearError[^\n]*row\.clientId/);
});

test('Reset focus uses semantic Notes and route component destinations', async () => {
  const manager = await sourceOrEmpty(managerPath);
  const notes = await sourceOrEmpty(notesPath);
  const surface = await sourceOrEmpty(surfacePath);
  assert.match(notes, /defineExpose\(\{ focusEditor:/);
  assert.match(manager, /focusNotes/);
  assert.match(manager, /focusRouteAction/);
  assert.match(surface, /focusRouteAction/);
  assert.match(surface, /data-segment-route-action/);
});

test('Reset and Cancel operate on the complete waypoint-bearing draft', async () => {
  const source = await sourceOrEmpty(managerPath);
  assert.match(source, /persistedBaseline/);
  assert.match(source, /resetDraft[\s\S]*waypoint/i);
  assert.match(source, /cancelDraft[\s\S]*waypoint/i);
});

test('an unsaved waypoint draft cannot emit a misleading endpoint-only map preview', async () => {
  const source = await sourceOrEmpty(managerPath);
  assert.match(source, /waypointPlaceIds\.length[\s\S]*routeDraftPreviewChanged/);
});
