import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { parse, compileScript, compileTemplate } from '@vue/compiler-sfc';
import { build } from 'esbuild';
import { createRenderer, nextTick, proxyRefs } from 'vue';

// Qualify the actual App -> TripSidebar -> MetadataEditor using the existing mounted production-client seam.
const bundle = await build({ stdin: { contents: 'export { default } from "./App.vue"; export { captureMutationPublication, orderSegments } from "./api/tripEditorApi";', loader: 'ts',
  resolveDir: 'ClientApps/trip-editor/src' }, bundle: true, write: false, format: 'esm', platform: 'node', external: ['vue'],
  plugins: [{ name: 'c3-sharing-parent-tree', setup(builder) {
    // Fixture-owned fetch responses exercise publication and draft protection behind the authenticated dispatch boundary.
    builder.onLoad({ filter: /dependentDispatch\.ts$/ }, () => ({ contents:
      'export const dependentRefusal = () => false; export const dependentSegmentSuccess = (_response, result) => result.success === true && !!result.affected && !!result.deletedIds && Object.hasOwn(result, "data") && Array.isArray(result.warnings); export const sendDependent = (_, url, init) => fetch(url, init).then(response => ({ response, complete: async () => {} }));'
    }));
    builder.onLoad({ filter: /\.vue$/ }, async args => {
      // The layout-only wrapper passes both slots through; browser tests own its DOM geometry.
      if (/TripSidebarLayout\.vue$/.test(args.path)) return { contents: `export default { props: ['surfaceMode'], setup(_, { slots }) {
        return () => [slots.default?.(), slots.map?.()];
      } };` };
      if (/SegmentEditorSurface\.vue$/.test(args.path)) return { contents: `export default { setup(_, context) {
        context.expose({ focusNotes() {}, focusRouteAction: () => false }); return () => null;
      } };` };
      if (!/(?:App|TripSidebar|MetadataEditor|EditorSurface|RegionManager|RegionEditorSurface|SegmentManager|PlanningEditor|PlanningValueInput)\.vue$/.test(args.path)) return { contents: 'export default { render: () => null };' };
      const { descriptor } = parse(await readFile(args.path, 'utf8'), { filename: args.path });
      const script = compileScript(descriptor, { id: args.path, genDefaultAs: '__component' });
      const template = compileTemplate({ source: descriptor.template.content, filename: args.path, id: args.path,
        compilerOptions: { bindingMetadata: script.bindings } });
      return { loader: 'ts', contents: `${script.content}\n${template.code}\n__component.render = render;
        const setup = __component.setup;
        __component.setup = (props, context) => {
          const bindings = setup(props, context);
          globalThis.captureC3Component(${JSON.stringify(args.path.split('/').at(-1))}, bindings);
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
    builder.onLoad({ filter: /useConfirmDialog\.ts$/ }, () => ({ contents: `export const confirm = options => globalThis.c3Confirm(options);
      export const disposeConfirmDialogHost = () => {}; export const setConfirmDialogFocusFallback = () => {};` }));
  } }] });
const code = bundle.outputFiles[0].text.replaceAll('from "vue"', `from "${import.meta.resolve('vue')}"`);
const { default: actualApp, captureMutationPublication, orderSegments } = await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`);

// Self-contained client fixtures mirror the routed contract; no local database/evidence directory is needed.
const metadata = (enabled = false) => ({ contributionPolicy: 'CC-BY-4.0', credits: [], id: 'trip', planning: null, name: 'Trip', notesHtml: '', isPublic: true,
  shareProgressEnabled: enabled, center: null, zoom: null, coverImage: null,
  updatedAt: '2026-10-23T10:00:00Z', publicUrl: '/Public/Trips/trip', progressPublicUrl: enabled ? '/Public/Trips/trip?progress=1' : null,
  originalValues: { contributionPolicy: 'CC-BY-4.0', credits: [], name: 'Trip', notesHtml: null, isPublic: true, center: null, zoom: null, coverImage: null } });
const initial = { tripId: 'trip', metadata: metadata(), regionsById: {}, regionOrder: [], placesById: {}, placeOrderByRegionId: {},
  areasById: {}, areaOrderByRegionId: {}, segmentsById: {}, segmentOrder: [], tagsBySlug: {}, tagOrder: [],
  visitProgress: { totalPlaces: 0, visitedPlaces: 0, percentVisited: 0, placeSummariesByPlaceId: {}, historyRows: [] },
  options: { iconNames: [], markerColorClasses: [], transportModes: [], areaDefaults: { fillHex: '#ff6600' }, tag: { maxTags: 10, suggestionTake: 5 } },
  permissions: { canEditMetadata: true } };
const reply = (enabled, saved = metadata(enabled)) => JSON.stringify({ contract: 'bd2-1', success: true, data: saved, affected: { metadata: saved,
  regions: [], places: [], areas: [], segments: [], tags: [], regionOrder: null, segmentOrder: null, tagOrder: null,
  placeOrdersByRegionId: {}, areaOrdersByRegionId: {}, visitProgress: null, options: null },
  deletedIds: { regions: [], places: [], areas: [], segments: [], tags: [] }, warnings: [] });
// Publish refresh fixtures through the same complete-result path used by production saves.
const publishMetadata = (parent, metadata) => parent.applyMutation(JSON.parse(reply(false, metadata)));
const deferred = () => {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
};

// Reuse the host renderer from placeConditionalEditing; production templates and parent event scheduling run unchanged.
const withApp = async action => {
  const previous = { window: globalThis.window, document: globalThis.document, fetch: globalThis.fetch, CSS: globalThis.CSS,
    Document: globalThis.Document, ShadowRoot: globalThis.ShadowRoot,
    c3Confirm: globalThis.c3Confirm, captureC3Component: globalThis.captureC3Component };
  const captured = {};
  const navigations = [];
  const confirmations = [];
  let approved = false;
  globalThis.captureC3Component = (name, bindings) => { captured[name] = proxyRefs(bindings); };
  globalThis.c3Confirm = async options => { confirmations.push(options); return approved; };
  // Vue text-model directives inspect the input root even in this non-DOM host.
  globalThis.Document = class {};
  globalThis.ShadowRoot = class {};
  globalThis.CSS = { escape: value => value };
  globalThis.document = { getElementById: () => null, querySelector: () => null };
  // The mounted shell uses window events to publish Planning language availability.
  globalThis.window = Object.assign(new EventTarget(), {
    Sortable: { create: () => ({ destroy() {} }) }, matchMedia: () => ({ matches: false, addEventListener() {}, removeEventListener() {} }),
    location: { assign: url => navigations.push(url) }
  });
  globalThis.fetch = async () => new Response(JSON.stringify(initial));
  const remove = node => {
    if (node?.parent) node.parent.children.splice(node.parent.children.indexOf(node), 1);
    if (node) node.parent = null;
  };
  const teleportBody = { children: [] };
  const renderer = createRenderer({ createElement: tag => ({ tagName: tag.toUpperCase(), style: {}, children: [], options: [],
    addEventListener() {}, removeEventListener() {}, setAttribute() {}, removeAttribute() {},
    getRootNode: () => globalThis.document }), createText: text => ({ text }), createComment: () => ({}),
    insert(node, parent, anchor) {
      remove(node);
      parent.children ??= [];
      const index = anchor ? parent.children.indexOf(anchor) : -1;
      parent.children.splice(index < 0 ? parent.children.length : index, 0, node);
      node.parent = parent;
    }, remove, setText: (node, text) => { node.text = text; },
    setElementText: (node, text) => { node.text = text; node.children = []; },
    patchProp: (node, key, _old, value) => { (node.props ??= {})[key] = value; }, querySelector: () => teleportBody, parentNode: node => node?.parent,
    nextSibling: node => node?.parent?.children[node.parent.children.indexOf(node) + 1] ?? null });
  const app = renderer.createApp(actualApp, { config: { editorEndpoint: `/api/trips/${initial.tripId}/editor`,
    antiforgeryToken: 'anti', tripIndexUrl: '/User/Trip' } });
  const root = { children: [] };
  const componentErrors = [];
  app.config.errorHandler = error => { componentErrors.push(error.message); };
  app.mount(root);
  try {
    for (let tick = 0; tick < 20 && !captured['MetadataEditor.vue']; tick++) await new Promise(done => setImmediate(done));
    const parent = captured['App.vue'];
    const editor = captured['MetadataEditor.vue'];
    assert.ok(editor, 'actual production Metadata owner mounted through App and TripSidebar');
    assert.equal(parent.error, null);
    await nextTick();
    await action({ parent, editor, regionEditor: captured['RegionManager.vue'], segmentEditor: captured['SegmentManager.vue'], planning: captured['PlanningEditor.vue'], surface: captured['EditorSurface.vue'], navigations, confirmations,
      approve: value => { approved = value; },
      visibleText: () => textOf(root),
      button: label => descendants(root).find(node => node.tagName === 'BUTTON' && textOf(node) === label) });
  } finally {
    captured['App.vue']?.editorSurface.clearActiveTarget();
    app.unmount();
    await nextTick();
    Object.assign(globalThis, previous);
    assert.deepEqual(componentErrors, [], 'component exceptions must fail the mounted test');
  }
};

// Inspect the rendered production templates as well as the state owner's safety contract.
const descendants = node => [node, ...(node.children ?? []).flatMap(descendants)];
const textOf = node => [node.text ?? '', ...(node.children ?? []).map(textOf)].join('');

// WB1: production publication and application-controlled exit include separate Planning saves.
test('Planning-only edits report unsaved and Save & Exit requests explicit separate-save consent', () => withApp(async owner => {
  const { editor, planning, navigations, confirmations } = owner;
  planning.add();
  planning.draft.entries[0].when.value.local = '2027-03-02';
  await nextTick();
  assert.match(owner.surface.displayedStatus, /Planning.*unsaved/i);
  assert.equal(planning.workspace.hasChanges.value, true);
  let requests = 0;
  globalThis.fetch = async () => { requests++; throw new Error('unexpected request'); };
  await editor.backToTrips();
  assert.equal(requests, 0);
  assert.equal(navigations.length, 0);
  assert.equal(confirmations.length, 1);
  assert.match(confirmations[0].message, /separately/i);
  owner.approve(true);
  await editor.backToTrips();
  assert.equal(navigations.length, 1);
  assert.equal(planning.workspace.hasChanges.value, true);
}));

test('all five independent affected slices preserve newer acknowledged Planning and later drafts', () => withApp(async ({ parent }) => {
  const graph = parent.state;
  graph.regionsById.r = { id: 'r', planning: null };
  graph.placesById.p = { id: 'p', regionId: 'r', planning: null };
  graph.areasById.a = { id: 'a', regionId: 'r', planning: null };
  graph.segmentsById.s = { id: 's', planning: null };
  const result = JSON.parse(reply(false));
  result.affected.regions = [JSON.parse(JSON.stringify(graph.regionsById.r))];
  result.affected.places = [JSON.parse(JSON.stringify(graph.placesById.p))];
  result.affected.areas = [JSON.parse(JSON.stringify(graph.areasById.a))];
  result.affected.segments = [JSON.parse(JSON.stringify(graph.segmentsById.s))];
  // Capture the actual dispatch boundary before Planning acknowledgements advance it.
  const publish = captureMutationPublication('/api/trips/trip/editor/metadata/conditional');
  const drafts = [];
  for (const [kind, id] of [['metadata', 'trip'], ['region', 'r'], ['place', 'p'], ['area', 'a'], ['segment', 's']]) {
    const draft = parent.planningWorkspace.open({ kind, entityId: id, mode: 'edit' });
    draft.entries.push({ id: `entry-${kind}`, when: { form: 'at', value: { precision: 'date', local: '2027-03-02' } } });
    globalThis.fetch = async (_, init) => {
      const request = JSON.parse(init.body);
      return new Response(JSON.stringify({ contract: 'planning-1', success: true, applied: true,
        targetKind: draft.kind, targetId: draft.id, parentId: draft.parentId, planning: request.value }));
    };
    await parent.planningWorkspace.save(draft);
    assert.equal(draft.confirmed, true);
    draft.entries[0].when.value.local = '2027-03-03';
    drafts.push(draft);
  }
  publish(result);
  parent.applyMutation(result);
  for (const draft of drafts) {
    const entity = draft.kind === 'trip' ? parent.state.metadata : parent.state[`${draft.kind}sById`][draft.id];
    assert.equal(entity.planning.entries[0].when.value.local, '2027-03-02');
    assert.equal(draft.original.entries[0].when.value.local, '2027-03-02');
    assert.equal(draft.entries[0].when.value.local, '2027-03-03');
  }
}));

test('pending and uncertain Planning are visible in shared status and application exit consent', () => withApp(async owner => {
  const { planning, editor } = owner;
  planning.add();
  planning.draft.entries[0].when.value.local = '2027-03-02';
  let settle;
  globalThis.fetch = async () => new Promise(resolve => { settle = resolve; });
  const saving = planning.workspace.save(planning.draft);
  await nextTick();
  assert.match(owner.surface.displayedStatus, /Planning save pending/);
  await editor.backToTrips();
  assert.equal(owner.navigations.length, 0);
  assert.match(owner.confirmations.at(-1).message, /pending or unconfirmed/);
  settle(new Response('{}'));
  await saving;
  await nextTick();
  assert.match(owner.surface.displayedStatus, /Planning save not confirmed/);
  await editor.backToTrips();
  assert.equal(owner.navigations.length, 0);
  assert.equal(planning.draft.uncertain, true);
}));

test('authoritative deletion exposes an individually recoverable Planning draft in the mounted editor', () => withApp(async owner => {
  const { parent, planning } = owner;
  parent.state.areasById.a = { id: 'a', regionId: 'r', planning: null };
  const draft = parent.planningWorkspace.open({ kind: 'area', entityId: 'a', mode: 'edit' });
  draft.entries.push({ id: 'retained', when: { form: 'unknown' } });
  planning.add();
  planning.draft.entries[0].when.value.local = '2027-03-02';
  const deletion = JSON.parse(reply(false));
  deletion.deletedIds.areas = ['a'];
  parent.applyMutation(deletion);
  await nextTick();
  assert.match(owner.visibleText(), /This area was deleted/);
  const discard = owner.button('Discard this Planning draft');
  assert.ok(discard && !discard.props.disabled);
  await discard.props.onClick();
  assert.ok(parent.planningWorkspace.drafts['area/a']);
  owner.approve(true);
  await discard.props.onClick();
  assert.equal(parent.planningWorkspace.drafts['area/a'], undefined);
  assert.equal(parent.planningWorkspace.dirty(planning.draft), true);
}));

// Current authoritative Planning must refresh all five clean owners, without exchanging dirty originals.
const datedPlan = local => ({ format: 1, entries: [{ id: 'entry', when: { form: 'at', value: { precision: 'date', local } } }] });
const planningTargets = [['metadata', 'trip'], ['region', 'r'], ['place', 'p'], ['area', 'a'], ['segment', 's']];
const seedPlanningTargets = parent => {
  for (const [kind, id] of planningTargets.slice(1)) parent.state[`${kind}sById`][id] = { id, regionId: 'r', planning: null };
};
const currentPlanningResult = (parent, local) => {
  const result = JSON.parse(reply(false, { ...parent.state.metadata, planning: datedPlan(local) }));
  for (const [kind, id] of planningTargets.slice(1)) result.affected[`${kind}s`] = [{ ...parent.state[`${kind}sById`][id], planning: datedPlan(local) }];
  return result;
};
test('current authoritative publication refreshes five clean Planning owners and retains five dirty originals', () => withApp(async ({ parent }) => {
  seedPlanningTargets(parent);
  const drafts = planningTargets.map(([kind, entityId]) => parent.planningWorkspace.open({ kind, entityId, mode: 'edit' }));
  // Prior local acknowledgements protect only older publications, never every future refresh.
  for (const draft of drafts) {
    draft.entries.push(...datedPlan('2027-03-02').entries);
    globalThis.fetch = async (_, init) => new Response(JSON.stringify({ contract: 'planning-1', success: true, applied: true,
      targetKind: draft.kind, targetId: draft.id, parentId: draft.parentId, planning: JSON.parse(init.body).value }));
    await parent.planningWorkspace.save(draft);
  }
  for (const local of ['2027-03-09', '2027-03-10']) {
    const result = currentPlanningResult(parent, local);
    result.affected.places[0].regionId = 'current-parent';
    parent.applyMutation(result);
    for (const draft of drafts) {
      const entity = draft.kind === 'trip' ? parent.state.metadata : parent.state[`${draft.kind}sById`][draft.id];
      assert.equal(entity.planning.entries[0].when.value.local, local);
      assert.equal(draft.original.entries[0].when.value.local, '2027-03-09');
      if (draft.kind === 'place') assert.equal(draft.parentId, 'current-parent');
      assert.equal(draft.entries[0].when.value.local, local === '2027-03-09' ? local : '2027-03-15');
    }
    for (const draft of drafts) draft.entries[0].when.value.local = '2027-03-15';
  }
}));

for (const outcome of ['valid', 'uncertain', 'rejected']) {
  test(`deleted Planning recovery survives a ${outcome} late reply and protects exit until individual resolution`, () => withApp(async owner => {
    const { parent, planning, editor } = owner;
    seedPlanningTargets(parent);
    planning.add(); planning.draft.entries[0].when.value.local = '2027-04-01';
    const draft = parent.planningWorkspace.open({ kind: 'area', entityId: 'a', mode: 'edit' });
    draft.entries.push(...datedPlan('2027-03-02').entries);
    let settle;
    globalThis.fetch = async () => new Promise(done => { settle = done; });
    const saving = parent.planningWorkspace.save(draft);
    const deletion = JSON.parse(reply(false)); deletion.deletedIds.areas = ['a']; parent.applyMutation(deletion);
    await nextTick();
    assert.equal(owner.button('Discard this Planning draft').props.disabled, true);
    // Later typing is separate from the exact earlier save, even after authoritative deletion.
    draft.entries[0].label = 'Retain this';
    settle(outcome === 'valid' ? new Response(JSON.stringify({ contract: 'planning-1', success: true, applied: true,
      targetKind: 'area', targetId: 'a', parentId: 'r', planning: datedPlan('2027-03-02') }))
      : outcome === 'uncertain' ? new Response('{}') : new Response(JSON.stringify({ status: 409, code: 'mutation-base-stale' }), { status: 409 }));
    await saving; await nextTick();
    assert.equal(parent.state.areasById.a, undefined);
    assert.equal(draft.confirmed, outcome === 'valid');
    assert.equal(draft.uncertain, outcome === 'uncertain');
    assert.equal(draft.original === null, outcome !== 'valid');
    assert.equal(parent.planningWorkspace.hasChanges.value, true);
    assert.ok(parent.planningWorkspace.status());
    assert.equal(draft.entries[0].label, 'Retain this');
    assert.equal(owner.button('Discard this Planning draft').props.disabled, outcome === 'uncertain');
    if (outcome === 'valid') assert.match(owner.visibleText(), /earlier Planning save succeeded.*target remains deleted/i);
    await editor.backToTrips(); assert.equal(owner.navigations.length, 0); assert.equal(owner.confirmations.length, 1);
    if (outcome !== 'uncertain') {
      owner.approve(true); await owner.button('Discard this Planning draft').props.onClick();
      assert.equal(parent.planningWorkspace.drafts['area/a'], undefined);
    }
    assert.equal(parent.planningWorkspace.dirty(planning.draft), true);
  }));
}

test('valid late success with no later typing retains deleted recovery and exit protection', () => withApp(async ({ parent, editor, confirmations, navigations }) => {
  seedPlanningTargets(parent);
  const draft = parent.planningWorkspace.open({ kind: 'area', entityId: 'a', mode: 'edit' });
  draft.entries.push(...datedPlan('2027-03-02').entries);
  let settle; globalThis.fetch = async () => new Promise(done => { settle = done; });
  const saving = parent.planningWorkspace.save(draft);
  const deletion = JSON.parse(reply(false)); deletion.deletedIds.areas = ['a']; parent.applyMutation(deletion);
  settle(new Response(JSON.stringify({ contract: 'planning-1', success: true, applied: true, targetKind: 'area', targetId: 'a', parentId: 'r', planning: datedPlan('2027-03-02') })));
  await saving;
  assert.equal(draft.confirmed, true); assert.equal(parent.planningWorkspace.hasChanges.value, true);
  assert.match(parent.planningWorkspace.status(), /deleted/i);
  await editor.backToTrips(); assert.equal(confirmations.length, 1); assert.equal(navigations.length, 0);
}));

test('real Segment ordering and independent affected graph replies cannot restore deleted identities', () => withApp(async ({ parent }) => {
  seedPlanningTargets(parent); parent.state.segmentOrder = ['s'];
  const older = currentPlanningResult(parent, '2027-03-09'); older.affected.segmentOrder = ['s'];
  const gate = deferred(); globalThis.fetch = async () => { await gate.promise; return new Response(JSON.stringify(older)); };
  const ordering = orderSegments('/api/trips/trip/editor', 'anti', { segmentIds: ['s'] });
  const draft = parent.planningWorkspace.open({ kind: 'segment', entityId: 's', mode: 'edit' });
  draft.entries.push(...datedPlan('2027-03-02').entries);
  const deletion = JSON.parse(reply(false));
  for (const kind of ['regions', 'places', 'areas', 'segments']) deletion.deletedIds[kind] = Object.keys(parent.state[`${kind}ById`]);
  parent.applyMutation(deletion);
  gate.resolve(); parent.applyMutation(await ordering);
  for (const kind of ['regions', 'places', 'areas', 'segments']) assert.deepEqual(Object.keys(parent.state[`${kind}ById`]), []);
  assert.deepEqual(parent.state.segmentOrder, []);
  assert.equal(draft.deleted, true); assert.equal(parent.planningWorkspace.hasChanges.value, true);
  // A normal later creation still publishes. Session deletion authority concerns the removed identities only.
  const created = JSON.parse(reply(false)); created.affected.areas = [{ id: 'new', regionId: 'new-region', planning: datedPlan('2027-05-01') }];
  parent.applyMutation(created); assert.ok(parent.state.areasById.new);
}));

// Execute SegmentManager's real delayed producer and independent row deletion through the mounted parent.
test('mounted Segment order reply cannot undo independent row deletion', () => withApp(async owner => {
  const { parent, segmentEditor } = owner;
  const segment = { id: 's', tripId: 'trip', fromPlaceId: null, toPlaceId: null, waypointPlaceIds: [], waypointRouteVertexIndices: [],
    planning: null, externalRouting: null, route: null, effectiveRoute: null, mode: 'Walk', displayOrder: 1,
    estimatedDistanceKm: null, estimatedDurationMinutes: null, estimatedDurationSource: 'Automatic', notesHtml: '', capabilities: { canEdit: true, canDelete: true } };
  parent.state.segmentsById.s = segment; parent.state.segmentOrder = ['s']; await nextTick();
  const older = JSON.parse(reply(false)); older.affected.segments = [segment]; older.affected.segmentOrder = ['s'];
  const deletion = JSON.parse(reply(false)); deletion.deletedIds.segments = ['s']; deletion.affected.segmentOrder = [];
  let release; const calls = [];
  globalThis.fetch = async (url, init) => {
    calls.push([url, init.method]);
    if (url.endsWith('/segments/order')) return new Promise(done => { release = () => done(new Response(JSON.stringify(older))); });
    assert.equal(init.method, 'DELETE'); return new Response(JSON.stringify(deletion));
  };
  const ordering = segmentEditor.reorder(['s'], ['s']); assert.ok(release);
  owner.approve(true); await segmentEditor.deleteSegmentFromRow(segment); await nextTick();
  assert.equal(parent.state.segmentsById.s, undefined);
  release(); await ordering; await nextTick();
  assert.equal(parent.state.segmentsById.s, undefined); assert.deepEqual(parent.state.segmentOrder, []);
  assert.equal(calls.length, 2);
}));

test('late comparison review cannot exchange a deleted recovery original or release its hold', () => withApp(async ({ parent }) => {
  seedPlanningTargets(parent);
  const draft = parent.planningWorkspace.open({ kind: 'area', entityId: 'a', mode: 'edit' });
  draft.entries.push(...datedPlan('2027-03-02').entries);
  const staleGraph = JSON.parse(JSON.stringify(parent.state)); staleGraph.areasById.a.planning = datedPlan('2027-03-09');
  let settle; globalThis.fetch = async () => new Promise(done => { settle = done; });
  const reviewing = parent.planningWorkspace.review(draft);
  const deletion = JSON.parse(reply(false)); deletion.deletedIds.areas = ['a']; parent.applyMutation(deletion);
  settle(new Response(JSON.stringify(staleGraph)));
  await assert.rejects(reviewing, /scope changed/);
  assert.equal(draft.original, null); assert.equal(draft.deleted, true);
  assert.equal(parent.planningWorkspace.hasChanges.value, true);
}));
