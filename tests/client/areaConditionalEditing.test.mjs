import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { parse, compileScript } from '@vue/compiler-sfc';
import { build } from 'esbuild';
import { createRenderer, h, nextTick, proxyRefs, shallowReactive } from 'vue';

// Mount the production owner and surface with Vue's real parent/child update queue; stub only rendering and DOM HTML parsing.
const filename = 'ClientApps/trip-editor/src/components/RegionManager.vue';
const { descriptor } = parse(await readFile(filename, 'utf8'), { filename });
const script = compileScript(descriptor, { id: 'area-conditional-test' });
const bundled = await build({ stdin: { contents: script.content + '\nexport { useEditorSurface } from "../composables/useEditorSurface";',
  loader: 'ts', resolveDir: 'ClientApps/trip-editor/src/components' }, bundle: true, write: false, format: 'esm', platform: 'node',
  external: ['vue'], plugins: [{ name: 'area-leaves', setup(builder) {
    builder.onLoad({ filter: /\.vue$/ }, () => ({ contents: 'export default {};' }));
    builder.onLoad({ filter: /notesHtml\.ts$/ }, () => ({ contents: 'export const normalizeNotesHtml = value => value ?? "";' }));
    builder.onLoad({ filter: /useConfirmDialog\.ts$/ }, () => ({ contents: 'export const confirm = options => globalThis.areaConfirm(options);' }));
  } }] });
const code = bundled.outputFiles[0].text.replaceAll('from "vue"', `from "${import.meta.resolve('vue')}"`);
const { default: component, useEditorSurface } = await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`);
const polygon = offset => ({ type: 'Polygon', coordinates: [
  [[offset, 1], [offset + 4, 1], [offset + 4, 5], [offset, 1]],
  [[offset + 1, 2], [offset + 2, 2], [offset + 2, 3], [offset + 1, 2]]
] });
const area = (overrides = {}) => {
  const value = { id: 'area', tripId: 'trip', regionId: 'region', name: 'Original', notesHtml: '', fillHex: '#ff6600',
    geometry: polygon(10), displayOrder: 1, capabilities: { canEdit: true, canDelete: true }, ...overrides };
  value.originalValues ??= { regionId: value.regionId, name: value.name, notesHtml: value.notesHtml || null,
    fillHex: value.fillHex, geometry: 'area-polygon-1:' + JSON.stringify(value.geometry) };
  return value;
};
const region = { id: 'region', name: 'Region', isShadow: false, capabilities: { canEdit: true, canAddChildren: true } };
const mount = (initial = area()) => {
  let editor;
  const dirty = [];
  const surface = useEditorSurface();
  surface.clearActiveTarget();
  let mapOptions;
  let stops = 0;
  const props = shallowReactive({ state: { regionsById: { region }, regionOrder: ['region'],
    placesById: {}, placeOrderByRegionId: {}, areasById: { area: initial }, areaOrderByRegionId: { region: ['area'] },
    options: { areaDefaults: { name: 'Area', fillHex: '#ff6600' } }, permissions: { canEditAreas: true } },
    editorSurface: surface, editorEndpoint: '/api/trips/trip/editor', antiforgeryToken: 'anti', searchActive: false,
    selectedPlaceId: null, searchRegions: [], searchPlaceIdsByRegionId: {}, searchAreaIdsByRegionId: {}, pendingSearchAdd: null,
    coordinatePicker: {}, polygonEditor: { startAreaPolygonWork: options => { mapOptions = options; return () => { stops++; }; } },
    selectPlace: async () => true, clearSelectedPlace: async () => true });
  const update = next => { props.state = { ...props.state, areasById: { ...props.state.areasById, [next.id]: next } }; };
  const child = { ...component, setup(childProps, context) { editor = proxyRefs(component.setup(childProps, context)); return () => null; } };
  const renderer = createRenderer({ createComment: () => ({}), insert() {}, remove() {}, parentNode: () => ({}), nextSibling: () => null });
  const app = renderer.createApp({ setup: () => () => h(child, { ...props,
    onMutationApplied: result => { for (const next of result.affected.areas ?? []) update(next); },
    onDirtyStateChanged: value => dirty.push(value) }) });
  app.mount({});
  return { editor, props, update, dirty, surface, get mapOptions() { return mapOptions; }, get stops() { return stops; },
    dispose: () => { app.unmount(); surface.clearActiveTarget(); } };
};
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; };
const saved = data => new Response(JSON.stringify({ success: true, data, affected: { areas: [data] }, warnings: [] }));
const withGlobals = async action => {
  const previous = { document: globalThis.document, fetch: globalThis.fetch, areaConfirm: globalThis.areaConfirm };
  globalThis.document = { getElementById: () => null };
  globalThis.areaConfirm = async () => false;
  try { await action(); } finally { Object.assign(globalThis, previous); }
};

test('Area dirty originals stay pinned across a real parent refresh; untouched fields never enter the request', () => withGlobals(async () => {
  const initial = area({ originalValues: { regionId: 'region', name: 'Original', notesHtml: null, fillHex: null, geometry: 'opaque-original' } });
  const owner = mount(initial);
  const calls = [];
  globalThis.fetch = async (url, init) => { calls.push([url, init]); return saved(area({ name: 'Concurrent name', notesHtml: 'Mine', fillHex: '#112233', geometry: polygon(20) })); };
  try {
    await owner.editor.openAreaEdit(initial);
    assert.equal(owner.editor.areaDirty, false);
    owner.editor.areaDraft.notesHtml = 'Mine';
    owner.update(area({ name: 'Concurrent name', notesHtml: 'Concurrent notes', fillHex: '#112233', geometry: polygon(20) }));
    await nextTick();
    assert.equal(owner.editor.areaDraft.notesHtml, 'Mine');
    assert.equal(owner.editor.areaDraft.name, 'Concurrent name');
    assert.deepEqual(owner.editor.areaDraft.geometry, polygon(20));
    await owner.editor.saveAreaDraft();
    assert.equal(calls[0][0], '/api/trips/trip/editor/areas/area/conditional');
    assert.equal(calls[0][1].method, 'PATCH');
    assert.deepEqual(JSON.parse(calls[0][1].body), { expected: { regionId: 'region', notesHtml: null }, changes: { notesHtml: 'Mine' } });
    await nextTick();
    assert.equal(owner.editor.areaDirty, false);
  } finally { owner.dispose(); }
}));
