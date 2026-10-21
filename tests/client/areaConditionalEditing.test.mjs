import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { parse, compileScript, compileTemplate } from '@vue/compiler-sfc';
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

for (const field of ['name', 'fillHex', 'geometry']) {
  test(`Area ${field} payload contains only authored intent and exact nullable/Polygon original`, () => withGlobals(async () => {
    const initial = area({ originalValues: { regionId: 'region', name: 'Original', notesHtml: null, fillHex: null, geometry: 'server-ewkb' } });
    const owner = mount(initial);
    const calls = [];
    globalThis.fetch = async (_url, init) => { calls.push(JSON.parse(init.body)); return saved(area()); };
    try {
      await owner.editor.openAreaEdit(initial);
      owner.editor.areaDraft[field] = field === 'geometry' ? polygon(20) : field === 'fillHex' ? '#abcdef' : 'Authored';
      await owner.editor.saveAreaDraft();
      assert.deepEqual(calls[0], { expected: { regionId: 'region', [field]: initial.originalValues[field] },
        changes: { [field]: field === 'geometry' ? polygon(20) : field === 'fillHex' ? '#abcdef' : 'Authored' } });
    } finally { owner.dispose(); }
  }));
}

for (const reversal of [false, true]) {
  test(`map-work Polygon and unsent Notes survive delayed reply/refresh, reversal=${reversal}`, () => withGlobals(async () => {
    const owner = mount();
    const reply = deferred();
    const calls = [];
    globalThis.fetch = async (_url, init) => { calls.push(JSON.parse(init.body)); return reply.promise; };
    try {
      await owner.editor.openAreaEdit(owner.props.state.areasById.area);
      owner.editor.drawAreaPolygon();
      assert.deepEqual(owner.mapOptions.initialGeometry, polygon(10));
      owner.mapOptions.onChanged(polygon(20));
      await owner.surface.finishMapWork();
      assert.deepEqual(owner.editor.areaDraft.geometry, polygon(20));
      const saving = owner.editor.saveAreaDraft();
      owner.editor.drawAreaPolygon();
      owner.mapOptions.onChanged(polygon(reversal ? 10 : 30));
      await owner.surface.finishMapWork();
      owner.editor.areaDraft.notesHtml = 'Unsent newer notes';
      owner.update(area({ geometry: polygon(40), notesHtml: 'Concurrent refresh' }));
      await nextTick();
      assert.deepEqual(owner.editor.areaDraft.geometry, polygon(reversal ? 10 : 30));
      assert.equal(owner.editor.areaDirty, true);
      reply.resolve(saved(area({ geometry: polygon(20) })));
      await saving;
      await nextTick();
      assert.deepEqual(owner.props.state.areasById.area.geometry, polygon(20));
      assert.deepEqual(owner.editor.areaDraft.geometry, polygon(reversal ? 10 : 30));
      assert.equal(owner.editor.areaDraft.notesHtml, 'Unsent newer notes');
      assert.equal(owner.editor.areaDirty, true);
      assert.equal(owner.editor.statusText, 'Unsaved changes');
      globalThis.fetch = async (_url, init) => { calls.push(JSON.parse(init.body)); return saved(area({
        geometry: polygon(reversal ? 10 : 30), notesHtml: 'Unsent newer notes' })); };
      await owner.editor.saveAreaDraft();
      assert.deepEqual(calls[1].expected, { regionId: 'region', notesHtml: null, geometry: area({ geometry: polygon(20) }).originalValues.geometry });
      await nextTick();
      assert.equal(owner.editor.areaDirty, false);
      assert.equal(owner.dirty.at(-1), false);
      assert.equal(owner.stops, 2);
    } finally { owner.dispose(); }
  }));
}

