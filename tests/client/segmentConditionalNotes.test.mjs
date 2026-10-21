import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { parse, compileScript, compileTemplate } from '@vue/compiler-sfc';
import { build } from 'esbuild';
import { createRenderer, nextTick, proxyRefs } from 'vue';

// Mount the actual App -> TripSidebar -> SegmentManager queue; substitute only map, DOM/Quill and unrelated leaves.
const bundle = await build({ stdin: { contents: 'export { default } from "./App.vue";', loader: 'ts',
  resolveDir: 'ClientApps/trip-editor/src' }, bundle: true, write: false, format: 'esm', platform: 'node', external: ['vue'],
  plugins: [{ name: 'segment-parent-tree', setup(builder) {
    builder.onLoad({ filter: /\.vue$/ }, async args => {
      if (/SegmentEditorSurface\.vue$/.test(args.path)) return { contents: `export default { setup(_, context) {
        context.expose({ focusNotes() {}, focusRouteAction: () => false }); return () => null;
      } };` };
      if (!/(?:App|TripSidebar|SegmentManager)\.vue$/.test(args.path)) return { contents: 'export default { render: () => null };' };
      const { descriptor } = parse(await readFile(args.path, 'utf8'), { filename: args.path });
      const script = compileScript(descriptor, { id: args.path, genDefaultAs: '__component' });
      const template = compileTemplate({ source: descriptor.template.content, filename: args.path, id: args.path,
        compilerOptions: { bindingMetadata: script.bindings } });
      return { loader: 'ts', contents: `${script.content}\n${template.code}\n__component.render = render;
        const setup = __component.setup;
        __component.setup = (props, context) => {
          const bindings = setup(props, context);
          globalThis.captureSegmentComponent(${JSON.stringify(args.path.split('/').at(-1))}, bindings);
          return bindings;
        }; export default __component;` };
    });
    builder.onLoad({ filter: /leafletAdapter\.ts$/ }, () => ({ contents: `export const createTripEditorMap = () => ({
      render() {}, dispose() {}, clearSearchPreview() {}, selectPlace() {}, selectSegment() {}, setSegmentPresentation() {}, setSegmentDraftPreview() {},
      setPlaceDraftPreview() {}, setSegmentRouteWorkState() {},
      startSegmentRouteWork: options => { globalThis.segmentMapWork = options; return () => {}; } });
      export const hasAnyGeometry = () => true; export const hasSavedTripView = () => false;
      export const canFocusActiveEntity = () => true;` }));
    builder.onLoad({ filter: /notesHtml\.ts$/ }, () => ({ contents: 'export const normalizeNotesHtml = value => value ?? "";' }));
    builder.onLoad({ filter: /useConfirmDialog\.ts$/ }, () => ({ contents: `export const confirm = options => globalThis.segmentConfirm(options);
      export const disposeConfirmDialogHost = () => {}; export const setConfirmDialogFocusFallback = () => {};` }));
  } }] });
