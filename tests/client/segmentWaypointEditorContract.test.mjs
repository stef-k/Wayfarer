import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { parse, compileScript, compileTemplate } from '@vue/compiler-sfc';
import { build } from 'esbuild';
import { createSSRApp, proxyRefs, reactive } from 'vue';
import { renderToString } from 'vue/server-renderer';
import { segmentPlaceGroups, segmentWaypointChoices, segmentPlaceUnavailableLabel, segmentPlaceHasLocation } from '../../ClientApps/trip-editor/src/components/segmentPlaceChoices.ts';

const componentPath = new URL('../../ClientApps/trip-editor/src/components/SegmentWaypointEditor.vue', import.meta.url);
const managerPath = new URL('../../ClientApps/trip-editor/src/components/SegmentManager.vue', import.meta.url);
const notesPath = new URL('../../ClientApps/trip-editor/src/components/RichNotesEditor.vue', import.meta.url);
const surfacePath = new URL('../../ClientApps/trip-editor/src/components/SegmentEditorSurface.vue', import.meta.url);
const draftsPath = new URL('../../ClientApps/trip-editor/src/components/regionPlaceDrafts.ts', import.meta.url);

const sourceOrEmpty = async (path) => readFile(path, 'utf8').catch(() => '');

/** Small authoritative catalogue; order arrays deliberately differ from object insertion order. */
const catalogue = () => ({ tripId: 'trip', regionOrder: ['normal', 'unassigned'],
  regionsById: { unassigned: { id: 'unassigned', tripId: 'trip', name: 'Unassigned Places', isShadow: true },
    normal: { id: 'normal', tripId: 'trip', name: 'Region' } },
  placeOrderByRegionId: { normal: ['b', 'a'], unassigned: ['c', 'invalid', 'missing', 'foreign'] },
  placesById: Object.fromEntries(['a', 'b', 'c', 'invalid', 'foreign'].map(id => [id, {
    id, name: id, tripId: id === 'foreign' ? 'another-trip' : 'trip', regionId: ['a', 'b'].includes(id) ? 'normal' : 'unassigned',
    location: id === 'invalid' ? null : { latitude: 38, longitude: 23 }
  }])) });

test('endpoint and waypoint catalogues include Unassigned in authoritative Region/Place order', () => {
  const state = catalogue();
  assert.deepEqual(segmentPlaceGroups(state).map(group => [group.label, group.places.map(p => p.id)]),
    [['Region', ['b', 'a']], ['Unassigned Places', ['c', 'invalid']]]);
  assert.equal(segmentPlaceHasLocation(state.placesById.c), true);
  for (const location of [null, { latitude: NaN, longitude: 23 }, { latitude: 38, longitude: Infinity },
    { latitude: 91, longitude: 23 }, { latitude: 38, longitude: -181 }]) {
    assert.equal(segmentPlaceHasLocation({ location }), false);
  }
});

test('waypoint add and replacement exclude duplicates/endpoints and permit valid Unassigned reselection', () => {
  const state = catalogue();
  const row = { clientId: 'row', placeId: 'c', routeVertexIndex: 2 };
  const draft = { fromPlaceId: 'a', toPlaceId: 'a', waypointRows: [row] };
  assert.deepEqual(segmentWaypointChoices(state, draft, null).map(p => p.id), ['b', 'invalid']);
  assert.deepEqual(segmentWaypointChoices(state, draft, row).map(p => p.id), ['b', 'c', 'invalid']);
  row.placeId = 'b';
  assert.ok(segmentWaypointChoices(state, draft, row).some(p => p.id === 'c'));
  draft.waypointRows.push({ clientId: 'other', placeId: 'c', routeVertexIndex: 3 });
  assert.ok(!segmentWaypointChoices(state, draft, row).some(p => p.id === 'c'));
});

test('Region membership moves retain selected identities and the complete saved route draft', () => {
  const state = catalogue();
  const draft = { fromPlaceId: 'a', toPlaceId: 'b', waypointRows: [{ clientId: 'row', placeId: 'c', routeVertexIndex: 1 }],
    route: { type: 'LineString', coordinates: [[23,38],[24,39],[25,40]] }, estimatedDurationSource: 'Manual', estimatedDurationMinutes: 12 };
  const before = structuredClone(draft);
  state.placesById.a.regionId = 'unassigned';
  state.placeOrderByRegionId.normal = ['b'];
  state.placeOrderByRegionId.unassigned.unshift('a');
  assert.ok(segmentPlaceGroups(state)[1].places.some(p => p.id === draft.fromPlaceId));
  assert.ok(segmentWaypointChoices(state, draft, draft.waypointRows[0]).some(p => p.id === 'c'));
  assert.deepEqual(draft, before);
});