for (const status of [400, 409]) {
  test(`Area rejection ${status} retains originals/draft until explicit Reset or Cancel`, () => withGlobals(async () => {
    const owner = mount();
    const calls = [];
    globalThis.fetch = async (_url, init) => { calls.push(JSON.parse(init.body)); return new Response(JSON.stringify({
      code: 'mutation-base-stale', detail: 'Review Area fields.', ...(status === 400 ? { errors: { name: ['Invalid name.'] } } : {}) }), { status }); };
    try {
      await owner.editor.openAreaEdit(owner.props.state.areasById.area);
      owner.editor.areaDraft.name = 'Mine';
      await owner.editor.saveAreaDraft();
      owner.update(area({ name: 'Other device', fillHex: '#abcdef' }));
      await nextTick();
      assert.equal(owner.editor.areaDraft.name, 'Mine');
      assert.ok(owner.editor.saveError);
      await owner.editor.saveAreaDraft();
      assert.deepEqual(calls[0], calls[1]);
      owner.editor.resetAreaDraft();
      assert.equal(owner.editor.areaDraft.name, 'Other device');
      assert.equal(owner.editor.areaDirty, false);
      owner.editor.areaDraft.name = 'Reviewed';
      await owner.editor.saveAreaDraft();
      assert.deepEqual(calls[2].expected, { regionId: 'region', name: 'Other device' });
      await owner.editor.cancelAreaDraft();
      assert.notEqual(owner.editor.areaDraft.id, null, 'declined discard retains draft');
      globalThis.areaConfirm = async () => true;
      await owner.editor.cancelAreaDraft();
      assert.equal(owner.editor.areaDraft.id, null);
      assert.equal(owner.editor.areaDirty, false);
    } finally { owner.dispose(); }
  }));
}

for (const failure of ['server', 'transport', 'invalid success', 'empty success']) {
  test(`Area ${failure} uncertainty holds a reversal through refresh and prevents blind replay/exit`, () => withGlobals(async () => {
    const owner = mount();
    const reply = deferred();
    let calls = 0;
    globalThis.fetch = async () => { calls++; const response = await reply.promise; if (!response) throw new TypeError('Lost reply'); return response; };
    try {
      await owner.editor.openAreaEdit(owner.props.state.areasById.area);
      owner.editor.areaDraft.name = 'Submitted';
      const saving = owner.editor.saveAreaDraft();
      owner.editor.areaDraft.name = 'Original';
      reply.resolve(failure === 'transport' ? null : new Response(failure === 'server' || failure === 'empty success' ? '{}' : 'broken JSON', { status: failure === 'server' ? 500 : 200 }));
      await saving;
      owner.update(area({ name: 'Submitted' }));
      await nextTick();
      assert.equal(owner.editor.areaDraft.name, 'Original');
      assert.equal(owner.editor.areaDirty, true);
      await owner.editor.saveAreaDraft();
      assert.equal(calls, 1);
      assert.match(owner.editor.saveError, /unknown outcome/);
      assert.equal(await owner.surface.activateTarget({ identity: 'other', key: 'other' }), false);
      assert.equal(owner.editor.areaDraft.name, 'Original');
      owner.editor.resetAreaDraft();
      assert.equal(owner.editor.areaDraft.name, 'Submitted');
      assert.equal(owner.editor.areaDirty, false);
    } finally { owner.dispose(); }
  }));
}

test('Region drift never silently rebinds pinned Area scope; missing originals and unsupported route fail closed', () => withGlobals(async () => {
  const owner = mount();
  const calls = [];
  globalThis.fetch = async (url, init) => { calls.push([url, init.method, JSON.parse(init.body)]); return new Response('{}', { status: 404 }); };
  try {
    await owner.editor.openAreaEdit(owner.props.state.areasById.area);
    owner.editor.areaDraft.notesHtml = 'Mine';
    owner.update(area({ regionId: 'unassigned', notesHtml: 'Other' }));
    await nextTick();
    await owner.editor.saveAreaDraft();
    assert.deepEqual(calls[0][2], { expected: { regionId: 'region', notesHtml: null }, changes: { notesHtml: 'Mine' } });
    assert.equal(calls[0][1], 'PATCH');
    assert.equal(owner.editor.areaDraft.regionId, 'region');
    owner.editor.resetAreaDraft();
    assert.equal(owner.editor.areaDraft.regionId, 'unassigned');
    const noOriginals = area();
    delete noOriginals.originalValues;
    owner.update(noOriginals);
    await nextTick();
    owner.editor.resetAreaDraft();
    owner.editor.areaDraft.notesHtml = 'Without evidence';
    await owner.editor.saveAreaDraft();
    assert.equal(calls.length, 1);
    assert.match(owner.editor.saveError, /Original Area parent/);
  } finally { owner.dispose(); }
}));

