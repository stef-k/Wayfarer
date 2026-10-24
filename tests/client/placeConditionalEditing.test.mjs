import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { parse, compileScript, compileTemplate } from '@vue/compiler-sfc';
import { build } from 'esbuild';
import { createRenderer, nextTick, proxyRefs } from 'vue';

// Mount the actual App -> TripSidebar -> RegionManager queue; substitute only map, DOM/Quill and unrelated leaves.
const bundle = await build({ stdin: { contents: 'export { default } from "./App.vue";', loader: 'ts',
  resolveDir: 'ClientApps/trip-editor/src' }, bundle: true, write: false, format: 'esm', platform: 'node', external: ['vue'],
  plugins: [{ name: 'place-parent-tree', setup(builder) {
    builder.onLoad({ filter: /\.vue$/ }, async args => {
      if (/SegmentEditorSurface\.vue$/.test(args.path)) return { contents: `export default { setup(_, context) {
        context.expose({ focusNotes() {}, focusRouteAction: () => false }); return () => null;
      } };` };
      if (!/(?:App|TripSidebar|RegionManager|PlaceEditorSurface|EditorSurface)\.vue$/.test(args.path)) return { contents: 'export default { render: () => null };' };
      const { descriptor } = parse(await readFile(args.path, 'utf8'), { filename: args.path });
      const script = compileScript(descriptor, { id: args.path, genDefaultAs: '__component' });
      const template = compileTemplate({ source: descriptor.template.content, filename: args.path, id: args.path,
        compilerOptions: { bindingMetadata: script.bindings } });
      return { loader: 'ts', contents: `${script.content}\n${template.code}\n__component.render = render;
        const setup = __component.setup;
        __component.setup = (props, context) => {
          const bindings = setup(props, context);
          globalThis.capturePlaceComponent(${JSON.stringify(args.path.split('/').at(-1))}, bindings);
          return bindings;
        }; export default __component;` };
    });
    builder.onLoad({ filter: /leafletAdapter\.ts$/ }, () => ({ contents: `export const createTripEditorMap = () => ({
      render() {}, dispose() {}, clearSearchPreview() {}, selectPlace() {}, selectSegment() {}, setSegmentPresentation() {}, setSegmentDraftPreview() {},
      setPlaceDraftPreview() {}, setSegmentRouteWorkState() {},
      getMapView: () => ({ center: { latitude: 10, longitude: 20 }, zoom: 5 }),
      startCoordinatePick: options => { globalThis.placeMapWork = options; return () => {}; } });
      export const hasAnyGeometry = () => true; export const hasSavedTripView = () => false;
      export const canFocusActiveEntity = () => true;` }));
    builder.onLoad({ filter: /notesHtml\.ts$/ }, () => ({ contents: 'export const normalizeNotesHtml = value => value ?? "";' }));
    builder.onLoad({ filter: /useConfirmDialog\.ts$/ }, () => ({ contents: `export const confirm = options => globalThis.placeConfirm(options);
      export const disposeConfirmDialogHost = () => {}; export const setConfirmDialogFocusFallback = () => {};` }));
  } }] });