test('missing, inaccessible, invalid-location and unavailable membership references stay distinguishable', () => {
  const state = catalogue();
  assert.match(segmentPlaceUnavailableLabel(state, 'missing'), /no longer available/);
  assert.match(segmentPlaceUnavailableLabel(state, 'foreign'), /no longer available/);
  assert.match(segmentPlaceUnavailableLabel(state, 'invalid'), /location required/);
  state.placesById.c.location.latitude = NaN;
  assert.match(segmentPlaceUnavailableLabel(state, 'c'), /invalid location/);
  state.regionOrder.pop();
  assert.match(segmentPlaceUnavailableLabel(state, 'c'), /region unavailable/);
  const draft = { fromPlaceId: null, toPlaceId: null, waypointRows: [{ clientId: 'stale', placeId: 'c', routeVertexIndex: 2 }] };
  assert.ok(!segmentWaypointChoices(state, draft, draft.waypointRows[0]).some(p => p.id === 'c'));
  assert.equal(draft.waypointRows[0].placeId, 'c');
  assert.equal(draft.waypointRows[0].routeVertexIndex, 2);
});

/** Uses the established SFC compiler seam; child editors are boundaries, both real picker templates run below. */
async function loadSelectorComponent(name) {
  const filename = `ClientApps/trip-editor/src/components/${name}.vue`;
  const { descriptor } = parse(await readFile(filename, 'utf8'), { filename });
  const script = compileScript(descriptor, { id: name });
  const template = compileTemplate({ source: descriptor.template.content, filename, id: name,
    compilerOptions: { bindingMetadata: script.bindings } });
  const bundled = await build({ stdin: { contents: `${script.content}\n${template.code}`, loader: 'ts',
    resolveDir: 'ClientApps/trip-editor/src/components' }, bundle: true, write: false, format: 'esm', platform: 'node',
    external: ['vue'], plugins: [{ name: 'child-editors', setup(builder) {
      builder.onLoad({ filter: /\.vue$/ }, () => ({ contents: 'export default { render: () => null };' }));
    } }] });
  const code = bundled.outputFiles[0].text.replaceAll('from "vue"', `from "${import.meta.resolve('vue')}"`);
  return import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`);
}

for (const name of ['SegmentEditorForm', 'SegmentWaypointEditor']) {
  test(`${name} renders eligible Unassigned choices and retains draft state across catalogue changes`, async () => {
    const { default: component, render } = await loadSelectorComponent(name);
    const state = reactive({ ...catalogue(), options: { transportModes: [] } });
    const draft = { fromPlaceId: 'a', toPlaceId: 'a', mode: '', estimatedDurationSource: 'Manual', estimatedDurationMinutes: 12,
      waypointPlaceIds: ['c'], waypointRouteVertexIndices: [1], waypointRows: [{ clientId: 'row', placeId: 'c', routeVertexIndex: 1 }],
      route: { type: 'LineString', coordinates: [[23,38],[24,39],[23,38]] } };
    const original = structuredClone(draft);
    const events = [];
    const props = { state, draft, baselineDraft: structuredClone(draft), fieldErrors: () => [], formSummaryErrors: [],
      isDirty: false, isSaving: false, semanticEditsSafe: true, formId: 'segment-form' };
    let bindings;
    const app = createSSRApp({ setup() {
      bindings = proxyRefs(component.setup(props, { expose() {}, emit: (...args) => events.push(args) }));
      return () => render({}, [], props, bindings, {}, {});
    } });
    const initial = await renderToString(app);
    assert.match(initial, /value="c"[^>]*>c<\/option>/);
    assert.match(initial, /value="invalid"[^>]*disabled[^>]*>invalid — location required/);
    if (name === 'SegmentEditorForm') assert.equal(initial.match(/optgroup label="Unassigned Places"/g).length, 2);
    state.placesById.a.regionId = 'unassigned';
    state.placeOrderByRegionId.normal = ['b'];
    state.placeOrderByRegionId.unassigned.unshift('a');
    const rerender = () => renderToString(createSSRApp({ render: () => render({}, [], props, bindings, {}, {}) }));
    assert.match(await rerender(), /value="c"[^>]*>c<\/option>/);
    assert.deepEqual(draft, original);
    assert.deepEqual(events, []);
    if (name === 'SegmentWaypointEditor') {
      bindings.substitute(draft.waypointRows[0], 'b');
      assert.match(await rerender(), /value="c"[^>]*>c<\/option>/);
      bindings.substitute(draft.waypointRows[0], 'c');
      assert.deepEqual(draft, original);
    }
    delete state.placesById.c;
    delete state.placesById.a;
    assert.match(await rerender(), /disabled[^>]*>Place no longer available/);
    assert.deepEqual(draft, original);
  });
}

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
  assert.match(notes, /defineExpose\(\{ focusEditor(?:\s*[:,}])/);
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

test('safe waypoint preservation remains anchored to the persisted semantic baseline', async () => {
  const component = await sourceOrEmpty(componentPath);
  const manager = await sourceOrEmpty(managerPath);
  assert.match(manager, /:baseline-draft="persistedBaseline"/);
  assert.match(manager, /createSegmentSemanticTaint/);
  assert.match(manager, /semanticTaint\.markUnsafe/);
  assert.match(component, /semanticEditsSafe/);
  assert.match(component, /endpointsMatchBaseline/);
  assert.match(component, /substitute[\s\S]*emit\('semanticEditsUnsafe'\)/);
  assert.match(component, /move[\s\S]*emit\('semanticEditsUnsafe'\)/);
});