test('Area map Cancel restores holes; create/delete/order retain existing routes and discard guards', () => withGlobals(async () => {
  const owner = mount();
  const calls = [];
  globalThis.fetch = async (url, init) => { calls.push([url, init.method, JSON.parse(init.body ?? 'null')]); return saved(area()); };
  try {
    await owner.editor.openAreaEdit(owner.props.state.areasById.area);
    owner.editor.drawAreaPolygon();
    owner.mapOptions.onChanged(polygon(20));
    assert.equal(await owner.surface.cancelMapWork(), false);
    globalThis.areaConfirm = async () => true;
    await owner.surface.cancelMapWork();
    assert.deepEqual(owner.editor.areaDraft.geometry, polygon(10));
    assert.equal(owner.stops, 1);
    await owner.editor.cancelAreaDraft();
    await owner.editor.openAreaCreate(region);
    assert.equal(owner.editor.areaDirty, false);
    owner.editor.drawAreaPolygon();
    owner.mapOptions.onChanged(polygon(20));
    await owner.surface.finishMapWork();
    await owner.editor.saveAreaDraft();
    await nextTick();
    assert.equal(calls[0][0], '/api/trips/trip/editor/regions/region/areas');
    assert.equal(calls[0][1], 'POST');
    assert.deepEqual(calls[0][2].geometry, polygon(20));
    assert.equal(Object.hasOwn(calls[0][2], 'expected'), false);
    owner.editor.areaDraft.name = 'Dirty';
    globalThis.areaConfirm = async () => false;
    await owner.editor.reorderAreas('region', ['area'], ['area']);
    assert.equal(calls.length, 1);
    globalThis.areaConfirm = async () => true;
    await owner.editor.deleteDraftArea();
    assert.equal(calls[1][1], 'DELETE');
    assert.equal(calls[1][0], '/api/trips/trip/editor/areas/area');
    await owner.editor.reorderAreas('region', ['area'], ['area']);
    assert.equal(calls[2][1], 'PUT');
    assert.equal(calls[2][0], '/api/trips/trip/editor/regions/region/areas/order');
  } finally { owner.dispose(); }
}));

// Compile the actual App -> TripSidebar -> RegionManager tree. Only map/DOM leaves are substituted.
const appBundle = await build({ stdin: { contents: 'export { default } from "./App.vue";', loader: 'ts',
  resolveDir: 'ClientApps/trip-editor/src' }, bundle: true, write: false, format: 'esm', platform: 'node', external: ['vue'],
  plugins: [{ name: 'actual-area-parent-tree', setup(builder) {
    builder.onLoad({ filter: /\.vue$/ }, async args => {
      if (!/(?:App|TripSidebar|RegionManager)\.vue$/.test(args.path)) return { contents: 'export default { render: () => null };' };
      const { descriptor: sfc } = parse(await readFile(args.path, 'utf8'), { filename: args.path });
      const compiled = compileScript(sfc, { id: args.path, genDefaultAs: '__component' });
      const template = compileTemplate({ source: sfc.template.content, filename: args.path, id: args.path,
        compilerOptions: { bindingMetadata: compiled.bindings } });
      return { loader: 'ts', contents: `${compiled.content}\n${template.code}\n__component.render = render;
        const originalSetup = __component.setup;
        __component.setup = (props, context) => {
          const bindings = originalSetup(props, context);
          globalThis.captureAreaComponent(${JSON.stringify(args.path.split('/').at(-1))}, bindings);
          return bindings;
        }; export default __component;` };
    });
    builder.onLoad({ filter: /leafletAdapter\.ts$/ }, () => ({ contents: `export const createTripEditorMap = () => ({
      render() {}, dispose() {}, clearSearchPreview() {}, setSegmentPresentation() {}, setSegmentDraftPreview() {},
      setPlaceDraftPreview() {} }); export const hasAnyGeometry = () => true; export const hasSavedTripView = () => false;
      export const canFocusActiveEntity = () => true;` }));
    builder.onLoad({ filter: /notesHtml\.ts$/ }, () => ({ contents: 'export const normalizeNotesHtml = value => value ?? "";' }));
    builder.onLoad({ filter: /useConfirmDialog\.ts$/ }, () => ({ contents: `export const confirm = options => globalThis.areaConfirm(options);
      export const disposeConfirmDialogHost = () => {}; export const setConfirmDialogFocusFallback = () => {};` }));
  } }] });