const code = bundle.outputFiles[0].text.replaceAll('from "vue"', `from "${import.meta.resolve('vue')}"`);
const actualApp = (await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`)).default;
const route = { type: 'LineString', coordinates: [[0, 0], [1, 1], [2, 2]] };
const segment = (overrides = {}) => {
  const value = { id: 'segment', tripId: 'trip', fromPlaceId: 'from', toPlaceId: 'to', waypointPlaceIds: ['waypoint'],
    waypointRouteVertexIndices: [1], mode: 'walk', transportProfileId: 'profile', hasCustomRoute: true,
    estimatedDistanceKm: 20, estimatedDurationMinutes: 90, estimatedDurationSource: 'Manual', notesHtml: '',
    route, effectiveRoute: route, aggregateConcurrencyToken: 'original-aggregate', displayOrder: 1,
    capabilities: { canEdit: true, canDelete: true }, ...overrides };
  value.originalValues ??= { notesHtml: value.notesHtml || null };
  return value;
};
const envelope = data => ({ success: true, data, affected: { metadata: null, regions: [], places: [], areas: [],
  segments: data ? [data] : [], tags: [], placeOrdersByRegionId: {}, areaOrdersByRegionId: {} },
  deletedIds: { regions: [], places: [], areas: [], segments: [], tags: [] }, warnings: [] });
const saved = data => new Response(JSON.stringify(envelope(data)));
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; };
const withApp = async action => {
  const previous = { window: globalThis.window, document: globalThis.document, fetch: globalThis.fetch, CSS: globalThis.CSS,
    segmentConfirm: globalThis.segmentConfirm, captureSegmentComponent: globalThis.captureSegmentComponent };
  const captured = {};
  globalThis.captureSegmentComponent = (name, bindings) => { captured[name] = proxyRefs(bindings); };
  globalThis.segmentConfirm = async () => false;
  globalThis.CSS = { escape: value => value };
  globalThis.document = { getElementById: () => null, querySelector: () => null };
  globalThis.window = { matchMedia: () => ({ matches: false, addEventListener() {}, removeEventListener() {} }),
    Sortable: { create: () => ({ destroy() {} }) } };
  const initial = { tripId: 'trip', metadata: { name: 'Trip', notesHtml: '', updatedAt: '2026-10-21T00:00:00Z' },
    regionsById: {}, regionOrder: [], areasById: {}, areaOrderByRegionId: {},
    placesById: Object.fromEntries(['from', 'waypoint', 'to'].map((id, i) => [id, { id, name: id, location: { longitude: i, latitude: i } }])),
    placeOrderByRegionId: {}, segmentsById: { segment: segment() }, segmentOrder: ['segment'], tagsBySlug: {}, tagOrder: [],
    options: { transportModes: [{ value: 'walk', label: 'Walk' }] }, permissions: { canEditSegments: true }, visitProgress: {} };
  globalThis.fetch = async () => new Response(JSON.stringify(initial));
  const renderer = createRenderer({ createElement: tag => ({ tagName: tag.toUpperCase(), style: {},
    addEventListener() {}, removeEventListener() {}, setAttribute() {}, removeAttribute() {} }), createText: () => ({}), createComment: () => ({}),
    insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => ({}), nextSibling: () => null });
  const app = renderer.createApp(actualApp, { config: { editorEndpoint: '/api/trips/trip/editor', antiforgeryToken: 'anti', tripIndexUrl: '/User/Trip' } });
  app.mount({});
  try {
    for (let tick = 0; tick < 20 && !captured['SegmentManager.vue']; tick++) await new Promise(done => setImmediate(done));
    const parent = captured['App.vue'];
    const editor = captured['SegmentManager.vue'];
    assert.ok(editor, 'actual production Segment owner mounted through App and TripSidebar');
    parent.editorSurface.clearActiveTarget();
    await action({ parent, editor, sidebar: captured['TripSidebar.vue'] });
  } finally {
    await nextTick();
    await nextTick();
    captured['App.vue']?.editorSurface.clearActiveTarget();
    app.unmount();
    await nextTick();
    Object.assign(globalThis, previous);
    delete globalThis.segmentMapWork;
  }
};

test('Notes-only uses pinned nullable evidence and PATCH without route fields across parent refresh', () => withApp(async ({ parent, editor }) => {
  const calls = [];
  globalThis.fetch = async (url, init) => { calls.push([url, init]); return saved(segment({ notesHtml: 'Mine', aggregateConcurrencyToken: 'saved-token' })); };
  await editor.openEdit(parent.state.segmentsById.segment);
  editor.draft.notesHtml = 'Mine';
  parent.applyMutation(envelope(segment({ notesHtml: 'Other device' })));
  await nextTick();
  assert.equal(editor.draft.notesHtml, 'Mine');
  await editor.saveDraft();
  assert.equal(calls.length, 1);
  assert.equal(calls[0][0], '/api/trips/trip/editor/segments/segment/conditional');
  assert.equal(calls[0][1].method, 'PATCH');
  assert.equal(calls[0][1].credentials, 'same-origin');
  assert.equal(calls[0][1].headers.RequestVerificationToken, 'anti');
  assert.deepEqual(JSON.parse(calls[0][1].body), { expected: { notesHtml: null }, changes: { notesHtml: 'Mine' } });
  await nextTick();
  assert.equal(editor.isDirty, false);
  assert.equal(editor.draft.aggregateConcurrencyToken, 'saved-token');
}));

for (const newer of ['Newer unsaved', '']) {
  test(`delayed Notes acknowledgement retains newer desired content/reversal ${JSON.stringify(newer)}`, () => withApp(async ({ parent, editor, sidebar }) => {
    const reply = deferred();
    const calls = [];
    globalThis.fetch = async (_url, init) => { calls.push(JSON.parse(init.body)); return reply.promise; };
    await editor.openEdit(parent.state.segmentsById.segment);
    editor.draft.notesHtml = 'Submitted';
    const saving = editor.saveDraft();
    editor.draft.notesHtml = newer;
    reply.resolve(saved(segment({ notesHtml: 'Submitted', aggregateConcurrencyToken: 'after-notes' })));
    await saving;
    await nextTick();
    assert.equal(parent.state.segmentsById.segment.notesHtml, 'Submitted');
    assert.equal(editor.draft.notesHtml, newer);
    assert.equal(editor.isDirty, true);
    assert.equal(sidebar.hasAnyDraftChanges, true);
    globalThis.fetch = async (_url, init) => { calls.push(JSON.parse(init.body)); return saved(segment({ notesHtml: newer })); };
    await editor.saveDraft();
    assert.deepEqual(calls[1], { expected: { notesHtml: 'Submitted' }, changes: { notesHtml: newer } });
  }));
}

test('route intent begun during Notes PATCH retains its old dependency token and requires explicit review', () => withApp(async ({ parent, editor }) => {
  const reply = deferred();
  let calls = 0;
  globalThis.fetch = async () => { calls++; return reply.promise; };
  await editor.openEdit(parent.state.segmentsById.segment);
  editor.draft.notesHtml = 'Submitted';
  const saving = editor.saveDraft();
  editor.draft.estimatedDurationMinutes = 120;
  editor.previewRouteProposal({ proposalId: 'proposal', protectedContext: 'original-proof',
    geometry: [{ longitude: 0, latitude: 0 }, { longitude: 2, latitude: 2 }], waypointIndices: [0, 1], durationSeconds: 500 });
  reply.resolve(saved(segment({ notesHtml: 'Submitted', aggregateConcurrencyToken: 'after-notes' })));
  await saving;
  await nextTick();
  assert.equal(editor.draft.estimatedDurationMinutes, 120);
  assert.equal(editor.draft.aggregateConcurrencyToken, 'original-aggregate');
  assert.equal(editor.routeProposalDraft.hasProposal.value, true);
  assert.equal(editor.isDirty, true);
  await editor.saveDraft();
  assert.equal(calls, 1, 'no implicit aggregate rebase/retry');
  assert.match(editor.saveError, /review/i);
  editor.resetDraft();
  assert.equal(editor.draft.aggregateConcurrencyToken, 'after-notes');
}));

for (const failure of ['500', '408', 'network', 'incomplete']) {
  test(`undiscarded ${failure} holds a newer reversal and prevents blind replay`, () => withApp(async ({ parent, editor, sidebar }) => {
    const reply = deferred();
    let calls = 0;
    globalThis.fetch = async () => { calls++; const response = await reply.promise; if (!response) throw new TypeError('Lost reply'); return response; };
    await editor.openEdit(parent.state.segmentsById.segment);
    editor.draft.notesHtml = 'Submitted';
    const saving = editor.saveDraft();
    editor.draft.notesHtml = '';
    reply.resolve(failure === 'network' ? null : failure === 'incomplete' ? saved({ id: 'segment' }) : new Response('{}', { status: Number(failure) }));
    await saving;
    parent.applyMutation(envelope(segment({ notesHtml: 'Submitted' })));
    await nextTick();
    assert.equal(editor.draft.notesHtml, '');
    assert.equal(editor.isDirty, true);
    assert.equal(sidebar.hasAnyDraftChanges, true);
    await editor.saveDraft();
    assert.equal(calls, 1);
    assert.match(editor.saveError, /unknown outcome/);
    assert.equal(await parent.editorSurface.closeActiveTarget(), false);
  }));
}

for (const [outcome, id] of [[500, 'other'], [200, 'other'], [200, 'segment']]) {
  test(`discarded late ${outcome} cannot contaminate a newer completed save of ${id}`, () => withApp(async ({ parent, editor }) => {
    const old = deferred();
    let calls = 0;
    globalThis.fetch = async () => (++calls === 1 ? old.promise : saved(segment({ id, notesHtml: 'New save' })));
    await editor.openEdit(parent.state.segmentsById.segment);
    editor.draft.notesHtml = 'Old submission';
    const saving = editor.saveDraft();
    globalThis.segmentConfirm = async () => true;
    await parent.editorSurface.closeActiveTarget();
    parent.applyMutation(envelope(segment({ id })));
    await nextTick();
    await editor.openEdit(parent.state.segmentsById[id]);
    editor.draft.notesHtml = 'New save';
    await editor.saveDraft();
    assert.equal(calls, 2, 'discard retires the old session saving gate');
    old.resolve(outcome === 200 ? saved(segment({ notesHtml: 'Old submission' })) : new Response('{}', { status: 500 }));
    await saving;
    await nextTick();
    assert.equal(editor.draft.id, id);
    assert.equal(editor.draft.notesHtml, 'New save');
    assert.equal(editor.isDirty, false);
    assert.equal(editor.saveError, null);
    assert.equal(parent.state.segmentsById.segment.notesHtml, id === 'segment' ? 'New save' : '', 'retired response cannot replace current parent slices');
  }));
}

test('400/409 remain correctable with pinned original; absent original/unsupported route fails closed', () => withApp(async ({ parent, editor }) => {
  const calls = [];
  globalThis.fetch = async (url, init) => { calls.push([url, init.method, JSON.parse(init.body)]); return new Response(JSON.stringify({
    code: 'mutation-base-stale', detail: 'Review current Notes.', errors: { notesHtml: ['Check Notes.'] } }), { status: calls.length === 1 ? 400 : 409 }); };
  await editor.openEdit(parent.state.segmentsById.segment);
  editor.draft.notesHtml = 'Mine';
  await editor.saveDraft();
  assert.deepEqual(editor.fieldErrors('notesHtml'), ['Check Notes.']);
  editor.draft.notesHtml = 'Corrected';
  await editor.saveDraft();
  assert.deepEqual(calls[1][2], { expected: { notesHtml: null }, changes: { notesHtml: 'Corrected' } });
  globalThis.fetch = async (url, init) => { calls.push([url, init.method]); return new Response('{}', { status: 404 }); };
  await editor.saveDraft();
  assert.equal(calls[2][1], 'PATCH');
  const missing = segment();
  delete missing.originalValues;
  parent.applyMutation(envelope(missing));
  await nextTick();
  editor.resetDraft();
  editor.draft.notesHtml = 'No evidence';
  await editor.saveDraft();
  assert.equal(calls.length, 3);
  assert.match(editor.saveError, /Original Segment Notes/);
}));

test('combined Notes/manual route/proposal remains one aggregate PUT and keeps original confirmation authority', () => withApp(async ({ parent, editor }) => {
  const calls = [];
  globalThis.fetch = async (url, init) => { calls.push([url, init, JSON.parse(init.body)]); return saved(segment({ notesHtml: 'Combined', estimatedDurationMinutes: 120 })); };
  await editor.openEdit(parent.state.segmentsById.segment);
  editor.draft.notesHtml = 'Combined';
  editor.draft.estimatedDurationMinutes = 120;
  editor.previewRouteProposal({ proposalId: 'proposal', protectedContext: 'proof',
    geometry: [{ longitude: 0, latitude: 0 }, { longitude: 1, latitude: 1 }, { longitude: 2, latitude: 2 }], waypointIndices: [0, 1, 2] });
  await editor.saveDraft();
  assert.equal(calls.length, 1);
  assert.equal(calls[0][0], '/api/trips/trip/editor/segments/segment');
  assert.equal(calls[0][1].method, 'PUT');
  assert.equal(calls[0][2].aggregateConcurrencyToken, 'original-aggregate');
  assert.equal(calls[0][2].notesHtml, 'Combined');
  assert.equal(calls[0][2].estimatedDurationMinutes, 120);
  assert.deepEqual(calls[0][2].proposal, { proposalId: 'proposal', protectedContext: 'proof', manualDurationOverride: true });
}));

test('route-clear confirmation resubmits the same complete aggregate and retains later Notes', () => withApp(async ({ parent, editor }) => {
  const calls = [];
  globalThis.fetch = async (url, init) => {
    calls.push([url, init, JSON.parse(init.body)]);
    return calls.length === 1 ? new Response(JSON.stringify({ code: 'segment-route-clear-confirmation-required',
      currentSegment: segment(), warning: 'Clear custom route?', operation: 'update' }), {
      status: 409, headers: { 'X-Wayfarer-Clear-Route-Confirmation': 'clear-approval' }
    }) : saved(segment({ fromPlaceId: 'waypoint', route: null, effectiveRoute: null, notesHtml: 'Submitted' }));
  };
  await editor.openEdit(parent.state.segmentsById.segment);
  editor.draft.notesHtml = 'Submitted';
  editor.draft.fromPlaceId = 'waypoint';
  globalThis.segmentConfirm = async () => { editor.draft.notesHtml = 'Newer during confirmation'; return true; };
  await editor.saveDraft();
  assert.equal(calls.length, 2);
  assert.deepEqual(calls.map(call => call[1].method), ['PUT', 'PUT']);
  assert.deepEqual(calls[0][2], calls[1][2]);
  assert.equal(calls[1][1].headers['X-Wayfarer-Clear-Route-Confirmation'], 'clear-approval');
  assert.equal(editor.draft.notesHtml, 'Newer during confirmation');
  assert.equal(editor.isDirty, true);
}));

test('explicit aggregate conflict reload pins its published Notes original for the next scalar edit', () => withApp(async ({ parent, editor }) => {
  const calls = [];
  const current = segment({ notesHtml: 'Server current', aggregateConcurrencyToken: 'current-token' });
  globalThis.fetch = async (url, init) => {
    calls.push([url, JSON.parse(init.body)]);
    return calls.length === 1 ? new Response(JSON.stringify({ code: 'segment-aggregate-stale', currentSegment: current,
      warning: 'Review current saved state.', operation: 'update' }), { status: 409 }) : saved(segment({ notesHtml: 'Reviewed scalar' }));
  };
  await editor.openEdit(parent.state.segmentsById.segment);
  editor.draft.estimatedDurationMinutes = 120;
  await editor.saveDraft();
  globalThis.segmentConfirm = async () => true;
  await editor.reloadCurrentSegment();
  editor.draft.notesHtml = 'Reviewed scalar';
  await editor.saveDraft();
  assert.match(calls[1][0], /\/conditional$/);
  assert.deepEqual(calls[1][1], { expected: { notesHtml: 'Server current' }, changes: { notesHtml: 'Reviewed scalar' } });
}));

for (const failure of ['500', 'incomplete']) {
  test(`unknown combined save ${failure} retains proposal/newer Notes without repeating a route mutation`, () => withApp(async ({ parent, editor }) => {
    const reply = deferred();
    let calls = 0;
    globalThis.fetch = async () => { calls++; return reply.promise; };
    await editor.openEdit(parent.state.segmentsById.segment);
    editor.draft.notesHtml = 'Submitted';
    editor.draft.estimatedDurationMinutes = 120;
    editor.previewRouteProposal({ proposalId: 'proposal', protectedContext: 'proof',
      geometry: [{ longitude: 0, latitude: 0 }, { longitude: 1, latitude: 1 }, { longitude: 2, latitude: 2 }], waypointIndices: [0, 1, 2] });
    const saving = editor.saveDraft();
    editor.draft.notesHtml = '';
    reply.resolve(failure === '500' ? new Response('{}', { status: 500 }) : saved({ id: 'segment' }));
    await saving;
    parent.applyMutation(envelope(segment({ notesHtml: 'Submitted', estimatedDurationMinutes: 120 })));
    await nextTick();
    assert.equal(editor.draft.notesHtml, '');
    assert.equal(editor.isDirty, true);
    assert.equal(editor.routeProposalDraft.hasProposal.value, true);
    await editor.saveDraft();
    assert.equal(calls, 1);
    assert.match(editor.saveError, /unknown outcome/);
  }));
}

test('pending complete saves keep a newer reversal dirty and pinned before any reply or parent refresh', () => withApp(async ({ parent, editor, sidebar }) => {
  const reply = deferred();
  globalThis.fetch = async () => reply.promise;
  await editor.openEdit(parent.state.segmentsById.segment);
  editor.draft.notesHtml = 'Submitted';
  editor.draft.estimatedDurationMinutes = 120;
  const saving = editor.saveDraft();
  editor.draft.notesHtml = '';
  editor.draft.estimatedDurationMinutes = 90;
  await nextTick();
  assert.equal(editor.isDirty, true, 'pending work remains an obligation even after a complete display reversal');
  assert.equal(sidebar.hasAnyDraftChanges, true);
  parent.applyMutation(envelope(segment({ notesHtml: 'Concurrent refresh', estimatedDurationMinutes: 180 })));
  await nextTick();
  assert.equal(editor.draft.notesHtml, '');
  reply.resolve(saved(segment({ notesHtml: 'Submitted', estimatedDurationMinutes: 120 })));
  await saving;
  await nextTick();
  assert.equal(editor.draft.notesHtml, '');
  assert.equal(editor.isDirty, true);
}));
