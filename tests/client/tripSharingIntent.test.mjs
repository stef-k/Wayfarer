import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { parse, compileScript, compileTemplate } from '@vue/compiler-sfc';
import { build } from 'esbuild';
import { createRenderer, nextTick, proxyRefs } from 'vue';

// Qualify the actual App -> TripSidebar -> MetadataEditor using the existing mounted production-client seam.
const bundle = await build({ stdin: { contents: 'export { default } from "./App.vue";', loader: 'ts',
  resolveDir: 'ClientApps/trip-editor/src' }, bundle: true, write: false, format: 'esm', platform: 'node', external: ['vue'],
  plugins: [{ name: 'c3-sharing-parent-tree', setup(builder) {
    builder.onLoad({ filter: /\.vue$/ }, async args => {
      if (/SegmentEditorSurface\.vue$/.test(args.path)) return { contents: `export default { setup(_, context) {
        context.expose({ focusNotes() {}, focusRouteAction: () => false }); return () => null;
      } };` };
      if (!/(?:App|TripSidebar|MetadataEditor|EditorSurface)\.vue$/.test(args.path)) return { contents: 'export default { render: () => null };' };
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
const actualApp = (await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`)).default;

// Self-contained client fixtures mirror the routed contract; no local database/evidence directory is needed.
const metadata = (enabled = false) => ({ id: 'trip', name: 'Trip', notesHtml: '', isPublic: true,
  shareProgressEnabled: enabled, center: null, zoom: null, coverImage: null,
  updatedAt: '2026-10-23T10:00:00Z', publicUrl: '/Public/Trips/trip', progressPublicUrl: enabled ? '/Public/Trips/trip?progress=1' : null,
  originalValues: { name: 'Trip', notesHtml: null, isPublic: true, center: null, zoom: null, coverImage: null } });
const initial = { tripId: 'trip', metadata: metadata(), regionsById: {}, regionOrder: [], placesById: {}, placeOrderByRegionId: {},
  areasById: {}, areaOrderByRegionId: {}, segmentsById: {}, segmentOrder: [], tagsBySlug: {}, tagOrder: [],
  visitProgress: { totalPlaces: 0, visitedPlaces: 0, percentVisited: 0, placeSummariesByPlaceId: {}, historyRows: [] },
  options: { iconNames: [], markerColorClasses: [], transportModes: [], tag: { maxTags: 10, suggestionTake: 5 } },
  permissions: { canEditMetadata: true } };
const reply = enabled => JSON.stringify({ success: true, data: metadata(enabled), affected: { metadata: metadata(enabled),
  regions: [], places: [], areas: [], segments: [], tags: [], regionOrder: null, segmentOrder: null, tagOrder: null,
  placeOrdersByRegionId: {}, areaOrdersByRegionId: {}, visitProgress: null, options: null },
  deletedIds: { regions: [], places: [], areas: [], segments: [], tags: [] }, warnings: [] });
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
    c3Confirm: globalThis.c3Confirm, captureC3Component: globalThis.captureC3Component };
  const captured = {};
  const navigations = [];
  const confirmations = [];
  let approved = false;
  globalThis.captureC3Component = (name, bindings) => { captured[name] = proxyRefs(bindings); };
  globalThis.c3Confirm = async options => { confirmations.push(options); return approved; };
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
  const renderer = createRenderer({ createElement: tag => ({ tagName: tag.toUpperCase(), style: {}, children: [],
    addEventListener() {}, removeEventListener() {}, setAttribute() {}, removeAttribute() {} }), createText: text => ({ text }), createComment: () => ({}),
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
    await action({ parent, editor, navigations, confirmations,
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
  parent.applyMetadata({ ...initial.metadata });
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
    parent.applyMetadata({ ...metadata(enabled), isPublic });
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
  assert.equal(owner.button('Save & Continue').props.disabled, true);
  assert.equal(owner.button('Save & Exit').props.disabled, true);
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
    parent.applyMetadata(refreshedState.metadata);
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
  assert.equal(owner.button('Save & Exit').props.disabled, false);
  await owner.button('Save & Exit').props.onClick();
  assert.equal(calls, 2);
  assert.equal(parent.state.metadata.shareProgressEnabled, true);
  assert.equal(editor.isDirty, false);
  assert.deepEqual(owner.navigations, ['/User/Trip']);
}));
