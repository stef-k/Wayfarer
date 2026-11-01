import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { parse, compileScript } from '@vue/compiler-sfc';
import { build } from 'esbuild';
import { createRenderer, h, nextTick, provide, proxyRefs, reactive, ref } from 'vue';

// Mount the shipped review component/state owner; only rendering and transport are substituted.
const bundle = await build({ stdin: { contents: `export { default as Review } from './PlanCheck.vue'; export { default as Movement } from './CheckMovement.vue'; export * from './planningState';
  export * from '../composables/useEditorSurface';`, resolveDir: 'ClientApps/trip-editor/src/planning', loader: 'ts' },
  bundle: true, write: false, format: 'esm', platform: 'node', external: ['vue'], plugins: [{ name: 'review-owner', setup(builder) {
    builder.onLoad({ filter: /\.vue$/ }, async args => {
      const { descriptor } = parse(await readFile(args.path, 'utf8'), { filename: args.path });
      return { contents: compileScript(descriptor, { id: args.path }).content, loader: 'ts' };
    });
    builder.onLoad({ filter: /\.css$/ }, () => ({ contents: '', loader: 'js' }));
    builder.onLoad({ filter: /useConfirmDialog\.ts$/ }, () => ({ contents: 'export const confirm = async () => true;' }));
  } }] });