const code = bundle.outputFiles[0].text.replaceAll('from "vue"', `from "${import.meta.resolve('vue')}"`);
const actualApp = (await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`)).default;
const region = { id: 'region', name: 'Region', notesHtml: '', isShadow: false, capabilities: { canEdit: true, canAddChildren: true, canTargetForSearchAdd: true } };
const place = (overrides = {}) => {
  const value = { id: 'place', tripId: 'trip', regionId: 'region', name: 'Original', notesHtml: '', address: 'Original address',
    location: { latitude: 10, longitude: 20 }, iconName: 'marker', markerColor: 'bg-blue', displayOrder: 1,
    visitSummary: { placeId: 'place', visitCount: 0, isVisited: false, firstVisitAt: null, lastVisitAt: null },
    capabilities: { canEdit: true, canDelete: true }, ...overrides };
  value.originalValues ??= { regionId: value.regionId, name: value.name, notesHtml: value.notesHtml || null, iconName: null, markerColor: null };
  if (overrides.iconName && !overrides.originalValues) value.originalValues.iconName = value.iconName;
  if (overrides.markerColor && !overrides.originalValues) value.originalValues.markerColor = value.markerColor;
  return value;
};
const envelope = data => ({ success: true, data, affected: { metadata: null, regions: [], places: data ? [data] : [], areas: [],
  segments: [], tags: [], placeOrdersByRegionId: {}, areaOrdersByRegionId: {},
  regionOrder: null, segmentOrder: null, tagOrder: null, visitProgress: null, options: null },
  deletedIds: { regions: [], places: [], areas: [], segments: [], tags: [] }, warnings: [] });
const saved = data => new Response(JSON.stringify(envelope(data)));
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; };
const withApp = async action => {
  const previous = { window: globalThis.window, document: globalThis.document, fetch: globalThis.fetch, CSS: globalThis.CSS,
    placeConfirm: globalThis.placeConfirm, capturePlaceComponent: globalThis.capturePlaceComponent };
  const captured = {};
  globalThis.capturePlaceComponent = (name, bindings) => { captured[name] = proxyRefs(bindings); };
  globalThis.placeConfirm = async () => false;
  globalThis.CSS = { escape: value => value };
  globalThis.document = { getElementById: () => null, querySelector: () => null };
  globalThis.window = { matchMedia: () => ({ matches: false, addEventListener() {}, removeEventListener() {} }),
    Sortable: { create: () => ({ destroy() {} }) } };
  const initial = { tripId: 'trip', metadata: { name: 'Trip', notesHtml: '', updatedAt: '2026-10-21T00:00:00Z' },
    regionsById: { region }, regionOrder: ['region'], areasById: {}, areaOrderByRegionId: {},
    placesById: { place: place() }, placeOrderByRegionId: { region: ['place'] }, segmentsById: {}, segmentOrder: [], tagsBySlug: {}, tagOrder: [],
    options: { transportModes: [], iconNames: ['marker', 'star'], markerColorClasses: ['bg-blue', 'bg-red'], areaDefaults: { fillHex: '#ff6600' } },
    permissions: { canEditPlaces: true, canEditRegions: true, canEditAreas: true }, visitProgress: {} };
  globalThis.fetch = async () => new Response(JSON.stringify(initial));
  // Real surface v-if transitions need host sibling identity so Vue can remove a fragment's exact range.
  const remove = node => {
    if (node?.parent) node.parent.children.splice(node.parent.children.indexOf(node), 1);
    if (node) node.parent = null;
  };
  const renderer = createRenderer({ createElement: tag => ({ tagName: tag.toUpperCase(), style: {}, children: [],
    addEventListener() {}, removeEventListener() {}, setAttribute() {}, removeAttribute() {} }), createText: () => ({}), createComment: () => ({}),
    insert(node, parent, anchor) {
      remove(node);
      parent.children ??= [];
      const index = anchor ? parent.children.indexOf(anchor) : -1;
      parent.children.splice(index < 0 ? parent.children.length : index, 0, node);
      node.parent = parent;
    }, remove, setText() {}, setElementText() {}, patchProp() {}, parentNode: node => node?.parent,
    nextSibling: node => node?.parent?.children[node.parent.children.indexOf(node) + 1] ?? null });
  const app = renderer.createApp(actualApp, { config: { editorEndpoint: '/api/trips/trip/editor', antiforgeryToken: 'anti', tripIndexUrl: '/User/Trip' } });
  app.mount({});
  try {
    for (let tick = 0; tick < 20 && !captured['RegionManager.vue']; tick++) await new Promise(done => setImmediate(done));
    const parent = captured['App.vue'];
    const editor = captured['RegionManager.vue'];
    assert.ok(editor, 'actual production Place owner mounted through App and TripSidebar');
    parent.editorSurface.clearActiveTarget();
    await action({ parent, editor, sidebar: captured['TripSidebar.vue'] });
  } finally {
    await nextTick();
    await nextTick();
    captured['App.vue']?.editorSurface.clearActiveTarget();
    app.unmount();
    await nextTick();
    Object.assign(globalThis, previous);
    delete globalThis.placeMapWork;
  }
};

// These observations run through the production App affected-slice owner and its asynchronous Vue children.
test('Place scalar PATCH pins null/default originals, adopts clean refreshes and drives marker preview', () => withApp(async ({ parent, editor, sidebar }) => {
  const calls = [];
  globalThis.fetch = async (url, init) => { calls.push([url, init]); return saved(place({ name: 'Refreshed', notesHtml: 'Mine', iconName: 'star' })); };
  await editor.openPlaceEdit(parent.state.placesById.place);
  editor.placeDraft.notesHtml = 'Mine';
  editor.placeDraft.iconName = 'star';
  parent.applyMutation(envelope(place({ name: 'Refreshed', notesHtml: 'Other', address: 'New address' })));
  await nextTick();
  assert.equal(editor.placeDraft.name, 'Refreshed');
  assert.equal(editor.placeDraft.address, 'New address');
  assert.equal(editor.placeDraft.notesHtml, 'Mine');
  assert.equal(editor.placePreviewById.place.iconName, 'star');
  await editor.savePlaceDraft();
  await nextTick();
  assert.equal(calls[0][0], '/api/trips/trip/editor/places/place/conditional');
  assert.equal(calls[0][1].method, 'PATCH');
  assert.deepEqual(JSON.parse(calls[0][1].body), { expected: { regionId: 'region', notesHtml: null, iconName: null }, changes: { notesHtml: 'Mine', iconName: 'star' } });
  assert.equal(parent.state.placesById.place.notesHtml, 'Mine');
  assert.equal(editor.placeDirty, false);
  assert.equal(sidebar.hasAnyDraftChanges, false);
  assert.match(editor.statusText, /^Place saved/);
}));

// Cancel discards only temporary map work, even after App has refreshed the clean coordinate baseline.
for (const authored of [false, true]) {
  test(`Place coordinate Cancel preserves refreshed or intentionally edited coordinates: ${authored}`, () => withApp(async ({ parent, editor }) => {
    await editor.openPlaceEdit(parent.state.placesById.place);
    if (authored) {
      editor.placeDraft.latitude = 11;
      editor.placeDraft.longitude = 22;
      editor.placeDraft.notesHtml = 'Keep my notes';
    }
    editor.pickPlaceCoordinate();
    globalThis.placeMapWork.onPicked({ latitude: 50, longitude: 60 });
    parent.applyMutation(envelope(place({ location: { latitude: 30, longitude: 40 } })));
    await nextTick();
    globalThis.placeConfirm = async () => true;
    await parent.editorSurface.cancelMapWork();
    await nextTick();
    assert.deepEqual([editor.placeDraft.latitude, editor.placeDraft.longitude], authored ? [11, 22] : ['30', '40']);
    assert.equal(editor.placeDirty, authored);
    assert.equal(editor.placeDraftState.baseline.latitude, authored ? '10' : '30');
    assert.equal(editor.placeDraft.notesHtml, authored ? 'Keep my notes' : '');
    assert.deepEqual(parent.activePlaceDraftPreview.coordinate, authored ? { latitude: 11, longitude: 22 } : { latitude: 30, longitude: 40 });
    editor.placeDraft.name = 'Name after Cancel';
    const calls = [];
    globalThis.fetch = async (_url, init) => { calls.push(init); return saved(place({ name: 'Name after Cancel',
      notesHtml: authored ? 'Keep my notes' : '', location: authored ? { latitude: 11, longitude: 22 } : { latitude: 30, longitude: 40 } })); };
    await editor.savePlaceDraft();
    assert.equal(calls.length, 1);
    assert.equal(calls[0].method, authored ? 'PUT' : 'PATCH');
    if (!authored) assert.deepEqual(JSON.parse(calls[0].body), { expected: { regionId: 'region', name: 'Original' }, changes: { name: 'Name after Cancel' } });
  }));
}

// Both demonstrated malformed 200 replies must hold before any production parent or comparison adoption.
for (const scalar of [true, false]) {
  test(`Place malformed ${scalar ? 'scalar projection' : 'combined footprint'} holds before adoption`, () => withApp(async ({ parent, editor, sidebar }) => {
    await editor.openPlaceEdit(parent.state.placesById.place);
    editor.placeDraft.name = 'Submitted';
    if (!scalar) editor.placeDraft.address = 'New address';
    await nextTick();
    const before = JSON.stringify({ parent: parent.state, baseline: editor.placeDraftState.baseline, preview: parent.activePlaceDraftPreview });
    const result = envelope(place({ name: 'Submitted', address: scalar ? 'Original address' : 'New address',
      ...(scalar ? { resolvedFeatureType: { bad: 'type' } } : {}) }));
    if (!scalar) result.affected.regionOrder = ['region', 'region'];
    let calls = 0;
    globalThis.fetch = async (_url, init) => { calls++; assert.equal(init.method, scalar ? 'PATCH' : 'PUT'); return new Response(JSON.stringify(result)); };
    await editor.savePlaceDraft();
    await nextTick();
    assert.equal(JSON.stringify({ parent: parent.state, baseline: editor.placeDraftState.baseline, preview: parent.activePlaceDraftPreview }), before);
    assert.equal(editor.placeDraft.name, 'Submitted');
    assert.equal(editor.placeDraft.address, scalar ? 'Original address' : 'New address');
    assert.deepEqual(editor.placeDraftState.request(), { expected: { regionId: 'region', name: 'Original' }, changes: { name: 'Submitted' } });
    assert.equal(editor.placeDirty, true);
    assert.equal(sidebar.hasAnyDraftChanges, true);
    assert.equal(editor.placeDraftState.uncertain.value, true);
    assert.equal(editor.statusText, 'Save failed');
    await editor.savePlaceDraft();
    assert.match(editor.saveError, /unknown outcome/i);
    assert.equal(calls, 1);
  }));
}

// Complete saves must match the lifecycle footprint, with the saved Place only in its target vector.
for (const [operation, orders] of [
  ['update unrelated-only', { other: ['b', 'a'] }],
  ['update target-only', { region: ['place'] }],
  ['update saved ID repeated in source', { region: ['place'], other: ['place', 'b', 'a'] }],
  ['update extra Region', { region: ['place'], source: [], other: ['b', 'a'] }],
  ['create extra Region', { region: ['place'], other: ['b', 'a'] }]
]) {
  test(`Place complete ${operation} order map holds before adoption`, () => withApp(async ({ parent, editor, sidebar }) => {
    parent.state.regionsById.other = { ...region, id: 'other', name: 'Other Region' };
    parent.state.regionOrder.push('other');
    for (const [index, id] of ['a', 'b'].entries()) parent.state.placesById[id] = place({ id, regionId: 'other',
      name: id, displayOrder: index + 1, visitSummary: { ...place().visitSummary, placeId: id } });
    parent.state.placeOrderByRegionId.other = ['a', 'b'];
    if (operation.startsWith('create')) await editor.openPlaceCreate(region);
    else await editor.openPlaceEdit(parent.state.placesById.place);
    editor.placeDraft.name = 'Submitted';
    editor.placeDraft.address = 'New address';
    await nextTick();
    const before = JSON.stringify({ parent: parent.state, baseline: editor.placeDraftState.baseline, preview: parent.activePlaceDraftPreview });
    const result = envelope(place({ name: 'Submitted', address: 'New address' }));
    result.affected.placeOrdersByRegionId = orders;
    let calls = 0;
    globalThis.fetch = async (_url, init) => { calls++; assert.equal(init.method, operation.startsWith('create') ? 'POST' : 'PUT'); return new Response(JSON.stringify(result)); };
    await editor.savePlaceDraft();
    await nextTick();
    assert.deepEqual(parent.state.placeOrderByRegionId.other, ['a', 'b']);
    assert.equal(JSON.stringify({ parent: parent.state, baseline: editor.placeDraftState.baseline, preview: parent.activePlaceDraftPreview }), before);
    assert.equal(editor.placeDraft.name, 'Submitted');
    assert.equal(editor.placeDraft.address, 'New address');
    if (!operation.startsWith('create')) assert.deepEqual(editor.placeDraftState.request(), {
      expected: { regionId: 'region', name: 'Original' }, changes: { name: 'Submitted' } });
    assert.equal(editor.placeDraftState.uncertain.value, true);
    assert.equal(editor.placeDirty, true);
    assert.equal(sidebar.hasAnyDraftChanges, true);
    assert.equal(editor.statusText, 'Save failed');
    await editor.savePlaceDraft();
    assert.match(editor.saveError, /unknown outcome/i);
    assert.equal(calls, 1);
  }));
}

for (const reversal of [false, true]) {
  test(`newer Place intent and old-baseline reversal survive delayed acknowledgement and later refresh: ${reversal}`, () => withApp(async ({ parent, editor }) => {
    const reply = deferred();
    const calls = [];
    globalThis.fetch = async (_url, init) => { calls.push(JSON.parse(init.body)); return reply.promise; };
    await editor.openPlaceEdit(parent.state.placesById.place);
    editor.placeDraft.name = 'Submitted';
    const saving = editor.savePlaceDraft();
    editor.placeDraft.name = reversal ? 'Original' : 'Newer';
    editor.placeDraft.notesHtml = 'Unsent newer';
    parent.applyMutation(envelope(place({ name: 'Concurrent', notesHtml: 'Concurrent notes' })));
    await nextTick();
    reply.resolve(saved(place({ name: 'Submitted' })));
    await saving;
    await nextTick();
    parent.applyMutation(envelope(place({ name: 'Another refresh', notesHtml: 'Yet more notes', markerColor: 'bg-red' })));
    await nextTick();
    assert.equal(editor.placeDraft.name, reversal ? 'Original' : 'Newer');
    assert.equal(editor.placeDraft.notesHtml, 'Unsent newer');
    assert.equal(editor.placeDraft.markerColor, 'bg-red');
    assert.equal(editor.placeDirty, true);
    assert.equal(editor.statusText, 'Unsaved changes');
    globalThis.fetch = async (_url, init) => { calls.push(JSON.parse(init.body)); return saved(place({ name: editor.placeDraft.name, notesHtml: 'Unsent newer', markerColor: 'bg-red' })); };
    await editor.savePlaceDraft();
    assert.deepEqual(calls[1].expected, { regionId: 'region', name: 'Submitted', notesHtml: null });
    await nextTick();
    assert.equal(editor.placeDirty, false);
  }));
}

for (const status of [400, 409, 405]) {
  test(`Place ${status} preserves dirty content and originals without unconditional fallback`, () => withApp(async ({ parent, editor }) => {
    const calls = [];
    globalThis.fetch = async (_url, init) => { calls.push(init); return new Response(JSON.stringify(status === 400 ? { errors: { name: ['Correct this name'] } } : { code: 'mutation-base-stale', detail: 'Review current values' }), { status }); };
    await editor.openPlaceEdit(parent.state.placesById.place);
    editor.placeDraft.name = 'Mine';
    await editor.savePlaceDraft();
    parent.applyMutation(envelope(place({ name: 'Concurrent' })));
    await nextTick();
    assert.equal(editor.placeDraft.name, 'Mine');
    assert.equal(editor.placeDirty, true);
    if (status === 400) assert.deepEqual(editor.fieldErrors('name'), ['Correct this name']);
    await editor.savePlaceDraft();
    assert.equal(calls.length, 2);
    assert.deepEqual(JSON.parse(calls[1].body).expected, { regionId: 'region', name: 'Original' });
    assert.ok(calls.every(call => call.method === 'PATCH'));
    editor.resetPlaceDraft();
    editor.placeDraft.name = 'Reviewed';
    globalThis.fetch = async (_url, init) => { calls.push(init); return saved(place({ name: 'Reviewed' })); };
    await editor.savePlaceDraft();
    assert.equal(JSON.parse(calls[2].body).expected.name, 'Concurrent');
  }));
}

for (const outcome of ['network', '408', '500', 'incomplete', 'affected mismatch', 'original mismatch', 'missing deletedIds', 'unrelated slice']) {
  test(`Place ${outcome} retains newer content under an unknown hold before any parent adoption`, () => withApp(async ({ parent, editor }) => {
    const reply = deferred();
    let calls = 0;
    globalThis.fetch = async () => { calls++; return reply.promise; };
    await editor.openPlaceEdit(parent.state.placesById.place);
    editor.placeDraft.name = 'Submitted';
    const saving = editor.savePlaceDraft();
    editor.placeDraft.name = 'Original';
    editor.placeDraft.notesHtml = 'Newer';
    let result = envelope(place({ name: 'Submitted' }));
    if (outcome === 'incomplete') delete result.data.visitSummary;
    if (outcome === 'affected mismatch') result.affected.places = [place({ name: 'Unacknowledged' })];
    if (outcome === 'original mismatch') result.data.originalValues.name = 'Other';
    if (outcome === 'missing deletedIds') delete result.deletedIds;
    if (outcome === 'unrelated slice') result.affected.regionOrder = ['unexpected'];
    if (outcome === 'network') reply.resolve(Promise.reject(new TypeError('Lost connection')));
    else reply.resolve(new Response(JSON.stringify(result), { status: /^\d+$/.test(outcome) ? Number(outcome) : 200 }));
    await saving;
    await nextTick();
    assert.equal(editor.placeDraft.name, 'Original');
    assert.equal(editor.placeDraft.notesHtml, 'Newer');
    assert.equal(parent.state.placesById.place.name, 'Original');
    assert.equal(editor.placeDirty, true);
    assert.equal(editor.statusText, 'Save failed');
    await editor.savePlaceDraft();
    assert.equal(calls, 1);
    assert.match(editor.saveError, /unknown outcome/i);
    parent.applyMutation(envelope(place({ name: 'Refreshed', notesHtml: 'Refresh' })));
    await nextTick();
    assert.equal(editor.placeDraft.notesHtml, 'Newer');
  }));
}

for (const outcome of [200, 400, 409, 500]) {
  test(`discarded Place late ${outcome} cannot contaminate a newer Region or Place session`, () => withApp(async ({ parent, editor, sidebar }) => {
    const reply = deferred();
    let calls = 0;
    globalThis.fetch = async () => { calls++; return reply.promise; };
    await editor.openPlaceEdit(parent.state.placesById.place);
    editor.placeDraft.name = 'Submitted';
    const saving = editor.savePlaceDraft();
    globalThis.placeConfirm = async () => true;
    await parent.editorSurface.closeActiveTarget();
    await editor.openEdit(region);
    await nextTick();
    assert.equal(editor.isSaving, false);
    reply.resolve(outcome === 200 ? saved(place({ name: 'Submitted' })) : new Response(JSON.stringify(outcome === 400
      ? { errors: { name: ['Old error'] } } : { code: 'mutation-base-stale' }), { status: outcome }));
    await saving;
    await nextTick();
    assert.equal(editor.regionDirty, false);
    assert.equal(editor.placeDirty, false);
    assert.equal(sidebar.hasAnyDraftChanges, false);
    assert.equal(editor.saveError, null);
    assert.deepEqual(editor.fieldErrors('name'), []);
    assert.equal(parent.state.placesById.place.name, 'Original');
    await editor.openPlaceEdit(parent.state.placesById.place);
    assert.equal(editor.placeDirty, false);
    assert.equal(calls, 1);
  }));
}

// An ordinary update has no order slice; a concurrent move can change the server's source before this PUT.
for (const concurrentSource of [false, true]) {
  test(`Place complete update accepts its authoritative lifecycle order footprint: ${concurrentSource}`, () => withApp(async ({ parent, editor }) => {
    parent.state.regionsById.source = { ...region, id: 'source', name: 'Live source' };
    parent.state.regionOrder.push('source');
    if (concurrentSource) parent.state.placeOrderByRegionId.region.push('stale-client-member');
    await editor.openPlaceEdit(parent.state.placesById.place);
    editor.placeDraft.name = 'Submitted';
    editor.placeDraft.address = 'New address';
    const result = envelope(place({ name: 'Submitted', address: 'New address' }));
    // Neither the changed source nor concurrent/stale vector membership is client comparison evidence.
    result.affected.placeOrdersByRegionId = concurrentSource ? { source: [], region: ['concurrent-place', 'place'] } : {};
    const calls = [];
    globalThis.fetch = async (_url, init) => { calls.push(init); return new Response(JSON.stringify(result)); };
    await editor.savePlaceDraft();
    await nextTick();
    assert.equal(calls.length, 1);
    assert.equal(calls[0].method, 'PUT');
    assert.equal(parent.state.placesById.place.name, 'Submitted');
    assert.equal(editor.placeDraftState.baseline.address, 'New address');
    assert.equal(editor.placeDraftState.uncertain.value, false);
    assert.equal(editor.placeDirty, false);
    if (concurrentSource) assert.deepEqual(parent.state.placeOrderByRegionId, { region: ['place'], source: [] });
    assert.match(editor.statusText, /^Place saved/);
  }));
}

test('Place coordinate picker Cancel/Done and combined scalar/address/move save retain one complete PUT', () => withApp(async ({ parent, editor }) => {
  const calls = [];
  parent.state.regionsById.target = { ...region, id: 'target', name: 'Target' };
  parent.state.regionOrder.push('target');
  await editor.openPlaceEdit(parent.state.placesById.place);
  editor.pickPlaceCoordinate();
  globalThis.placeMapWork.onPicked({ latitude: 30, longitude: 40 });
  globalThis.placeConfirm = async () => true;
  await parent.editorSurface.cancelMapWork();
  assert.equal(editor.placeDraft.latitude, '10');
  assert.equal(editor.placeDirty, false);
  editor.pickPlaceCoordinate();
  globalThis.placeMapWork.onPicked({ latitude: 30, longitude: 40 });
  parent.applyMutation(envelope(place({ location: { latitude: 35, longitude: 45 } })));
  await nextTick();
  await parent.editorSurface.finishMapWork();
  assert.equal(editor.placeDraft.latitude, 30);
  editor.placeDraft.name = 'Combined';
  editor.placeDraft.address = 'Combined address';
  editor.placeDraft.regionId = 'target';
  // Mirror the reader's legitimate move/coordinate effects, including authoritative orders beyond stale client membership.
  const result = envelope(place({ regionId: 'target', name: 'Combined', address: 'Combined address',
    resolvedFeatureName: null, resolvedFeatureType: 'museum', location: { latitude: 30, longitude: 40 } }));
  result.affected.placeOrdersByRegionId = { region: [], target: ['concurrent-place', 'place'] };
  result.affected.segments = [{ id: 'segment', tripId: 'trip', fromPlaceId: 'place', toPlaceId: null,
    waypointPlaceIds: [], waypointRouteVertexIndices: [], mode: 'walk', transportProfileId: null, hasCustomRoute: false,
    estimatedDistanceKm: null, estimatedDurationMinutes: null, estimatedDurationSource: 'Automatic', notesHtml: '',
    route: null, effectiveRoute: null, originalRouteToken: 'refreshed-route', aggregateConcurrencyToken: 'refreshed-aggregate', displayOrder: 1,
    capabilities: { canEdit: true, canDelete: true }, originalValues: { notesHtml: null } }];
  result.affected.segmentOrder = ['concurrent-segment', 'segment'];
  result.affected.visitProgress = { totalPlaces: 1, visitedPlaces: 0, percentVisited: 0,
    placeSummariesByPlaceId: { place: result.data.visitSummary }, historyRows: [{ visitId: 'visit', placeId: 'place',
      regionId: 'target', startedAt: '2026-10-22T00:00:00Z', endedAt: null, durationMinutes: null }] };
  globalThis.fetch = async (url, init) => { calls.push([url, init]); return new Response(JSON.stringify(result)); };
  await editor.savePlaceDraft();
  await nextTick();
  assert.equal(calls.length, 1);
  assert.equal(calls[0][0], '/api/trips/trip/editor/places/place');
  assert.equal(calls[0][1].method, 'PUT');
  assert.deepEqual(JSON.parse(calls[0][1].body), { regionId: 'target', name: 'Combined', notesHtml: '', address: 'Combined address',
    location: { latitude: 30, longitude: 40 }, iconName: 'marker', markerColor: 'bg-blue', reverseGeocode: false });
  assert.equal(editor.placeDirty, false);
  assert.deepEqual(parent.state.placeOrderByRegionId, { region: [], target: ['place'] });
  assert.equal(parent.state.segmentsById.segment.aggregateConcurrencyToken, 'refreshed-aggregate');
  assert.deepEqual(parent.state.segmentOrder, ['segment']);
  assert.deepEqual(parent.state.visitProgress, result.affected.visitProgress);
  assert.equal(parent.state.placesById.place.resolvedFeatureType, 'museum');
  assert.match(editor.statusText, /^Place saved/);
}));

test('Place map and search create keep the single existing POST and creation draft separation', () => withApp(async ({ parent, editor }) => {
  await editor.openPlaceCreate(region);
  assert.equal(editor.placeDraft.latitude, 10);
  assert.equal(editor.placeDraft.longitude, 20);
  globalThis.placeConfirm = async () => true;
  const result = { name: 'Search place', displayName: 'Search place, City', address: 'Search address', latitude: 11, longitude: 22 };
  assert.equal(await editor.openPlaceCreateFromSearch(region, result), true);
  assert.equal(editor.placeDraft.name, 'Search place');
  assert.equal(editor.draft.id, null);
  assert.equal(editor.areaDraft.id, null);
  const calls = [];
  globalThis.fetch = async (url, init) => { calls.push([url, init]); const result = envelope(place({ name: 'Search place', address: 'Search address', location: { latitude: 11, longitude: 22 } })); result.affected.placeOrdersByRegionId.region = ['place']; return new Response(JSON.stringify(result)); };
  await editor.savePlaceDraft();
  await nextTick();
  assert.equal(calls.length, 1);
  assert.equal(calls[0][0], '/api/trips/trip/editor/regions/region/places');
  assert.equal(calls[0][1].method, 'POST');
  assert.equal(editor.placeDraft.id, 'place');
  assert.equal(editor.placeDirty, false);
}));

test('discarded same-Place reply cannot release a newer pending save or change an Area session', () => withApp(async ({ parent, editor }) => {
  const first = deferred();
  const second = deferred();
  let calls = 0;
  globalThis.fetch = async () => (++calls === 1 ? first.promise : second.promise);
  await editor.openPlaceEdit(parent.state.placesById.place);
  editor.placeDraft.name = 'Old session';
  const oldSave = editor.savePlaceDraft();
  globalThis.placeConfirm = async () => true;
  await parent.editorSurface.closeActiveTarget();
  await editor.openPlaceEdit(parent.state.placesById.place);
  editor.placeDraft.name = 'New session';
  const newSave = editor.savePlaceDraft();
  first.resolve(saved(place({ name: 'Old session' })));
  await oldSave;
  await nextTick();
  assert.equal(editor.isSaving, true);
  assert.equal(editor.placeDraft.name, 'New session');
  assert.equal(parent.state.placesById.place.name, 'Original');
  second.resolve(saved(place({ name: 'New session' })));
  await newSave;
  await nextTick();
  assert.equal(editor.isSaving, false);
  assert.equal(editor.placeDirty, false);
  await parent.editorSurface.closeActiveTarget();
  await editor.openAreaCreate(region);
  assert.equal(editor.placeDirty, false);
  assert.equal(editor.areaDraft.regionId, 'region');
  assert.equal(calls, 2);
}));
