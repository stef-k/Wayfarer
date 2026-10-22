import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { parse, compileScript, compileTemplate } from '@vue/compiler-sfc';
import { build } from 'esbuild';
import { createRenderer, nextTick, proxyRefs, watch } from 'vue';

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
  segments: data ? [data] : [], tags: [], placeOrdersByRegionId: {}, areaOrdersByRegionId: {},
  regionOrder: null, segmentOrder: null, tagOrder: null, visitProgress: null, options: null },
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

for (const failure of ['500', '408', 'network', 'incomplete', 'incomplete affected']) {
  test(`undiscarded ${failure} holds a newer reversal and prevents blind replay`, () => withApp(async ({ parent, editor, sidebar }) => {
    const reply = deferred();
    let calls = 0;
    globalThis.fetch = async () => { calls++; const response = await reply.promise; if (!response) throw new TypeError('Lost reply'); return response; };
    await editor.openEdit(parent.state.segmentsById.segment);
    editor.draft.notesHtml = 'Submitted';
    const saving = editor.saveDraft();
    editor.draft.notesHtml = '';
    const incompleteAffected = envelope(segment({ notesHtml: 'Submitted', aggregateConcurrencyToken: 'committed-token' }));
    incompleteAffected.affected.segments = [];
    reply.resolve(failure === 'network' ? null : failure === 'incomplete' ? saved({ id: 'segment' })
      : failure === 'incomplete affected' ? new Response(JSON.stringify(incompleteAffected)) : new Response('{}', { status: Number(failure) }));
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

test('combined Notes/manual route/proposal remains one aggregate PUT and keeps original confirmation authority', t => withApp(async ({ parent, editor }) => {
  const calls = [];
  let applied = 0;
  t.after(watch(() => parent.state, () => applied++, { flush: 'sync' }));
  globalThis.fetch = async (url, init) => {
    calls.push([url, init, JSON.parse(init.body)]);
    const result = envelope(segment({ notesHtml: 'Combined', estimatedDurationMinutes: 120, aggregateConcurrencyToken: 'complete-token' }));
    // Equal JSON objects remain authoritative when their property order differs between data and affected.
    result.affected.segments = [Object.fromEntries(Object.entries(result.data).reverse())];
    result.affected.segments[0].capabilities = { canDelete: true, canEdit: true };
    result.affected.segments[0].route = { coordinates: route.coordinates, type: 'LineString' };
    return new Response(JSON.stringify(result));
  };
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
  await nextTick();
  assert.equal(applied, 1);
  assert.equal(parent.state.segmentsById.segment.notesHtml, 'Combined');
  assert.equal(editor.draft.aggregateConcurrencyToken, 'complete-token');
  assert.equal(editor.routeProposalDraft.hasProposal.value, false);
  assert.equal(editor.isDirty, false);
  assert.match(editor.statusText, /^Saved/);
}));

test('route-clear confirmation resubmits the same complete aggregate and retains later Notes', t => withApp(async ({ parent, editor }) => {
  const calls = [];
  let applied = 0;
  t.after(watch(() => parent.state, () => applied++, { flush: 'sync' }));
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
  await nextTick();
  assert.equal(applied, 1);
  assert.equal(parent.state.segmentsById.segment.notesHtml, 'Submitted');
  assert.equal(parent.state.segmentsById.segment.route, null);
  assert.equal(editor.routeProposalDraft.hasProposal.value, false);
  assert.ok(editor.lastSavedAt, 'the acknowledged aggregate is saved while newer Notes remain dirty');
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

// A complete data DTO cannot acknowledge the parent's saved state without its authoritative affected Segment.
for (const confirmed of [false, true]) {
  test(`F1 ${confirmed ? 'route-clear confirmed' : 'ordinary'} complete 200 missing affected Segment retains the entire uncertain draft`, t => withApp(async ({ parent, editor, sidebar }) => {
    const calls = [];
    const committed = segment({ notesHtml: 'Submitted', estimatedDurationMinutes: 120,
      fromPlaceId: confirmed ? 'waypoint' : 'from', aggregateConcurrencyToken: 'committed-token',
      ...(confirmed ? { route: null, effectiveRoute: null, waypointRouteVertexIndices: [null] } : {}) });
    globalThis.fetch = async (url, init) => {
      calls.push([url, init, JSON.parse(init.body)]);
      if (confirmed && calls.length === 1) return new Response(JSON.stringify({ code: 'segment-route-clear-confirmation-required',
        currentSegment: segment(), warning: 'Clear custom route?', operation: 'update' }), {
        status: 409, headers: { 'X-Wayfarer-Clear-Route-Confirmation': 'clear-approval' }
      });
      const incomplete = envelope(committed);
      incomplete.affected.segments = [];
      return new Response(JSON.stringify(incomplete), { status: 200 });
    };
    globalThis.segmentConfirm = async () => true;
    await editor.openEdit(parent.state.segmentsById.segment);
    editor.draft.notesHtml = 'Submitted';
    editor.draft.estimatedDurationMinutes = 120;
    if (confirmed) editor.draft.fromPlaceId = 'waypoint';
    editor.draft.waypointRows[0].routeVertexIndex = 2;
    editor.previewRouteProposal({ proposalId: 'pending-proposal', protectedContext: 'original-proof',
      geometry: [{ longitude: 0, latitude: 0 }, { longitude: 1, latitude: 1 }, { longitude: 2, latitude: 2 }],
      waypointIndices: [0, 1, 2], durationSeconds: 500 });
    const before = JSON.parse(JSON.stringify({ draft: editor.draft, baseline: editor.persistedBaseline,
      proposalRequest: editor.routeProposalDraft.buildRequest() }));
    const parentBefore = parent.state;
    await editor.saveDraft();
    await nextTick();
    t.diagnostic(JSON.stringify({ dirty: editor.isDirty, error: editor.saveError, proposal: editor.routeProposalDraft.hasProposal.value,
      parentNotes: parent.state.segmentsById.segment.notesHtml, draftNotes: editor.draft.notesHtml,
      draftToken: editor.draft.aggregateConcurrencyToken, status: editor.statusText }));
    assert.equal(editor.isDirty, true, 'incomplete complete-save acknowledgement must retain the dirty hold');
    assert.equal(sidebar.hasAnyDraftChanges, true);
    assert.match(editor.saveError, /incomplete.*unknown outcome/i);
    assert.equal(editor.lastSavedAt, null, 'an incomplete acknowledgement never marks Saved');
    assert.doesNotMatch(editor.statusText, /^Saved/);
    assert.equal(parent.state, parentBefore, 'no parent slice is applied before full acknowledgement validation');
    assert.deepEqual(JSON.parse(JSON.stringify(editor.draft)), before.draft, 'Notes, manual duration, route, waypoints and old token survive');
    assert.deepEqual(JSON.parse(JSON.stringify(editor.persistedBaseline)), before.baseline);
    assert.equal(editor.routeProposalDraft.hasProposal.value, true);
    assert.deepEqual(editor.routeProposalDraft.buildRequest(), before.proposalRequest);
    assert.equal(calls.length, confirmed ? 2 : 1);
    assert.ok(calls.every(call => call[1].method === 'PUT'));
    if (confirmed) {
      assert.deepEqual(calls[0][2], calls[1][2], 'confirmation resubmits the original complete aggregate');
      assert.equal(calls[1][1].headers['X-Wayfarer-Clear-Route-Confirmation'], 'clear-approval');
    }
    await editor.saveDraft();
    assert.equal(calls.length, confirmed ? 2 : 1, 'uncertain attempt blocks blind retry');
    assert.match(editor.saveError, /unknown outcome/);
    globalThis.segmentConfirm = async () => false;
    assert.equal(await parent.editorSurface.closeActiveTarget(), false, 'the uncertain draft still fences navigation');
  }));
}

// Exercise malformed acknowledgements at the same mounted product boundary, including coherent but foreign data.
for (const [name, corrupt] of [
  ['absent affected slice', result => { delete result.affected.segments; }],
  ['affected identity', result => { result.affected.segments[0].id = 'other'; }],
  ['affected Notes', result => { result.affected.segments[0].notesHtml = 'Different'; }],
  ['affected route', result => { result.affected.segments[0].route.coordinates[1] = [3, 3]; }],
  ['affected waypoints', result => { result.affected.segments[0].waypointRouteVertexIndices = [2]; }],
  ['affected duration', result => { result.affected.segments[0].estimatedDurationMinutes = 180; }],
  ['affected token', result => { result.affected.segments[0].aggregateConcurrencyToken = 'different-token'; }],
  ['foreign Trip', result => { result.data.tripId = result.affected.segments[0].tripId = 'other-trip'; }],
  ['missing Notes original', result => { delete result.data.originalValues; delete result.affected.segments[0].originalValues; }],
  ['wrong Notes original', result => { result.data.originalValues = result.affected.segments[0].originalValues = { notesHtml: null }; }],
  ['missing parent-consumed slice', result => { delete result.affected.places; }],
  ['missing deleted IDs', result => { delete result.deletedIds; }],
  ['duplicate Segment order', result => { result.affected.segmentOrder = ['segment', 'segment']; }]
]) {
  test(`complete acknowledgement rejects ${name} before any adoption`, () => withApp(async ({ parent, editor }) => {
    let calls = 0;
    globalThis.fetch = async () => {
      calls++;
      const result = envelope(segment({ notesHtml: 'Submitted', estimatedDurationMinutes: 120, aggregateConcurrencyToken: 'committed-token' }));
      result.affected.segments = [structuredClone(result.data)];
      corrupt(result);
      return new Response(JSON.stringify(result));
    };
    await editor.openEdit(parent.state.segmentsById.segment);
    editor.draft.notesHtml = 'Submitted';
    editor.draft.estimatedDurationMinutes = 120;
    const parentBefore = parent.state;
    await editor.saveDraft();
    await nextTick();
    assert.equal(parent.state, parentBefore);
    assert.equal(editor.draft.notesHtml, 'Submitted');
    assert.equal(editor.draft.estimatedDurationMinutes, 120);
    assert.equal(editor.draft.aggregateConcurrencyToken, 'original-aggregate');
    assert.equal(editor.isDirty, true);
    assert.equal(editor.lastSavedAt, null);
    assert.match(editor.saveError, /incomplete.*unknown outcome/i);
    await editor.saveDraft();
    assert.equal(calls, 1);
  }));
}

// R1: malformed create order must not retire the authored draft/proposal or duplicate actual sidebar rows.
for (const repeatedId of ['created', 'segment']) {
  test(`R1 create duplicate ${repeatedId} order retains the entire uncertain draft`, t => withApp(async ({ parent, editor, sidebar }) => {
    const calls = [];
    let applied = 0;
    t.after(watch(() => parent.state, () => applied++, { flush: 'sync' }));
    globalThis.fetch = async (url, init) => {
      calls.push([url, init, JSON.parse(init.body)]);
      const result = envelope(segment({ id: 'created', notesHtml: 'Created', estimatedDurationMinutes: 120,
        aggregateConcurrencyToken: 'created-token' }));
      result.affected.segmentOrder = ['segment', 'created', repeatedId];
      return new Response(JSON.stringify(result));
    };
    await editor.openCreate();
    Object.assign(editor.draft, { fromPlaceId: 'from', toPlaceId: 'to', mode: 'walk', notesHtml: 'Created',
      estimatedDurationMinutes: 120, estimatedDurationSource: 'Manual', route: structuredClone(route),
      waypointRows: [{ clientId: 'create-waypoint', placeId: 'waypoint', routeVertexIndex: 1 }] });
    editor.previewRouteProposal({ proposalId: 'create-proposal', protectedContext: 'create-proof',
      geometry: [{ longitude: 0, latitude: 0 }, { longitude: 1, latitude: 1 }, { longitude: 2, latitude: 2 }],
      waypointIndices: [0, 1, 2], durationSeconds: 500 });
    const before = JSON.parse(JSON.stringify({ draft: editor.draft, baseline: editor.persistedBaseline,
      proposalRequest: editor.routeProposalDraft.buildRequest() }));
    const parentBefore = parent.state;
    await editor.saveDraft();
    await nextTick();
    t.diagnostic(JSON.stringify({ repeatedId, applied, dirty: editor.isDirty, error: editor.saveError,
      order: [...parent.state.segmentOrder], proposal: editor.routeProposalDraft.hasProposal.value,
      draftId: editor.draft.id, draftToken: editor.draft.aggregateConcurrencyToken, status: editor.statusText }));
    assert.equal(editor.isDirty, true, 'duplicate create order must retain the unknown-outcome hold');
    assert.equal(parent.state, parentBefore, 'invalid order never reaches the parent mutation owner');
    assert.equal(applied, 0);
    assert.deepEqual(sidebar.orderedSegments.map(value => value.id), ['segment']);
    assert.deepEqual(JSON.parse(JSON.stringify(editor.draft)), before.draft);
    assert.deepEqual(JSON.parse(JSON.stringify(editor.persistedBaseline)), before.baseline);
    assert.equal(editor.draft.id, null);
    assert.equal(editor.draft.aggregateConcurrencyToken, null);
    assert.equal(editor.routeProposalDraft.hasProposal.value, true);
    assert.deepEqual(editor.routeProposalDraft.buildRequest(), before.proposalRequest);
    assert.equal(sidebar.hasAnyDraftChanges, true);
    assert.equal(editor.lastSavedAt, null);
    assert.doesNotMatch(editor.statusText, /^Saved/);
    assert.match(editor.saveError, /incomplete.*unknown outcome/i);
    assert.equal(calls.length, 1);
    assert.equal(calls[0][0], '/api/trips/trip/editor/segments');
    assert.equal(calls[0][1].method, 'POST');
    assert.deepEqual(calls[0][2], before.proposalRequest);
    await editor.saveDraft();
    assert.equal(calls.length, 1, 'unknown create must never resend automatically or on another Save');
    globalThis.segmentConfirm = async () => false;
    assert.equal(await parent.editorSurface.closeActiveTarget(), false, 'unknown create still fences navigation');
  }));
}

// An authoritative order may differ from stale client membership, including concurrently added/removed Segments.
for (const creating of [false, true]) {
  test(`${creating ? 'create' : 'update'} accepts a unique supplied order beyond stale parent membership exactly once`, t => withApp(async ({ parent, editor }) => {
    let calls = 0;
    let applied = 0;
    t.after(watch(() => parent.state, () => applied++, { flush: 'sync' }));
    const id = creating ? 'created' : 'segment';
    const result = envelope(segment({ id, notesHtml: 'Submitted', estimatedDurationMinutes: 120,
      aggregateConcurrencyToken: 'saved-token' }));
    result.affected.segmentOrder = ['concurrent', id];
    globalThis.fetch = async (_url, init) => {
      calls++;
      assert.equal(init.method, creating ? 'POST' : 'PUT');
      return new Response(JSON.stringify(result));
    };
    if (creating) await editor.openCreate();
    else await editor.openEdit(parent.state.segmentsById.segment);
    Object.assign(editor.draft, { fromPlaceId: 'from', toPlaceId: 'to', mode: 'walk', notesHtml: 'Submitted',
      estimatedDurationMinutes: 120, estimatedDurationSource: 'Manual' });
    await editor.saveDraft();
    await nextTick();
    assert.equal(calls, 1);
    assert.equal(applied, 1);
    assert.equal(editor.saveError, null);
    assert.equal(editor.isDirty, false);
    assert.equal(editor.draft.id, id);
    assert.equal(editor.draft.aggregateConcurrencyToken, 'saved-token');
    assert.equal(parent.state.segmentsById[id].notesHtml, 'Submitted');
    assert.deepEqual([...parent.state.segmentOrder], [id], 'parent retains its established filtering of unknown IDs');
    assert.match(editor.statusText, /^Saved/);
  }));
}

// Create shares the complete-save boundary, but its target identity and order must come from this acknowledgement.
for (const complete of [false, true]) {
  test(`create ${complete ? 'accepts a complete' : 'holds an incomplete'} affected Segment acknowledgement`, () => withApp(async ({ parent, editor }) => {
    let calls = 0;
    globalThis.fetch = async (_url, init) => {
      calls++;
      assert.equal(init.method, 'POST');
      const result = envelope(segment({ id: 'created', notesHtml: 'Created', waypointPlaceIds: [], waypointRouteVertexIndices: [],
        route: null, effectiveRoute: null, aggregateConcurrencyToken: 'created-token' }));
      result.affected.segmentOrder = ['segment', 'created'];
      if (!complete) result.affected.segments = [];
      return new Response(JSON.stringify(result));
    };
    await editor.openCreate();
    editor.draft.fromPlaceId = 'from';
    editor.draft.toPlaceId = 'to';
    editor.draft.mode = 'walk';
    editor.draft.notesHtml = 'Created';
    const parentBefore = parent.state;
    await editor.saveDraft();
    await nextTick();
    assert.equal(calls, 1);
    if (complete) {
      assert.equal(parent.state.segmentsById.created.notesHtml, 'Created');
      assert.deepEqual([...parent.state.segmentOrder], ['segment', 'created']);
      assert.equal(editor.draft.id, 'created');
      assert.equal(editor.draft.aggregateConcurrencyToken, 'created-token');
      assert.equal(editor.isDirty, false);
      assert.match(editor.statusText, /^Saved/);
    } else {
      assert.equal(parent.state, parentBefore);
      assert.equal(editor.draft.id, null);
      assert.equal(editor.draft.notesHtml, 'Created');
      assert.equal(editor.draft.aggregateConcurrencyToken, null);
      assert.equal(editor.isDirty, true);
      assert.equal(editor.lastSavedAt, null);
      assert.match(editor.saveError, /incomplete.*unknown outcome/i);
      await editor.saveDraft();
      assert.equal(calls, 1);
    }
  }));
}

test('discarded complete acknowledgement cannot release or mutate a newer pending save of the same Segment', () => withApp(async ({ parent, editor }) => {
  const old = deferred();
  const newer = deferred();
  let calls = 0;
  globalThis.fetch = async () => ++calls === 1 ? old.promise : newer.promise;
  await editor.openEdit(parent.state.segmentsById.segment);
  editor.draft.notesHtml = 'Old aggregate';
  editor.draft.estimatedDurationMinutes = 120;
  const oldSaving = editor.saveDraft();
  globalThis.segmentConfirm = async () => true;
  await parent.editorSurface.closeActiveTarget();
  await editor.openEdit(parent.state.segmentsById.segment);
  editor.draft.notesHtml = 'New current Notes';
  const newSaving = editor.saveDraft();
  old.resolve(saved(segment({ notesHtml: 'Old aggregate', estimatedDurationMinutes: 120, aggregateConcurrencyToken: 'retired-token' })));
  await oldSaving;
  await nextTick();
  assert.equal(editor.isSaving, true);
  assert.equal(editor.draft.notesHtml, 'New current Notes');
  assert.equal(editor.draft.aggregateConcurrencyToken, 'original-aggregate');
  assert.equal(editor.saveError, null);
  assert.equal(editor.lastSavedAt, null);
  assert.equal(parent.state.segmentsById.segment.notesHtml, '');
  newer.resolve(saved(segment({ notesHtml: 'New current Notes', aggregateConcurrencyToken: 'new-token' })));
  await newSaving;
  await nextTick();
  assert.equal(calls, 2);
  assert.equal(editor.draft.aggregateConcurrencyToken, 'new-token');
  assert.equal(parent.state.segmentsById.segment.notesHtml, 'New current Notes');
  assert.equal(editor.isDirty, false);
}));