const code = bundle.outputFiles[0].text.replaceAll('from "vue"', `from "${import.meta.resolve('vue')}"`);
const { Review, Movement, createPlanningWorkspace, planningWorkspaceKey, useEditorSurface } = await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`);
const renderer = createRenderer({ createComment: () => ({}), insert() {}, remove() {}, parentNode: () => ({}), nextSibling: () => null });
const storage = () => {
  const values = new Map();
  return { values, getItem: key => values.get(key) ?? null, setItem: (key, value) => values.set(key, value), removeItem: key => values.delete(key) };
};
const entry = (id = 'visit', date = '2027-03-02') => ({ id, when: { form: 'at', value: { precision: 'date', local: date } } });
const graph = () => ({ tripId: 'trip', ownerId: 'alice', metadata: { id: 'trip', name: 'Trip', planning: { format: 1, entries: [entry()] } },
  regionsById: {}, placesById: {}, areasById: {}, segmentsById: {}, regionOrder: [], segmentOrder: [] });
const response = (setup, overrides = {}) => ({ contract: 'planning-check-1', ownerId: 'alice', tripId: 'trip', zoneRulesVersion: 'current',
  inventory: [{ key: 'trip/trip/visit', kind: 'trip', targetId: 'trip', parentId: 'trip', name: 'Trip', entry: entry(), selected: true, parentWindows: [], dependency: 'dep' }],
  findings: [{ id: 'context/1', check: 'context', entries: ['trip/trip/visit'], result: { status: 'mismatch', message: '30 minutes definitely outside', minutes: 30 } },
    { id: 'meaning/2', check: 'meaning', entries: ['trip/trip/visit'], result: { status: 'unknown', message: 'Meaning needed' } }],
  setup, reused: [], invalidated: [], ...overrides });
const mount = (state = graph(), surface = useEditorSurface()) => {
  const stateRef = ref(state);
  const workspace = createPlanningWorkspace(stateRef, '/api/trips/trip/editor', 'anti');
  const props = reactive({ state: stateRef.value, endpoint: '/api/trips/trip/editor', antiforgery: 'anti', ordinaryDirty: false, surface });
  let controls;
  const component = { ...Review, setup(values, context) { controls = proxyRefs(Review.setup(values, context)); return () => null; } };
  const app = renderer.createApp({ setup() { provide(planningWorkspaceKey, workspace); return () => h(component, props); } });
  app.mount({});
  return { stateRef, workspace, props, get controls() { return controls; }, dispose: () => app.unmount() };
};
const target = { key: 'metadata', identity: 'metadata', kind: 'metadata', mode: 'edit', title: 'Trip' };

// Save and check owns only the Planning versions present at the explicit action, across every save await.
test('Planning first typed during the ordinary save stays dirty and requires a new explicit choice', async () => {
  globalThis.localStorage = storage();
  const oldFetch = globalThis.fetch;
  const surface = useEditorSurface();
  let ordinaryDirty = true;
  let finishOrdinary;
  const unregister = surface.registerTargetHandler(target.key, { isDirty: () => ordinaryDirty, discard() {},
    save: async () => { await new Promise(resolve => { finishOrdinary = resolve; }); ordinaryDirty = false; } });
  await surface.activateTarget(target);
  const mounted = mount(graph(), surface);
  const calls = [];
  try {
    globalThis.fetch = async (url, init) => {
      const body = JSON.parse(init.body); calls.push({ url, body });
      return url.endsWith('/check') ? Response.json(response(body)) : Response.json({ contract: 'planning-1', success: true,
        applied: true, targetKind: 'trip', targetId: 'trip', parentId: 'trip', planning: body.value });
    };
    mounted.controls.review.request();
    const draft = mounted.workspace.open(target);
    const saving = mounted.controls.review.saveAndCheck();
    await nextTick();
    draft.entries[0].when.value.local = '2027-03-20';
    finishOrdinary(); await saving;
    assert.equal(calls.length, 0);
    assert.equal(mounted.workspace.hasChanges.value, true);
    assert.equal(draft.entries[0].when.value.local, '2027-03-20');
    assert.equal(mounted.controls.choices, true);
    assert.match(mounted.controls.error, /remain unsaved/);
    await mounted.controls.review.run();
    assert.equal(calls.length, 1);
    assert.equal(calls[0].url.endsWith('/check'), true);
    assert.equal(JSON.stringify(calls[0].body).includes('2027-03-20'), false);
    assert.equal(mounted.workspace.hasChanges.value, true);
    await mounted.controls.review.saveAndCheck();
    assert.equal(calls[1].body.value.entries[0].when.value.local, '2027-03-20');
    assert.equal(calls[2].url.endsWith('/check'), true);
    assert.equal(mounted.workspace.hasChanges.value, false);
  } finally { mounted.dispose(); unregister(); surface.clearActiveTarget(); globalThis.fetch = oldFetch; }
});

test('later draft edits or new draft membership during an earlier Planning save stop subsequent saves', async () => {
  const oldFetch = globalThis.fetch;
  // Exercise both version and membership changes at the same mounted protected-save seam.
  for (const newDraft of [false, true]) {
    globalThis.localStorage = storage();
    const state = graph();
    state.regionsById.region = { id: 'region', regionId: 'trip', name: 'Region', planning: { format: 1, entries: [entry('region-entry')] } };
    const mounted = mount(state);
    try {
      const first = mounted.workspace.open(target);
      first.entries[0].when.value.local = '2027-03-09';
      const regionTarget = { key: 'region/region', kind: 'region', entityId: 'region', mode: 'edit' };
      let later = newDraft ? null : mounted.workspace.open(regionTarget);
      if (later) later.entries[0].when.value.local = '2027-03-10';
      const calls = [];
      let acknowledge;
      globalThis.fetch = async (url, init) => {
        const body = JSON.parse(init.body); calls.push({ url, body });
        if (calls.length === 1) return new Promise(resolve => { acknowledge = resolve; });
        return url.endsWith('/check') ? Response.json(response(body)) : Response.json({ contract: 'planning-1', success: true,
          applied: true, targetKind: 'region', targetId: 'region', parentId: 'trip', planning: body.value });
      };
      mounted.controls.review.request();
      const saving = mounted.controls.review.saveAndCheck();
      await nextTick();
      later ??= mounted.workspace.open(regionTarget);
      later.entries[0].when.value.local = '2027-03-20';
      acknowledge(Response.json({ contract: 'planning-1', success: true, applied: true, targetKind: 'trip', targetId: 'trip', parentId: 'trip', planning: calls[0].body.value }));
      await saving;
      assert.equal(calls.length, 1);
      assert.equal(mounted.workspace.dirty(first), false);
      assert.equal(mounted.workspace.dirty(later), true);
      assert.equal(later.original.entries[0].when.value.local, '2027-03-02');
      assert.equal(later.entries[0].when.value.local, '2027-03-20');
      assert.equal(mounted.controls.choices, true);
      assert.match(mounted.controls.error, /remain unsaved/);
      assert.equal(mounted.controls.result, null);
    } finally { mounted.dispose(); globalThis.fetch = oldFetch; }
  }
});

test('allowance edits preserve relevance evidence; only source or explicit relevance assessment replaces it', () => {
  let controls;
  const setup = reactive({ transfers: [{ from: 'from', to: 'to', relevant: true, dependency: 'assessed-route',
    allowance: 'additional', allowanceMinutes: 15, connection: 'included', connectionMinutes: null }] });
  const component = { ...Movement, setup(values, context) { controls = proxyRefs(Movement.setup(values, context)); return () => null; } };
  const app = renderer.createApp({ setup: () => () => h(component, { setup, items: [], segments: {}, places: {} }) });
  app.mount({});
  try {
    const transfer = setup.transfers[0];
    transfer.allowanceMinutes = 10;
    controls.changed(transfer);
    assert.equal(transfer.dependency, 'assessed-route');
    assert.equal(transfer.relevant, true);
    assert.equal(transfer.connection, 'included');
    controls.sourceChanged(transfer);
    assert.equal(transfer.relevant, false);
    assert.equal(transfer.allowance, 'unknown');
    assert.equal(transfer.connection, 'unknown');
    transfer.relevant = true;
    controls.reassess(transfer);
    assert.equal(transfer.dependency, null);
    assert.equal(transfer.relevant, true);
  } finally { app.unmount(); }
});

test('scope selection retains traveller evidence for service membership revalidation', () => {
  globalThis.localStorage = storage();
  const mounted = mount();
  try {
    mounted.controls.setup.singleTraveller = true;
    mounted.controls.setup.travellerDependency = 'assessed-participants';
    mounted.controls.select({ key: 'trip/trip/visit' }, false);
    assert.equal(mounted.controls.setup.travellerDependency, 'assessed-participants');
  } finally { mounted.dispose(); }
});

test('mounted owner shows saved results before questions, keeps known findings beside Unknown and dismiss/recheck resurfaces', async () => {
  globalThis.localStorage = storage();
  const oldFetch = globalThis.fetch;
  const mounted = mount();
  const before = JSON.stringify(mounted.stateRef.value);
  const requests = [];
  try {
    globalThis.fetch = async (url, init) => { requests.push({ url, init }); return Response.json(response(JSON.parse(init.body))); };
    await mounted.controls.review.run();
    assert.equal(mounted.controls.summary.attention, 1);
    assert.equal(mounted.controls.summary.unknown, 1);
    assert.equal(mounted.controls.items.length, 1);
    mounted.controls.interpret('trip/trip/visit', 'allocationMinutes', 0);
    assert.equal(mounted.controls.setup.entries['trip/trip/visit'].allocationMinutes, 0);
    assert.deepEqual(JSON.parse(requests[0].init.body).entries, {});
    assert.equal(requests[0].url, '/api/trips/trip/editor/planning/check');
    assert.equal(requests[0].init.cache, 'no-store');
    assert.equal(requests[0].init.headers.RequestVerificationToken, 'anti');
    mounted.controls.dismissed.push('context/1');
    assert.equal(mounted.controls.findings.length, 1);
    assert.equal(mounted.controls.summary.attention, 1);
    await mounted.controls.review.run();
    assert.equal(mounted.controls.findings.length, 2);
    assert.equal(mounted.controls.dismissed.length, 0);
    assert.equal(JSON.stringify(mounted.stateRef.value), before);
    const retained = [...globalThis.localStorage.values.values()][0];
    assert.ok(retained);
    assert.equal(retained.includes('definitely outside'), false);
    assert.equal(retained.includes('findings'), false);
  } finally { mounted.dispose(); globalThis.fetch = oldFetch; }
});

test('dirty draft choices send only saved-version check inputs; Save and check uses protected acknowledged save', async () => {
  globalThis.localStorage = storage();
  const oldFetch = globalThis.fetch;
  const mounted = mount();
  try {
    const draft = mounted.workspace.open(target);
    draft.entries[0].when.value.local = '2027-03-09';
    const calls = [];
    globalThis.fetch = async (url, init) => {
      const body = JSON.parse(init.body); calls.push({ url, body });
      return url.endsWith('/check') ? Response.json(response(body)) : Response.json({ contract: 'planning-1', success: true,
        applied: true, targetKind: 'trip', targetId: 'trip', parentId: 'trip', planning: body.value });
    };
    mounted.controls.review.request();
    assert.equal(mounted.controls.choices, true);
    assert.equal(calls.length, 0);
    await mounted.controls.review.run();
    assert.equal(calls.length, 1);
    assert.equal(JSON.stringify(calls[0].body).includes('2027-03-09'), false);
    assert.equal(mounted.workspace.hasChanges.value, true);
    await mounted.controls.review.saveAndCheck();
    assert.equal(calls[1].url.endsWith('/planning/trip/trip'), true);
    assert.equal(calls[2].url.endsWith('/check'), true);
    assert.equal(mounted.workspace.hasChanges.value, false);
    assert.equal(mounted.stateRef.value.metadata.planning.entries[0].when.value.local, '2027-03-09');
    draft.entries[0].when.value.local = '2027-03-10';
    assert.equal(mounted.controls.stale, true);
  } finally { mounted.dispose(); globalThis.fetch = oldFetch; }
});

test('ordinary active handler is the actual shared save path; later typing and uncertain planning keep check held', async () => {
  globalThis.localStorage = storage();
  const oldFetch = globalThis.fetch;
  const surface = useEditorSurface();
  let ordinaryDirty = true;
  let ordinarySaves = 0;
  const unregister = surface.registerTargetHandler(target.key, { isDirty: () => ordinaryDirty, discard() {}, save: async () => { ordinarySaves++; ordinaryDirty = false; } });
  await surface.activateTarget(target);
  const mounted = mount(graph(), surface);
  try {
    globalThis.fetch = async (_, init) => Response.json(response(JSON.parse(init.body)));
    mounted.controls.review.request();
    assert.equal(mounted.controls.choices, true);
    await mounted.controls.review.saveAndCheck();
    assert.equal(ordinarySaves, 1);
    assert.ok(mounted.controls.result);
    const draft = mounted.workspace.open(target);
    draft.entries[0].when.value.local = '2027-03-03';
    let acknowledge;
    globalThis.fetch = async () => new Promise(resolve => { acknowledge = resolve; });
    const saving = mounted.controls.review.saveAndCheck();
    await nextTick();
    draft.entries[0].when.value.local = '2027-03-04';
    acknowledge(Response.json({ contract: 'planning-1', success: true, applied: true, targetKind: 'trip', targetId: 'trip', parentId: 'trip',
      planning: { format: 1, entries: [entry('visit', '2027-03-03')] } }));
    await saving;
    assert.match(mounted.controls.error, /remain unsaved/);
    draft.uncertain = true;
    assert.equal(mounted.controls.held, true);
    await mounted.controls.review.saveAndCheck();
    assert.equal(ordinarySaves, 1);
  } finally { mounted.dispose(); unregister(); surface.clearActiveTarget(); globalThis.fetch = oldFetch; }
});

test('newer review, later saved edits and new/deleted entries fence stale asynchronous results', async () => {
  globalThis.localStorage = storage();
  const oldFetch = globalThis.fetch;
  const mounted = mount();
  try {
    const pending = [];
    globalThis.fetch = async (_, init) => new Promise(resolve => pending.push({ resolve, setup: JSON.parse(init.body) }));
    const first = mounted.controls.review.run();
    const second = mounted.controls.review.run();
    pending[1].resolve(Response.json(response(pending[1].setup, { reused: ['newest'] })));
    await second;
    pending[0].resolve(Response.json(response(pending[0].setup, { reused: ['old'] })));
    await first;
    assert.deepEqual(mounted.controls.result.reused, ['newest']);
    const editing = mounted.controls.review.run();
    mounted.stateRef.value.metadata.planning.entries.push(entry('new'));
    assert.equal(mounted.controls.stale, true);
    pending[2].resolve(Response.json(response(pending[2].setup, { reused: ['stale'] })));
    await editing;
    assert.deepEqual(mounted.controls.result.reused, ['newest']);
    mounted.controls.review.request(target);
    assert.deepEqual(mounted.controls.setup.selection, ['trip/trip/visit', 'trip/trip/new']);
    mounted.stateRef.value.metadata.planning.entries.splice(0, 1);
    pending[3].resolve(Response.json(response(pending[3].setup)));
    await nextTick();
    assert.equal(mounted.controls.stale, true);
  } finally { mounted.dispose(); globalThis.fetch = oldFetch; }
});

test('latest inputs reuse on reopen, owner/Trip transitions clear private setup and cannot publish old replies', async () => {
  globalThis.localStorage = storage();
  const oldFetch = globalThis.fetch;
  let mounted = mount();
  try {
    globalThis.fetch = async (_, init) => Response.json(response(JSON.parse(init.body)));
    mounted.controls.setup.singleTraveller = true;
    await mounted.controls.review.run();
    mounted.dispose();
    mounted = mount();
    assert.equal(mounted.controls.setup.singleTraveller, true);
    let resolve;
    globalThis.fetch = async () => new Promise(done => { resolve = done; });
    const pending = mounted.controls.review.run();
    mounted.props.state = { ...graph(), ownerId: 'bob' };
    await nextTick();
    assert.equal(mounted.controls.result, null);
    assert.equal(mounted.controls.setup.singleTraveller, false);
    assert.equal(globalThis.localStorage.values.size, 0);
    resolve(Response.json(response({ selection: null, entries: {}, transfers: [], incompatible: {}, singleTraveller: true })));
    await pending;
    assert.equal(mounted.controls.result, null);
    mounted.props.state = { ...graph(), tripId: 'other-trip' };
    await nextTick();
    assert.equal(mounted.controls.setup.transfers.length, 0);
  } finally { mounted.dispose(); globalThis.fetch = oldFetch; }
});

test('denied owner access clears setup, a foreign-owner reply is fenced, and a disposed owner cannot publish', async () => {
  globalThis.localStorage = storage();
  const oldFetch = globalThis.fetch;
  const mounted = mount();
  try {
    globalThis.fetch = async () => new Response('', { status: 403 });
    mounted.controls.setup.singleTraveller = true;
    await mounted.controls.review.run();
    assert.equal(mounted.controls.setup.singleTraveller, false);
    assert.match(mounted.controls.error, /Owner access changed/);
    globalThis.fetch = async (_, init) => Response.json(response(JSON.parse(init.body), { ownerId: 'bob' }));
    await mounted.controls.review.run();
    assert.equal(mounted.controls.result, null);
    assert.match(mounted.controls.error, /Owner changed/);
    let resolve;
    globalThis.fetch = async () => new Promise(done => { resolve = done; });
    const pending = mounted.controls.review.run();
    mounted.dispose();
    resolve(Response.json(response({})));
    await pending;
    assert.equal(mounted.controls.result, null);
  } finally { mounted.dispose(); globalThis.fetch = oldFetch; }
});

test('blocked device storage leaves the mounted editor and optional review usable', async () => {
  const descriptor = Object.getOwnPropertyDescriptor(globalThis, 'localStorage');
  const oldFetch = globalThis.fetch;
  Object.defineProperty(globalThis, 'localStorage', { configurable: true, get() { throw new Error('Storage denied'); } });
  let mounted;
  try {
    mounted = mount();
    globalThis.fetch = async (_, init) => Response.json(response(JSON.parse(init.body)));
    await mounted.controls.review.run();
    assert.ok(mounted.controls.result);
    assert.match(mounted.controls.storageNotice, /unavailable/);
    assert.equal(mounted.workspace.hasChanges.value, false);
  } finally {
    mounted?.dispose(); globalThis.fetch = oldFetch;
    if (descriptor) Object.defineProperty(globalThis, 'localStorage', descriptor);
    else delete globalThis.localStorage;
  }
});
