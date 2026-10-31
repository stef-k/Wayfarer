import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { parse, compileScript, compileTemplate } from '@vue/compiler-sfc';
import { build } from 'esbuild';
import { createRenderer, nextTick, proxyRefs } from 'vue';

// Qualify the actual App -> TripSidebar -> MetadataEditor using the existing mounted production-client seam.
const bundle = await build({ stdin: { contents: 'export { default } from "./App.vue"; export { patchMetadata, orderSegments } from "./api/tripEditorApi";', loader: 'ts',
  resolveDir: 'ClientApps/trip-editor/src' }, bundle: true, write: false, format: 'esm', platform: 'node', external: ['vue'],
  plugins: [{ name: 'c3-sharing-parent-tree', setup(builder) {
    builder.onLoad({ filter: /\.vue$/ }, async args => {
      if (/SegmentEditorSurface\.vue$/.test(args.path)) return { contents: `export default { setup(_, context) {
        context.expose({ focusNotes() {}, focusRouteAction: () => false }); return () => null;
      } };` };
      if (!/(?:App|TripSidebar|MetadataEditor|EditorSurface|RegionManager|RegionEditorSurface|PlanningEditor|PlanningValueInput)\.vue$/.test(args.path)) return { contents: 'export default { render: () => null };' };
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
const { default: actualApp, patchMetadata, orderSegments } = await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`);

// Self-contained client fixtures mirror the routed contract; no local database/evidence directory is needed.
const metadata = (enabled = false) => ({ id: 'trip', planning: null, name: 'Trip', notesHtml: '', isPublic: true,
  shareProgressEnabled: enabled, center: null, zoom: null, coverImage: null,
  updatedAt: '2026-10-23T10:00:00Z', publicUrl: '/Public/Trips/trip', progressPublicUrl: enabled ? '/Public/Trips/trip?progress=1' : null,
  originalValues: { name: 'Trip', notesHtml: null, isPublic: true, center: null, zoom: null, coverImage: null } });
const initial = { tripId: 'trip', metadata: metadata(), regionsById: {}, regionOrder: [], placesById: {}, placeOrderByRegionId: {},
  areasById: {}, areaOrderByRegionId: {}, segmentsById: {}, segmentOrder: [], tagsBySlug: {}, tagOrder: [],
  visitProgress: { totalPlaces: 0, visitedPlaces: 0, percentVisited: 0, placeSummariesByPlaceId: {}, historyRows: [] },
  options: { iconNames: [], markerColorClasses: [], transportModes: [], areaDefaults: { fillHex: '#ff6600' }, tag: { maxTags: 10, suggestionTake: 5 } },
  permissions: { canEditMetadata: true } };
const reply = (enabled, saved = metadata(enabled)) => JSON.stringify({ success: true, data: saved, affected: { metadata: saved,
  regions: [], places: [], areas: [], segments: [], tags: [], regionOrder: null, segmentOrder: null, tagOrder: null,
  placeOrdersByRegionId: {}, areaOrdersByRegionId: {}, visitProgress: null, options: null },
  deletedIds: { regions: [], places: [], areas: [], segments: [], tags: [] }, warnings: [] });
// Publish refresh fixtures through the same complete-result path used by production saves.
const publishMetadata = (parent, metadata) => parent.applyMutation(JSON.parse(reply(false, metadata)));
const enabledReply = reply(true);
const disabledReply = reply(false);
const refreshedState = { metadata: metadata(true) };
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
  globalThis.window = { matchMedia: () => ({ matches: false, addEventListener() {}, removeEventListener() {} }),
    addEventListener() {}, removeEventListener() {}, location: { assign: url => navigations.push(url) } };
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
  app.mount(root);
  try {
    for (let tick = 0; tick < 20 && !captured['MetadataEditor.vue']; tick++) await new Promise(done => setImmediate(done));
    const parent = captured['App.vue'];
    const editor = captured['MetadataEditor.vue'];
    assert.ok(editor, 'actual production Metadata owner mounted through App and TripSidebar');
    assert.equal(parent.error, null);
    await nextTick();
    await action({ parent, editor, regionEditor: captured['RegionManager.vue'], planning: captured['PlanningEditor.vue'], surface: captured['EditorSurface.vue'], navigations, confirmations,
      approve: value => { approved = value; },
      visibleText: () => textOf(root),
      button: label => descendants(root).find(node => node.tagName === 'BUTTON' && textOf(node) === label) });
  } finally {
    captured['App.vue']?.editorSurface.clearActiveTarget();
    app.unmount();
    await nextTick();
    Object.assign(globalThis, previous);
  }
};

// Inspect the rendered production templates as well as the state owner's safety contract.
const descendants = node => [node, ...(node.children ?? []).flatMap(descendants)];
const textOf = node => [node.text ?? '', ...(node.children ?? []).map(textOf)].join('');

// Emit reviewable state before each safety assertion, including what the user sees and navigation behavior.
const observation = (label, owner, requests) => {
  const { editor, parent, navigations, confirmations } = owner;
  const event = { prevented: false, preventDefault() { this.prevented = true; } };
  editor.confirmUnload(event);
  const state = { parentSharing: parent.state.metadata.shareProgressEnabled, draftSharing: editor.draft.shareProgressEnabled,
    baselineSharing: editor.persistedDraft.shareProgressEnabled, dirty: editor.isDirty, status: editor.statusText,
    visibleText: owner.visibleText(), error: editor.saveError, unloadWarns: event.prevented, surfaceDirty: parent.editorSurface.isActiveTargetDirty(),
    requests, navigations, confirmations: confirmations.length };
  console.log(JSON.stringify({ label, ...state }));
  return state;
};

// No checkbox reversal: the acknowledgement cleans metadata but cannot exit a newer Region session.
test('R1: delayed sharing Save & Exit preserves a newer Region draft and navigation protection', () => withApp(async owner => {
  const { editor, parent, regionEditor } = owner;
  const gate = deferred();
  let calls = 0;
  globalThis.fetch = async () => { calls++; await gate.promise; return new Response(enabledReply); };
  editor.visibleShareProgressEnabled = true;
  const pending = editor.saveAndExit();
  await nextTick();
  owner.approve(true);
  await regionEditor.openCreate();
  regionEditor.draft.name = 'Newer Region choice';
  regionEditor.draft.notesHtml = 'Newer Region notes';
  await nextTick();
  gate.resolve();
  await pending;
  await nextTick();
  const settled = observation('R1-late-success-newer-region', owner, calls);
  console.log(JSON.stringify({ regionName: regionEditor.draft.name, regionNotes: regionEditor.draft.notesHtml,
    regionDirty: regionEditor.regionDirty, aggregateDirty: editor.hasUnsavedEditorChanges, activeTarget: parent.editorSurface.activeTarget.value }));
  assert.deepEqual(owner.navigations, [], 'the earlier discard approval cannot authorize losing later work');
  assert.equal(owner.confirmations.length, 1, 'a superseded continuation must not seek exit permission');
  assert.equal(parent.editorSurface.activeTarget.value.identity, 'region:add');
  assert.equal(regionEditor.draft.name, 'Newer Region choice');
  assert.equal(regionEditor.draft.notesHtml, 'Newer Region notes');
  assert.equal(regionEditor.regionDirty, true);
  assert.equal(editor.hasUnsavedEditorChanges, true);
  assert.equal(settled.unloadWarns, true);
  assert.equal(settled.dirty, false, 'the valid sharing acknowledgement settles its own attempt');
  assert.equal(settled.parentSharing, true);
  assert.equal(settled.baselineSharing, true);
  assert.equal(settled.draftSharing, true);
  assert.equal(editor.draftState.sharingAttempt.value, null);
  assert.equal(editor.saveError, null);
  assert.equal(calls, 1);
  owner.approve(false);
  await editor.backToTrips();
  assert.equal(owner.confirmations.length, 2, 'the shared navigation action still protects the Region draft');
  assert.deepEqual(owner.navigations, []);
}));

// A session change invalidates exit even if the reopened editor is clean; the ordinary path still exits.
for (const reopened of [false, true]) test(`R1: delayed sharing success ${reopened ? 'cannot exit a reopened session' : 'exits its current clean session'}`, () => withApp(async owner => {
  const { editor, parent } = owner;
  const gate = deferred();
  globalThis.fetch = async () => { await gate.promise; return new Response(enabledReply); };
  editor.visibleShareProgressEnabled = true;
  const pending = editor.saveAndExit();
  if (reopened) {
    owner.approve(true);
    assert.equal(await parent.editorSurface.closeActiveTarget(), true);
    await editor.openMetadata();
  }
  gate.resolve();
  await pending;
  await nextTick();
  assert.equal(editor.isDirty, false);
  assert.equal(editor.draftState.sharingAttempt.value, null);
  assert.equal(parent.state.metadata.shareProgressEnabled, true);
  assert.deepEqual(owner.navigations, reopened ? [] : ['/User/Trip']);
}));

test('C3 control: delayed successful enable retains newer disable and next explicit save completes it', () => withApp(async owner => {
  const { editor, parent } = owner;
  const gate = deferred();
  const requests = [];
  globalThis.fetch = async (url, options) => {
    requests.push({ url, body: JSON.parse(options.body) });
    if (requests.length === 1) await gate.promise;
    return new Response(requests.length === 1 ? enabledReply : disabledReply);
  };
  editor.visibleShareProgressEnabled = true;
  const enabling = editor.saveAndExit();
  assert.equal(requests.length, 1);
  editor.visibleShareProgressEnabled = false;
  publishMetadata(parent, { ...initial.metadata });
  await nextTick();
  gate.resolve();
  await enabling;
  await nextTick();
  const pending = observation('delayed-success-newer-disable-retained', owner, requests);
  assert.equal(pending.draftSharing, false);
  assert.equal(pending.parentSharing, true);
  assert.equal(pending.dirty, true);
  assert.equal(pending.unloadWarns, true);
  assert.equal(requests.length, 1);
  assert.deepEqual(owner.navigations, []);
  await editor.saveAndExit();
  const complete = observation('delayed-success-correction-completed', owner, requests);
  assert.deepEqual(requests.map(r => r.body), [{ enabled: true }, { enabled: false }]);
  assert.equal(complete.dirty, false);
  assert.equal(complete.parentSharing, false);
  assert.equal(complete.draftSharing, false);
  assert.deepEqual(owner.navigations, ['/User/Trip']);
}));

// Both a reversal during flight and a new choice after the failure must outlive the uncertain predecessor.
for (const disableTiming of ['in-flight', 'after loss']) test(`C3 contract: lost enable reply retains newer disable (${disableTiming})`, () => withApp(async owner => {
  const { editor } = owner;
  const gate = deferred();
  const requests = [];
  globalThis.fetch = async (url, options) => {
    requests.push({ url, body: JSON.parse(options.body) });
    await gate.promise;
    return new Response('{}', { status: 500 });
  };
  editor.visibleShareProgressEnabled = true;
  const enabling = editor.saveAndExit();
  let during;
  if (disableTiming === 'in-flight') {
    editor.visibleShareProgressEnabled = false;
    await nextTick();
    during = observation('enable-pending-newer-disable-equals-old-base', owner, requests);
  }
  gate.resolve();
  await enabling;
  if (disableTiming === 'after loss') editor.visibleShareProgressEnabled = false;
  await nextTick();
  const lost = observation(`lost-reply-newer-disable-${disableTiming}`, owner, requests);
  await editor.saveAndExit();
  const resaved = observation('lost-reply-next-save', owner, requests);
  assert.equal(requests.length, 1, 'no correction or enable retry was sent');
  if (during) { assert.equal(during.dirty, true); assert.equal(during.unloadWarns, true); }
  assert.equal(lost.draftSharing, false);
  assert.equal(lost.dirty, true, 'newer disable must remain an obligation despite old-baseline equality');
  assert.equal(resaved.unloadWarns, true);
  assert.equal(resaved.surfaceDirty, true);
  assert.match(resaved.status, /not confirmed/i);
  assert.match(resaved.visibleText, /could not confirm.*choice.*retained/i);
  assert.equal(editor.lastSavedAt, null);
  assert.deepEqual(owner.navigations, []);
}));

test('C3 contract: parent refresh cannot erase newer disable or settle the unknown request', () => withApp(async owner => {
  const { editor, parent } = owner;
  const gate = deferred();
  const requests = [];
  globalThis.fetch = async (url, options) => {
    requests.push({ url, body: JSON.parse(options.body) });
    await gate.promise;
    return new Response('{}', { status: 500 });
  };
  editor.visibleShareProgressEnabled = true;
  const enabling = editor.save(false);
  editor.visibleShareProgressEnabled = false;
  gate.resolve();
  await enabling;
  // Both matching and opposite snapshots are observations, not evidence that the predecessor is terminal.
  for (const [enabled, isPublic] of [[true, true], [false, true], [false, false]]) {
    publishMetadata(parent, { ...metadata(enabled), isPublic });
    await nextTick();
    const refreshed = observation(`unknown-parent-refresh-${enabled}`, owner, requests);
    assert.equal(refreshed.parentSharing, enabled);
    assert.equal(refreshed.draftSharing, false, 'refresh retains the latest choice');
    assert.equal(refreshed.baselineSharing, false);
    assert.equal(refreshed.dirty, true);
    assert.equal(refreshed.unloadWarns, true);
    assert.match(refreshed.status, /not confirmed/i);
    await editor.saveAndExit();
    assert.equal(requests.length, 1);
    assert.deepEqual(owner.navigations, []);
  }
}));

test('C3 contract: network uncertainty without reversal cannot blindly replay the enable on another Save', () => withApp(async owner => {
  const { editor } = owner;
  const requests = [];
  globalThis.fetch = async (url, options) => {
    requests.push({ url, body: JSON.parse(options.body) });
    if (requests.length === 1) throw new TypeError('Controlled lost network reply after modeled commit');
    return new Response(enabledReply);
  };
  editor.visibleShareProgressEnabled = true;
  await editor.save(false);
  observation('unknown-enable-before-explicit-retry', owner, requests);
  await editor.save(false);
  const held = observation('unknown-enable-explicit-save-held', owner, requests);
  assert.match(held.status, /not confirmed/i);
  assert.equal(held.draftSharing, true);
  assert.equal(held.dirty, true);
  assert.equal(held.unloadWarns, true);
  assert.deepEqual(owner.navigations, []);
  assert.equal(requests.length, 1, 'uncertain earlier attempt must hold, not be replayed as a normal dirty save');
}));

// The bounded recovery interaction is deliberate abandonment, never a claim of remote settlement.
test('C3 recovery: stay retains the choice; approved Leave without saving warns and dispatches nothing', () => withApp(async owner => {
  const { editor, parent } = owner;
  let calls = 0;
  globalThis.fetch = async () => { calls++; throw new TypeError('Lost reply'); };
  editor.visibleShareProgressEnabled = true;
  await editor.save(false);
  editor.visibleShareProgressEnabled = false;
  await nextTick();
  assert.equal(owner.button('Save settings & Continue').props.disabled, true);
  assert.equal(owner.button('Save settings & Exit').props.disabled, true);
  const leave = owner.button('Leave without saving');
  assert.ok(leave && !leave.props.disabled, 'an explained, available next action is rendered');
  await leave.props.onClick();
  assert.deepEqual(owner.navigations, []);
  assert.match(owner.confirmations[0].message, /earlier.*may still finish/i);
  assert.match(owner.confirmations[0].message, /lose.*choice/i);
  assert.equal(editor.draft.shareProgressEnabled, false);
  assert.equal(parent.editorSurface.isActiveTargetDirty(), true);
  assert.equal(editor.lastSavedAt, null);
  owner.approve(true);
  await leave.props.onClick();
  assert.deepEqual(owner.navigations, ['/User/Trip']);
  assert.equal(calls, 1);
  assert.match(editor.statusText, /not confirmed/i);
  assert.equal(editor.lastSavedAt, null);
}));

// Surface discard must not turn reopening into an opportunity to replay an unresolved request.
for (const successful of [false, true]) test(`C3 close/reopen retains in-flight choice across a delayed ${successful ? 'success' : 'failure'}`, () => withApp(async owner => {
  const { editor, parent } = owner;
  const gate = deferred();
  let calls = 0;
  globalThis.fetch = async () => { calls++; await gate.promise; return new Response(successful ? enabledReply : '{}', { status: successful ? 200 : 500 }); };
  editor.visibleShareProgressEnabled = true;
  const saving = editor.saveAndExit();
  editor.visibleShareProgressEnabled = false;
  await nextTick();
  assert.equal(parent.editorSurface.isActiveTargetDirty(), true);
  assert.equal(await parent.editorSurface.closeActiveTarget(), false, 'declining close retains editor');
  owner.approve(true);
  assert.equal(await parent.editorSurface.closeActiveTarget(), true);
  gate.resolve();
  await saving;
  await nextTick();
  assert.equal(editor.draft.shareProgressEnabled, false);
  assert.equal(editor.isDirty, true);
  assert.deepEqual(owner.navigations, []);
  if (!successful) {
    assert.match(owner.visibleText(), /sharing.*not confirmed/i);
    editor.resetDraft();
    publishMetadata(parent, refreshedState.metadata);
    await nextTick();
  }
  await editor.openMetadata();
  await nextTick();
  assert.equal(editor.draft.shareProgressEnabled, false);
  assert.equal(parent.editorSurface.isActiveTargetDirty(), true);
  if (!successful) {
    await editor.saveAndExit();
    assert.match(owner.visibleText(), /could not confirm/i);
    assert.equal(calls, 1);
    assert.equal(await parent.editorSurface.closeActiveTarget(), true);
    await editor.openMetadata();
    await nextTick();
    assert.match(owner.visibleText(), /could not confirm/i);
    assert.equal(editor.draft.shareProgressEnabled, false);
  }
}));

// Body/shape failures also lack an acknowledgement; they must hold before changing parent or baseline.
test('C3 incomplete successful reply retains intent and cannot become a retry opportunity', () => withApp(async owner => {
  const { editor, parent } = owner;
  let calls = 0;
  globalThis.fetch = async () => { calls++; return new Response(JSON.stringify({ data: {}, affected: { metadata: {} } })); };
  editor.visibleShareProgressEnabled = true;
  await editor.saveAndExit();
  editor.visibleShareProgressEnabled = false;
  editor.resetDraft();
  await nextTick();
  assert.equal(editor.draft.shareProgressEnabled, false);
  assert.equal(editor.persistedDraft.shareProgressEnabled, false);
  assert.equal(parent.state.metadata.shareProgressEnabled, false);
  assert.equal(editor.draftState.sharingAttempt.value.enabled, true);
  assert.equal(editor.draftState.sharingAttempt.value.outcome, 'unknown');
  assert.equal(editor.isDirty, true);
  assert.match(owner.visibleText(), /could not confirm/i);
  await editor.saveAndExit();
  assert.equal(calls, 1);
  assert.deepEqual(owner.navigations, []);
}));

// An explicit pre-write validation rejection remains correctable through the normal save action.
test('C3 confirmed validation rejection permits a later explicit sharing save', () => withApp(async owner => {
  const { editor, parent } = owner;
  let calls = 0;
  globalThis.fetch = async () => {
    calls++;
    return calls === 1 ? new Response(JSON.stringify({ title: 'Review sharing', errors: { enabled: ['Review sharing'] } }), { status: 400 }) : new Response(enabledReply);
  };
  editor.visibleShareProgressEnabled = true;
  await editor.saveAndExit();
  await nextTick();
  assert.equal(editor.statusText, 'Save failed');
  assert.equal(editor.sharingUnconfirmed, false);
  assert.equal(editor.isDirty, true);
  assert.match(owner.visibleText(), /Review sharing/);
  assert.equal(owner.button('Save settings & Exit').props.disabled, false);
  await owner.button('Save settings & Exit').props.onClick();
  assert.equal(calls, 2);
  assert.equal(parent.state.metadata.shareProgressEnabled, true);
  assert.equal(editor.isDirty, false);
  assert.deepEqual(owner.navigations, ['/User/Trip']);
}));

// F2 regressions converted from the retained C5 metadata counterexamples; only transport is controlled.
test('F2: lost metadata reply blocks an identical blind replay on another Save', () => withApp(async owner => {
  const { editor } = owner;
  const requests = [];
  globalThis.fetch = async (_url, options) => {
    requests.push(JSON.parse(options.body));
    throw new TypeError('Lost reply; submitted metadata may still commit');
  };
  editor.draft.name = 'Earlier request';
  await editor.save(false);
  assert.equal(requests.length, 1);
  await editor.save(false);
  assert.equal(requests.length, 1, 'an unresolved conditional request must not be replayed');
  assert.equal(editor.isDirty, true);
  assert.match(editor.statusText, /not confirmed/i);
  assert.deepEqual(owner.navigations, []);
}));

test('F2: newer metadata reversal retains its obligation after reply loss and cannot report Saved or exit', () => withApp(async owner => {
  const { editor } = owner;
  const gate = deferred();
  let requests = 0;
  globalThis.fetch = async () => { requests++; await gate.promise; return new Response('{}', { status: 500 }); };
  editor.draft.name = 'Earlier request';
  const pending = editor.saveAndExit();
  assert.equal(requests, 1);
  editor.draft.name = 'Trip';
  gate.resolve();
  await pending;
  assert.equal(editor.isDirty, true, 'old-baseline equality cannot settle a newer reversal');
  await editor.saveAndExit();
  assert.equal(requests, 1);
  assert.equal(editor.lastSavedAt, null);
  assert.deepEqual(owner.navigations, []);
  const event = { prevented: false, preventDefault() { this.prevented = true; } };
  editor.confirmUnload(event);
  assert.equal(event.prevented, true);
  assert.equal(owner.parent.editorSurface.isActiveTargetDirty(), true);
}));

// Refresh/reset and editor switching are not evidence that a submitted request cannot still commit.
test('F2: uncertain metadata survives refresh, reset, close/reopen and explicit abandonment', () => withApp(async owner => {
  const { editor, parent } = owner;
  let calls = 0;
  globalThis.fetch = async () => { calls++; throw new TypeError('Lost reply'); };
  editor.draft.name = 'Earlier request';
  await editor.save(false);
  const attempt = editor.draftState.metadataAttempt.value;
  editor.draft.name = 'Trip';
  editor.draft.notesHtml = 'Newer notes';
  for (const name of ['Earlier request', 'Trip']) {
    publishMetadata(parent, { ...metadata(), name, originalValues: { ...metadata().originalValues, name } });
    await nextTick();
    editor.resetDraft();
    assert.equal(editor.draft.name, 'Trip');
    assert.equal(editor.draft.notesHtml, 'Newer notes');
    assert.equal(editor.persistedDraft.name, 'Trip');
    assert.equal(editor.draftState.metadataAttempt.value, attempt);
  }
  owner.approve(true);
  await parent.editorSurface.closeActiveTarget();
  await editor.openMetadata();
  await nextTick();
  assert.equal(editor.draft.notesHtml, 'Newer notes');
  assert.equal(parent.editorSurface.isActiveTargetDirty(), true);
  assert.equal(owner.button('Save settings & Continue').props.disabled, true);
  assert.equal(owner.button('Save settings & Exit').props.disabled, true);
  assert.equal(owner.button('Cancel / Reset').props.disabled, true);
  assert.match(owner.visibleText(), /could not confirm.*settings.*retained/i);
  owner.approve(false);
  await owner.button('Leave without saving').props.onClick();
  assert.deepEqual(owner.navigations, []);
  assert.match(owner.confirmations.at(-1).message, /does not undo/i);
  owner.approve(true);
  await owner.button('Leave without saving').props.onClick();
  assert.deepEqual(owner.navigations, ['/User/Trip']);
  assert.equal(calls, 1);
  assert.equal(editor.lastSavedAt, null);
}));

// Reuse the accepted delayed-navigation control with the metadata operation in the same production owner.
for (const newerRegion of [false, true]) test(`F2: delayed metadata success preserves ${newerRegion ? 'a newer Region session' : 'a reversal and its next explicit save'}`, () => withApp(async owner => {
  const { editor, parent, regionEditor } = owner;
  const gate = deferred();
  const requests = [];
  globalThis.fetch = async (_url, options) => {
    const body = JSON.parse(options.body);
    requests.push(body);
    const saved = { ...metadata(), ...body.changes, originalValues: { ...metadata().originalValues, ...body.changes } };
    await gate.promise;
    return new Response(reply(false, saved));
  };
  editor.draft.name = 'Earlier request';
  const pending = editor.saveAndExit();
  if (newerRegion) {
    owner.approve(true);
    await regionEditor.openCreate();
    regionEditor.draft.name = 'Newer Region';
  } else {
    editor.draft.name = 'Trip';
    editor.resetDraft(); // A pending attempt and reversal also survive explicit reset.
    assert.equal(editor.isDirty, true);
  }
  gate.resolve();
  await pending;
  assert.equal(editor.draftState.metadataAttempt.value, null);
  assert.equal(parent.state.metadata.name, 'Earlier request');
  assert.deepEqual(owner.navigations, []);
  if (newerRegion) {
    assert.equal(regionEditor.draft.name, 'Newer Region');
    assert.equal(regionEditor.regionDirty, true);
    assert.equal(owner.confirmations.length, 1, 'old Save & Exit permission is revoked');
  } else {
    assert.equal(editor.draft.name, 'Trip');
    assert.equal(editor.isDirty, true);
    await editor.saveAndExit();
    assert.deepEqual(requests[1], { expected: { name: 'Earlier request' }, changes: { name: 'Trip' } });
    assert.equal(editor.isDirty, false);
    assert.deepEqual(owner.navigations, ['/User/Trip']);
  }
}));

// Missing/malformed or mis-scoped acknowledgements cannot resolve the request or mutate parent state.
for (const invalid of ['body', 'scope', 'originals', 'notes-type', 'notes-original-type', 'notes-inconsistent']) test(`F2: unusable metadata ${invalid} reply holds the corresponding attempt`, () => withApp(async owner => {
  const { editor, parent } = owner;
  let calls = 0;
  const saved = { ...metadata(), name: 'Earlier request', originalValues: { ...metadata().originalValues, name: 'Earlier request' } };
  if (invalid === 'scope') saved.id = 'another-trip';
  if (invalid === 'originals') delete saved.originalValues;
  if (invalid === 'notes-type') saved.notesHtml = null;
  if (invalid === 'notes-original-type') saved.originalValues.notesHtml = {};
  if (invalid === 'notes-inconsistent') saved.notesHtml = '<p>Different Notes</p>';
  globalThis.fetch = async () => { calls++; return new Response(invalid === 'body' ? '{' : reply(false, saved)); };
  editor.draft.name = 'Earlier request';
  await editor.saveAndExit();
  await editor.saveAndExit();
  assert.equal(calls, 1);
  assert.equal(parent.state.metadata.name, 'Trip');
  assert.equal(editor.draft.name, 'Earlier request');
  assert.equal(editor.metadataUnconfirmed, true);
  assert.equal(editor.lastSavedAt, null);
  assert.deepEqual(owner.navigations, []);
}));

// Definitive refusal resolves only this attempt; authored edits can still be saved explicitly.
for (const status of [400, 409]) test(`F2: metadata rejection ${status} permits a corrected explicit save`, () => withApp(async owner => {
  const { editor } = owner;
  let calls = 0;
  const saved = { ...metadata(), name: 'Corrected', originalValues: { ...metadata().originalValues, name: 'Corrected' } };
  globalThis.fetch = async () => ++calls === 1
    ? new Response(JSON.stringify({ title: 'Review name', errors: { name: ['Review name'] }, code: 'mutation-base-stale' }), { status })
    : new Response(reply(false, saved));
  editor.draft.name = 'Earlier request';
  await editor.saveAndExit();
  assert.equal(editor.draftState.metadataAttempt.value, null);
  assert.equal(editor.metadataUnconfirmed, false);
  assert.equal(editor.isDirty, true);
  editor.draft.name = 'Corrected';
  await editor.saveAndExit();
  assert.equal(calls, 2);
  assert.equal(editor.isDirty, false);
  assert.deepEqual(owner.navigations, ['/User/Trip']);
}));

// Historical partial centers and blank cover originals have intentionally lossy display projections.
test('F2: valid metadata acknowledgement retains exact nullable originals behind display defaults', () => withApp(async owner => {
  const { editor } = owner;
  const saved = { ...metadata(), name: 'Saved', originalValues: { ...metadata().originalValues,
    name: 'Saved', center: { latitude: null, longitude: 23 }, coverImage: { rawUrl: '  ' } } };
  globalThis.fetch = async () => new Response(reply(false, saved));
  editor.draft.name = 'Saved';
  await editor.saveAndExit();
  assert.equal(editor.draftState.metadataAttempt.value, null);
  assert.equal(editor.isDirty, false);
  editor.draft.centerLatitude = 38;
  editor.draft.centerLongitude = 24;
  assert.deepEqual(editor.draftState.request().expected.center, { latitude: null, longitude: 23 });
  assert.deepEqual(owner.navigations, ['/User/Trip']);
}));

// Mirror the retained routed U+0085/U+FEFF envelopes and ordinary mapper cover projections.
for (const [label, rawUrl, displayUrl] of [
  ['null', null, null], ['empty', '', null], ['whitespace', ' \t\r\n', null],
  ['U+0085', '\u0085', null], ['U+FEFF', '\uFEFF', '\uFEFF'],
  ['nonblank', ' https://example.test/cover.jpg ', ' https://example.test/cover.jpg ']
]) test(`F2: valid Name acknowledgement with ${label} cover adopts and retains the exact original`, () => withApp(async owner => {
  const { editor, parent } = owner;
  const coverImage = displayUrl === null ? null : { rawUrl: displayUrl, proxiedUrl: '/Public/ProxyImage' };
  const originalCover = rawUrl === null ? null : { rawUrl };
  publishMetadata(parent, { ...metadata(), coverImage, originalValues: { ...metadata().originalValues, coverImage: originalCover } });
  await nextTick();
  const saved = { ...parent.state.metadata, name: 'Saved name',
    originalValues: { ...parent.state.metadata.originalValues, name: 'Saved name' } };
  let calls = 0;
  globalThis.fetch = async () => { calls++; return new Response(reply(false, saved)); };
  editor.draft.name = 'Saved name';
  await editor.saveAndExit();
  assert.equal(calls, 1);
  assert.equal(parent.state.metadata.name, 'Saved name');
  assert.equal(editor.metadataUnconfirmed, false);
  assert.equal(editor.isDirty, false);
  assert.notEqual(editor.lastSavedAt, null);
  assert.deepEqual(owner.navigations, ['/User/Trip']);
  editor.draft.coverImageRawUrl = 'https://example.test/replacement.jpg';
  assert.deepEqual(editor.draftState.request().expected.coverImage, originalCover);
}));

// Defensive M1 checks use immutable intent; only explicit empty Clear has predictable Notes equality.
for (const scenario of ['name', 'newerName', 'notesHtml', 'isPublic', 'coverImage', 'center', 'zoom'])
  test(`M1: same-Trip acknowledgement contradicting submitted ${scenario} stays held`, () => withApp(async owner => {
    const { editor, parent } = owner;
    const field = scenario === 'newerName' ? 'name' : scenario;
    const gate = deferred();
    const desired = { name: 'Requested name', notesHtml: '', isPublic: false,
      coverImage: { rawUrl: 'https://example.test/requested.jpg' }, center: { latitude: 1, longitude: 2 }, zoom: 3 };
    const changes = { [field]: desired[field] };
    const unrelated = { name: 'Unrelated result', notesHtml: 'Unrelated notes', isPublic: true,
      coverImage: { rawUrl: 'https://example.test/unrelated.jpg' }, center: { latitude: 4, longitude: 5 }, zoom: 6 };
    const saved = { ...metadata(), ...changes, [field]: unrelated[field],
      originalValues: { ...metadata().originalValues, ...changes, [field]: unrelated[field] } };
    if (field === 'coverImage') saved.coverImage = { ...unrelated.coverImage, proxiedUrl: '/Public/ProxyImage' };
    let calls = 0;
    globalThis.fetch = async () => { calls++; await gate.promise; return new Response(reply(false, saved)); };
    const draftChanges = { name: { name: desired.name }, notesHtml: { notesHtml: desired.notesHtml }, isPublic: { isPublic: false },
      coverImage: { coverImageRawUrl: desired.coverImage.rawUrl }, center: { centerLatitude: 1, centerLongitude: 2 }, zoom: { zoom: 3 } };
    // Make Notes dirty before testing an explicit Clear against unrelated nonempty returned Notes.
    if (field === 'notesHtml') {
      publishMetadata(parent, { ...metadata(), notesHtml: 'Previous notes',
        originalValues: { ...metadata().originalValues, notesHtml: 'Previous notes' } });
      await nextTick();
    }
    Object.assign(editor.draft, draftChanges[field]);
    const saving = editor.saveAndExit();
    const submitted = editor.draftState.metadataAttempt.value.request;
    // A later draft matching the wrong response cannot authorize adoption of that response.
    if (scenario === 'newerName') editor.draft.name = unrelated.name;
    gate.resolve();
    await saving;
    assert.equal(parent.state.metadata.name, 'Trip');
    assert.equal(editor.metadataUnconfirmed, true);
    assert.equal(editor.draftState.metadataAttempt.value.request, submitted);
    assert.deepEqual(submitted.changes, changes);
    assert.equal(editor.isDirty, true);
    assert.equal(editor.lastSavedAt, null);
    await editor.saveAndExit();
    assert.equal(calls, 1);
    assert.deepEqual(owner.navigations, []);
  }));

// Mirror the genuine routed SMS normalization; the real sanitizer/Quill seam is covered in Playwright.
test('F2: server-normalized Notes acknowledgement adopts before Saved and exit', () => withApp(async owner => {
  const { editor, parent } = owner;
  const saved = { ...metadata(), notesHtml: '<p><a>Contact</a></p>',
    originalValues: { ...metadata().originalValues, notesHtml: '<p><a>Contact</a></p>' } };
  let calls = 0;
  globalThis.fetch = async () => { calls++; return new Response(reply(false, saved)); };
  editor.draft.notesHtml = '<p><a href="sms:+1234">Contact</a></p>';
  await editor.saveAndExit();
  assert.equal(calls, 1);
  assert.equal(parent.state.metadata.notesHtml, saved.notesHtml);
  assert.equal(editor.draft.notesHtml, saved.notesHtml);
  assert.equal(editor.metadataUnconfirmed, false);
  assert.equal(editor.isDirty, false);
  assert.notEqual(editor.lastSavedAt, null);
  assert.deepEqual(owner.navigations, ['/User/Trip']);
}));

// Blank display must be null, and nonblank display must preserve exact material, as in Region ordering.
for (const invalid of ['missing', 'blank-object', 'U+0085-object', 'U+FEFF-null'])
  test(`F2: malformed ${invalid} cover acknowledgement holds without replay or Saved`, () => withApp(async owner => {
    const { editor, parent } = owner;
    const rawUrl = invalid === 'U+FEFF-null' ? '\uFEFF' : '\u0085';
    const saved = { ...metadata(), name: 'Saved name',
      originalValues: { ...metadata().originalValues, name: 'Saved name', coverImage: { rawUrl } } };
    if (invalid === 'missing') delete saved.coverImage;
    if (invalid === 'blank-object') saved.coverImage = { rawUrl: '', proxiedUrl: '/Public/ProxyImage' };
    if (invalid === 'U+0085-object') saved.coverImage = { rawUrl, proxiedUrl: '/Public/ProxyImage' };
    let calls = 0;
    globalThis.fetch = async () => { calls++; return new Response(reply(false, saved)); };
    editor.draft.name = 'Saved name';
    await editor.saveAndExit();
    await editor.saveAndExit();
    assert.equal(calls, 1);
    assert.equal(parent.state.metadata.name, 'Trip');
    assert.equal(editor.draft.name, 'Saved name');
    assert.equal(editor.metadataUnconfirmed, true);
    assert.equal(editor.lastSavedAt, null);
    assert.deepEqual(owner.navigations, []);
  }));

// Name/cover use .NET whitespace; making private derives a sharing disable; clear center publishes nullable originals.
for (const clear of [false, true]) test(`M1: normalized submitted values and ${clear ? 'clears' : 'derived sharing'} are acknowledged`, () => withApp(async owner => {
  const { editor, parent } = owner;
  publishMetadata(parent, { ...metadata(true), notesHtml: 'Previous notes', center: { latitude: 10, longitude: 20 }, zoom: 5,
    coverImage: { rawUrl: 'https://example.test/old.jpg', proxiedUrl: '/Public/ProxyImage' },
    originalValues: { ...metadata(true).originalValues, notesHtml: 'Previous notes', center: { latitude: 10, longitude: 20 }, zoom: 5,
      coverImage: { rawUrl: 'https://example.test/old.jpg' } } });
  await nextTick();
  const saved = { ...metadata(), name: '\uFEFFSaved name\uFEFF', notesHtml: clear ? '' : 'Normalized notes', isPublic: false,
    center: clear ? null : { latitude: 1, longitude: 2 }, zoom: clear ? null : 3,
    coverImage: clear ? null : { rawUrl: 'https://example.test/new.jpg', proxiedUrl: '/Public/ProxyImage' } };
  saved.originalValues = { name: saved.name, notesHtml: saved.notesHtml, isPublic: false,
    center: clear ? { latitude: null, longitude: null } : saved.center, zoom: saved.zoom,
    coverImage: clear ? null : { rawUrl: saved.coverImage.rawUrl } };
  globalThis.fetch = async () => new Response(reply(false, saved));
  Object.assign(editor.draft, { name: '\u0085 \uFEFFSaved name\uFEFF \u0085', notesHtml: clear ? '' : '\u0085Normalized notes\u0085', isPublic: false,
    coverImageRawUrl: clear ? '' : '\u0085 https://example.test/new.jpg \u0085',
    centerLatitude: clear ? '' : 1, centerLongitude: clear ? '' : 2, zoom: clear ? '' : 3 });
  await editor.saveAndExit();
  assert.equal(editor.metadataUnconfirmed, false);
  assert.equal(editor.isDirty, false);
  assert.equal(parent.state.metadata.shareProgressEnabled, false);
  assert.equal(parent.state.metadata.name, saved.name);
  assert.deepEqual(owner.navigations, ['/User/Trip']);
}));

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
  await editor.saveAndExit();
  assert.equal(requests, 0);
  assert.equal(navigations.length, 0);
  assert.equal(confirmations.length, 1);
  assert.match(confirmations[0].message, /separately/i);
  owner.approve(true);
  await editor.backToTrips();
  assert.equal(navigations.length, 1);
  assert.equal(planning.workspace.hasChanges.value, true);
}));

test('late actual App metadata response preserves acknowledged planning and later edits on reopen', () => withApp(async ({ editor, planning, parent }) => {
  let releaseMetadata;
  const older = metadata();
  older.name = 'Changed Trip';
  older.originalValues.name = 'Changed Trip';
  globalThis.fetch = async (url, init) => {
    if (url.endsWith('/metadata/conditional')) return new Promise(resolve => { releaseMetadata = () => resolve(new Response(reply(false, older))); });
    const request = JSON.parse(init.body);
    return new Response(JSON.stringify({ contract: 'planning-1', success: true, applied: true,
      targetKind: 'trip', targetId: 'trip', parentId: 'trip', planning: request.value }));
  };
  editor.draft.name = 'Changed Trip';
  const saving = editor.save(false);
  assert.ok(releaseMetadata);
  planning.add();
  planning.draft.entries[0].when.value.local = '2027-03-02';
  await planning.workspace.save(planning.draft);
  assert.equal(parent.state.metadata.planning.entries[0].when.value.local, '2027-03-02');
  releaseMetadata();
  await saving;
  await nextTick();
  assert.equal(parent.state.metadata.planning.entries[0].when.value.local, '2027-03-02');
  const same = planning.workspace.open(editor.target);
  assert.equal(same.original.entries[0].when.value.local, '2027-03-02');
  same.entries[0].when.value.local = '2027-03-05';
  assert.equal(planning.workspace.dirty(same), true);
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
  // Dispatch the real producer before any Planning acknowledgement; its complete graph arrives later.
  const gate = deferred();
  globalThis.fetch = async () => { await gate.promise; return new Response(JSON.stringify(result)); };
  const older = patchMetadata('/api/trips/trip/editor', 'anti', {});
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
  gate.resolve();
  parent.applyMutation(await older);
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
  for (const local of ['2027-03-09', '2027-03-10']) {
    const result = currentPlanningResult(parent, local);
    globalThis.fetch = async () => new Response(JSON.stringify(result));
    parent.applyMutation(await patchMetadata('/api/trips/trip/editor', 'anti', {}));
    for (const draft of drafts) {
      const entity = draft.kind === 'trip' ? parent.state.metadata : parent.state[`${draft.kind}sById`][draft.id];
      assert.equal(entity.planning.entries[0].when.value.local, local);
      assert.equal(draft.original.entries[0].when.value.local, '2027-03-09');
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