const appCode = appBundle.outputFiles[0].text.replaceAll('from "vue"', `from "${import.meta.resolve('vue')}"`);
const actualApp = (await import(`data:text/javascript;base64,${Buffer.from(appCode).toString('base64')}`)).default;

test('actual App/TripSidebar parent updates preserve Area originals and newer dirty edits after a delayed reply', () => withGlobals(async () => {
  const previousWindow = globalThis.window;
  const previousCapture = globalThis.captureAreaComponent;
  const captured = {};
  globalThis.captureAreaComponent = (name, bindings) => { captured[name] = proxyRefs(bindings); };
  globalThis.window = { matchMedia: () => ({ matches: false, addEventListener() {}, removeEventListener() {} }) };
  const initial = { tripId: 'trip', metadata: { name: 'Trip', notesHtml: '', updatedAt: '2026-10-21T00:00:00Z' },
    regionsById: { region }, regionOrder: ['region'], areasById: { area: area() }, areaOrderByRegionId: { region: ['area'] },
    placesById: {}, placeOrderByRegionId: {}, segmentsById: {}, segmentOrder: [], tagsBySlug: {}, tagOrder: [],
    options: { areaDefaults: { name: 'Area', fillHex: '#ff6600' }, transportModes: [] }, permissions: { canEditAreas: true }, visitProgress: {} };
  const envelope = data => ({ success: true, data, affected: { metadata: null, regions: [], places: [], areas: data ? [data] : [],
    segments: [], tags: [], placeOrdersByRegionId: {}, areaOrdersByRegionId: {} },
    deletedIds: { regions: [], places: [], areas: [], segments: [], tags: [] }, warnings: [] });
  const reply = deferred();
  const calls = [];
  globalThis.fetch = async (_url, init) => {
    if (!init?.body) return new Response(JSON.stringify(initial));
    calls.push(JSON.parse(init.body));
    return reply.promise;
  };
  const renderer = createRenderer({ createElement: tag => ({ tagName: tag.toUpperCase(), style: {},
    addEventListener() {}, removeEventListener() {}, setAttribute() {}, removeAttribute() {} }), createText: () => ({}), createComment: () => ({}),
    insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => ({}), nextSibling: () => null });
  const app = renderer.createApp(actualApp, { config: { editorEndpoint: '/api/trips/trip/editor', antiforgeryToken: 'anti', tripIndexUrl: '/User/Trip' } });
  app.mount({});
  try {
    for (let tick = 0; tick < 20 && !captured['RegionManager.vue']; tick++) await new Promise(done => setImmediate(done));
    const parent = captured['App.vue'];
    const editor = captured['RegionManager.vue'];
    assert.ok(editor, 'production parent tree mounted its real Area owner');
    await editor.openAreaEdit(parent.state.areasById.area);
    editor.areaDraft.notesHtml = 'Submitted';
    parent.applyMutation(envelope(area({ name: 'Concurrent Name', notesHtml: 'Concurrent Notes' })));
    await nextTick();
    assert.equal(editor.areaDraft.notesHtml, 'Submitted');
    assert.equal(editor.areaDraft.name, 'Concurrent Name');
    const saving = editor.saveAreaDraft();
    editor.areaDraft.notesHtml = 'Newer unsaved';
    reply.resolve(new Response(JSON.stringify(envelope(area({ name: 'Concurrent Name', notesHtml: 'Submitted' })))));
    await saving;
    await nextTick();
    assert.equal(parent.state.areasById.area.notesHtml, 'Submitted');
    assert.equal(editor.areaDraft.notesHtml, 'Newer unsaved');
    assert.equal(editor.areaDirty, true);
    assert.deepEqual(calls[0], { expected: { regionId: 'region', notesHtml: null }, changes: { notesHtml: 'Submitted' } });
  } finally { app.unmount(); globalThis.window = previousWindow; globalThis.captureAreaComponent = previousCapture; }
}));
