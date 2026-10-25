import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { parse, compileScript } from '@vue/compiler-sfc';
import { build } from 'esbuild';
import { createRenderer, h, nextTick, proxyRefs, shallowReactive, ref } from 'vue';

// Mount the production Region draft owner with a real parent scheduler; stub only leaf rendering/DOM HTML normalization.
const filename = 'ClientApps/trip-editor/src/components/RegionManager.vue';
const { descriptor } = parse(await readFile(filename, 'utf8'), { filename });
const script = compileScript(descriptor, { id: 'region-conditional-test' });
const bundled = await build({ stdin: { contents: script.content, loader: 'ts',
  resolveDir: 'ClientApps/trip-editor/src/components' }, bundle: true, write: false, format: 'esm', platform: 'node',
  external: ['vue'], plugins: [{ name: 'region-leaves', setup(builder) {
    builder.onLoad({ filter: /\.vue$/ }, () => ({ contents: 'export default {};' }));
    builder.onLoad({ filter: /notesHtml\.ts$/ }, () => ({ contents: 'export const normalizeNotesHtml = value => value ?? "";' }));
    builder.onLoad({ filter: /useConfirmDialog\.ts$/ }, () => ({ contents: 'export const confirm = options => globalThis.regionConfirm(options);' }));
  } }] });
const code = bundled.outputFiles[0].text.replaceAll('from "vue"', `from "${import.meta.resolve('vue')}"`);
const component = (await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`)).default;
const region = (overrides = {}) => {
  const value = { id: 'region', tripId: 'trip', name: 'Original', notesHtml: '', coverImage: { rawUrl: 'https://old.test/a.jpg' },
    center: { latitude: 1, longitude: 2 }, displayOrder: 1, isShadow: false,
    capabilities: { canEdit: true, canDelete: true }, ...overrides };
  value.originalValues = { name: value.name, notesHtml: value.notesHtml || null,
    coverImage: value.coverImage, center: value.center };
  return value;
};

// Parent listeners replace state, leaving child props stale until Vue's normal update tick.
const mount = (initial = region()) => {
  let editor;
  const dirty = [];
  const applied = [];
  const handlers = new Map();
  const active = ref(null);
  const props = shallowReactive({ state: { tripId: 'trip', regionsById: { [initial.id]: initial }, regionOrder: [initial.id],
    placesById: {}, placeOrderByRegionId: {}, areasById: {}, areaOrderByRegionId: {}, options: { areaDefaults: { fillHex: '#ff6600' } } },
    editorSurface: { isTargetActive: target => active.value?.identity === target.identity,
      activateTarget: async target => { active.value = target; return true; },
      replaceActiveTarget: target => { active.value = target; },
      registerTargetHandler: (key, value) => { handlers.set(key, value); return () => handlers.delete(key); },
      closeActiveTarget: async () => { handlers.get('region-draft')?.discard(); active.value = null; return true; },
      clearActiveTarget: () => { active.value = null; }, mapWork: ref(null) },
    editorEndpoint: '/api/trips/trip/editor', antiforgeryToken: 'anti', searchActive: false, selectedPlaceId: null,
    searchRegions: [], searchPlaceIdsByRegionId: {}, searchAreaIdsByRegionId: {}, pendingSearchAdd: null,
    coordinatePicker: {}, polygonEditor: {}, selectPlace: async () => true, clearSelectedPlace: async () => true });
  const update = next => { props.state = { ...props.state, regionsById: { ...props.state.regionsById, [next.id]: next } }; };
  const child = { ...component, setup(childProps, context) {
    editor = proxyRefs(component.setup(childProps, context));
    return () => null;
  } };
  const renderer = createRenderer({ createComment: () => ({}), insert() {}, remove() {}, parentNode: () => ({}), nextSibling: () => null });
  const app = renderer.createApp({ setup: () => () => h(child, { ...props,
    onMutationApplied: result => { applied.push(result); for (const next of result.affected.regions) update(next); if (result.affected.regionOrder) props.state = { ...props.state, regionOrder: result.affected.regionOrder }; },
    onDirtyStateChanged: value => dirty.push(value) }) });
  app.mount({});
  return { editor, props, update, dirty, applied, handlers, dispose: () => app.unmount() };
};
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; };
const saved = data => new Response(JSON.stringify({ success: true, data, affected: { regions: [data] }, warnings: [] }));
const withGlobals = async action => {
  const previous = { document: globalThis.document, localStorage: globalThis.localStorage, location: globalThis.location,
    fetch: globalThis.fetch, window: globalThis.window, regionConfirm: globalThis.regionConfirm };
  const held = new Map();
  globalThis.document = { getElementById: () => ({ dataset: { accountId: 'account', tripId: 'trip' } }) };
  globalThis.localStorage = { getItem: key => held.get(key) ?? null };
  globalThis.location = { origin: 'https://server.test' };
  globalThis.window = {};
  globalThis.regionConfirm = async () => false;
  try { await action(held); } finally { Object.assign(globalThis, previous); }
};

test('first edit pins exact nullable originals; parent refresh updates only clean fields', () => withGlobals(async () => {
  const owner = mount();
  const calls = [];
  globalThis.fetch = async (url, init) => { calls.push([url, init]); return saved(region({ name: 'Server rename', notesHtml: 'Mine' })); };
  try {
    await owner.editor.openEdit(owner.props.state.regionsById.region);
    assert.equal(owner.editor.regionDirty, false);
    owner.editor.draft.notesHtml = 'Mine';
    owner.update(region({ name: 'Server rename', notesHtml: 'Other device', coverImage: null, center: null }));
    assert.equal(owner.editor.draft.name, 'Original', 'parent update has not propagated yet');
    await nextTick();
    assert.equal(owner.editor.draft.notesHtml, 'Mine');
    assert.equal(owner.editor.draft.name, 'Server rename');
    assert.equal(owner.editor.draft.coverImageRawUrl, '');
    await owner.editor.saveDraft();
    assert.equal(calls[0][0], '/api/trips/trip/editor/regions/region/conditional');
    assert.equal(calls[0][1].method, 'PATCH');
    assert.equal(calls[0][1].headers.RequestVerificationToken, 'anti');
    assert.deepEqual(JSON.parse(calls[0][1].body), { expected: { notesHtml: null }, changes: { notesHtml: 'Mine' } });
    await nextTick();
    assert.equal(owner.editor.regionDirty, false);
    assert.equal(owner.dirty.at(-1), false);
  } finally { owner.dispose(); }
}));

for (const newer of ['Newer edit', 'Original']) {
  test(`delayed reply preserves ${newer}, including a reversal and unrelated refresh`, () => withGlobals(async () => {
    const owner = mount();
    const reply = deferred();
    const calls = [];
    globalThis.fetch = async (_url, init) => { calls.push(JSON.parse(init.body)); return reply.promise; };
    try {
      await owner.editor.openEdit(owner.props.state.regionsById.region);
      owner.editor.draft.name = 'Submitted';
      const saving = owner.editor.saveDraft();
      owner.editor.draft.name = newer;
      owner.editor.draft.notesHtml = 'Unsent newer notes';
      owner.update(region({ name: 'During wait', notesHtml: 'During wait' }));
      await nextTick();
      assert.equal(owner.editor.draft.name, newer);
      assert.equal(owner.editor.regionDirty, true, 'pending earlier writes retain navigation protection even after a reversal');
      reply.resolve(saved(region({ name: 'Submitted' })));
      await saving;
      await nextTick();
      assert.equal(owner.props.state.regionsById.region.name, 'Submitted');
      assert.equal(owner.editor.draft.name, newer);
      assert.equal(owner.editor.draft.notesHtml, 'Unsent newer notes');
      assert.equal(owner.editor.regionDirty, true);
      owner.editor.draft.coverImageRawUrl = 'https://new.test/cover.jpg'; // First edit after response.
      globalThis.fetch = async (_url, init) => { calls.push(JSON.parse(init.body)); return saved(region({ name: newer,
        notesHtml: 'Unsent newer notes', coverImage: { rawUrl: 'https://new.test/cover.jpg' } })); };
      await owner.editor.saveDraft();
      assert.deepEqual(calls[1].expected, { name: 'Submitted', notesHtml: null, coverImage: { rawUrl: 'https://old.test/a.jpg' } });
      await nextTick();
      assert.equal(owner.editor.regionDirty, false);
    } finally { owner.dispose(); }
  }));
}

test('conflict retains draft and original through props; explicit reset adopts reviewed current values', () => withGlobals(async () => {
  const owner = mount();
  const calls = [];
  globalThis.fetch = async (_url, init) => { calls.push(JSON.parse(init.body)); return new Response(JSON.stringify({ code: 'mutation-base-stale', detail: 'Review current Region fields.' }), { status: 409 }); };
  try {
    await owner.editor.openEdit(owner.props.state.regionsById.region);
    owner.editor.draft.name = 'Mine';
    await owner.editor.saveDraft();
    owner.update(region({ name: 'Other device' }));
    await nextTick();
    assert.equal(owner.editor.draft.name, 'Mine');
    assert.ok(owner.editor.saveError);
    assert.equal(owner.editor.saveError, 'Review current Region fields.');
    await owner.editor.saveDraft();
    assert.deepEqual(calls[0], calls[1]);
    owner.editor.resetDraft();
    assert.equal(owner.editor.draft.name, 'Other device');
    assert.equal(owner.editor.regionDirty, false);
    assert.equal(owner.editor.saveError, null);
    owner.editor.draft.name = 'Reviewed';
    await owner.editor.saveDraft();
    assert.deepEqual(calls[2].expected, { name: 'Other device' });
    [...owner.handlers.values()][0].discard();
    assert.equal(owner.editor.draft.id, null);
    assert.equal(owner.editor.regionDirty, false);
  } finally { owner.dispose(); }
}));

test('lost reply holds a newer reversal and never blindly resends after props refresh', () => withGlobals(async () => {
  const owner = mount();
  const reply = deferred();
  let calls = 0;
  globalThis.fetch = async () => { calls++; return reply.promise; };
  try {
    await owner.editor.openEdit(owner.props.state.regionsById.region);
    owner.editor.draft.name = 'Earlier pending edit';
    const saving = owner.editor.saveDraft();
    owner.editor.draft.name = 'Original';
    reply.resolve(new Response('{}', { status: 500 }));
    await saving;
    owner.update(region({ name: 'Earlier pending edit' }));
    await nextTick();
    assert.equal(owner.editor.draft.name, 'Original');
    assert.equal(owner.editor.regionDirty, true);
    await owner.editor.saveDraft();
    assert.equal(calls, 1);
    assert.match(owner.editor.saveError, /unknown outcome/);
    owner.editor.resetDraft();
    assert.equal(owner.editor.draft.name, 'Earlier pending edit');
    assert.equal(owner.editor.regionDirty, false);
  } finally { owner.dispose(); }
}));

for (const reason of ['missing originals', 'unsupported route', 'deletion held']) {
  test(`dispatch fails closed for ${reason}`, () => withGlobals(async held => {
    const initial = region();
    if (reason === 'missing originals') delete initial.originalValues;
    const owner = mount(initial);
    const calls = [];
    globalThis.fetch = async (url, init) => { calls.push([url, init.method]); return new Response('{}', { status: 404 }); };
    try {
      await owner.editor.openEdit(initial);
      owner.editor.draft.name = 'Mine';
      if (reason === 'deletion held') held.set([...held.keys()][0] ?? 'unused', 'held');
      if (reason === 'deletion held') globalThis.localStorage.getItem = () => 'held';
      await owner.editor.saveDraft();
      assert.deepEqual(calls.map(call => call[1]), reason === 'unsupported route' ? ['PATCH'] : []);
      assert.equal(owner.editor.draft.name, 'Mine');
      assert.equal(owner.editor.regionDirty, true);
      assert.ok(owner.editor.saveError);
    } finally { owner.dispose(); }
  }));
}

test('numeric center/Clear remain paired and creation sends pinned originals', () => withGlobals(async () => {
  const owner = mount();
  const calls = [];
  globalThis.fetch = async (url, init) => { calls.push([url, init.method, JSON.parse(init.body)]); return saved(region({ center: null, coverImage: null })); };
  try {
    await owner.editor.openEdit(owner.props.state.regionsById.region);
    owner.editor.draft.centerLatitude = 0;
    owner.editor.draft.centerLongitude = 0;
    await owner.editor.saveDraft();
    assert.deepEqual(calls[0][2].changes, { center: { latitude: 0, longitude: 0 } });
    await nextTick();
    owner.editor.draft.coverImageRawUrl = 'https://new.test/a';
    owner.editor.draft.centerLatitude = 0;
    owner.editor.draft.centerLongitude = 0;
    owner.editor.resetDraft();
    assert.equal(owner.editor.regionDirty, false);
    await owner.editor.openCreate();
    assert.equal(owner.editor.regionDirty, false);
    owner.editor.draft.notesHtml = 'Created notes';
    await owner.editor.saveDraft();
    assert.equal(calls[1][1], 'POST');
    assert.equal(calls[1][0], '/api/trips/trip/editor/regions/conditional');
    assert.equal(calls[1][2].value.notesHtml, 'Created notes');
    assert.deepEqual(calls[1][2].expected, { regions: [{ id: 'region', displayOrder: 1, isCanonical: false }] });
  } finally { owner.dispose(); }
}));

const createdRegionId = '20000000-0000-0000-0000-000000000002';
/** Mirrors the committed creation projection, including exact membership effects and originals. */
const createdRegionReply = () => {
  const data = region({ id: createdRegionId, name: 'New Region', notesHtml: '', coverImage: null, center: null,
    displayOrder: 2, capabilities: { canEdit: true, canRename: true, canDelete: true, canReorder: true, canMove: false, canAddChildren: true, canTargetForSearchAdd: true } });
  return { contract: 'bd2-1', success: true, data, affected: { metadata: null, regions: [data], regionOrder: ['region', createdRegionId],
    places: [], placeOrdersByRegionId: { [createdRegionId]: [] }, areas: [], areaOrdersByRegionId: { [createdRegionId]: [] },
    segments: [], segmentOrder: null, tags: [], tagOrder: null, visitProgress: null, options: null },
    deletedIds: { regions: [], places: [], areas: [], segments: [], tags: [] }, warnings: [] };
};
for (const outcome of ['success', 'network', 'malformed', 'newer', 'retired']) {
  test(`Region creation ${outcome} validates before adoption and retains pinned originals`, () => withGlobals(async () => {
    const owner = mount();
    try {
      await owner.editor.openCreate();
      const pending = deferred();
      const calls = [];
      globalThis.fetch = async (url, init) => { calls.push([url, JSON.parse(init.body)]); return pending.promise; };
      const save = owner.editor.saveDraft();
      if (outcome === 'newer') owner.editor.draft.name = 'Newer creation intent';
      if (outcome === 'retired') {
        await owner.props.editorSurface.closeActiveTarget();
        await owner.editor.openEdit(owner.props.state.regionsById.region);
        owner.editor.draft.name = 'Retired reply must preserve me';
      }
      owner.update(region({ displayOrder: 11 }));
      await nextTick();
      const result = createdRegionReply();
      pending.resolve(outcome === 'network' ? Promise.reject(new Error('Lost reply'))
        : new Response(JSON.stringify(outcome === 'malformed' ? { success: true, data: { id: createdRegionId } } : result)));
      await save;
      await nextTick();
      assert.deepEqual(calls[0][1].expected, { regions: [{ id: 'region', displayOrder: 1, isCanonical: false }] });
      if (outcome === 'success' || outcome === 'newer') {
        assert.equal(owner.editor.draft.id, createdRegionId);
        assert.equal(owner.props.state.regionsById[createdRegionId].name, 'New Region');
        assert.equal(owner.editor.regionDirty, outcome === 'newer');
        if (outcome === 'newer') assert.equal(owner.editor.draft.name, 'Newer creation intent');
      } else {
        assert.equal(owner.props.state.regionsById[createdRegionId], undefined);
        assert.equal(owner.editor.regionDraftState.creationUncertain.value, true);
        assert.equal(owner.editor.regionDirty, true);
        if (outcome !== 'retired') {
          owner.editor.resetDraft();
          await owner.editor.saveDraft();
          assert.equal(calls.length, 1);
        } else assert.equal(owner.editor.draft.name, 'Retired reply must preserve me');
      }
    } finally { owner.dispose(); }
  }));
}

const orderCapabilities = { canEdit: true, canRename: true, canDelete: true, canReorder: true, canMove: false, canAddChildren: true, canTargetForSearchAdd: true };
/** Complete committed order envelope, preserving material and returning no unrelated effects. */
const orderReply = request => {
  const regions = request.regionIds.map((id, index) => {
    const original = request.expected.regions.find(r => r.id === id);
    // The mapper hides blank covers in display data but preserves exact storage in originalValues.
    const next = region({ id, name: original.name, notesHtml: original.notes ?? '', displayOrder: index + 1, capabilities: orderCapabilities,
      coverImage: original.coverImageUrl === null || /^\p{White_Space}*$/u.test(original.coverImageUrl)
        ? null : { rawUrl: original.coverImageUrl, proxiedUrl: '/image' }, center: original.center });
    next.originalValues.coverImage = original.coverImageUrl === null ? null : { rawUrl: original.coverImageUrl };
    return next;
  });
  const order = [...request.regionIds, ...(request.expected.canonical ? [request.expected.canonical.id] : [])];
  return { contract: 'bd2-1', success: true, data: { tripId: 'trip', regionOrder: order },
    affected: { metadata: null, regions, regionOrder: order, places: [], placeOrdersByRegionId: {},
      areas: [], areaOrdersByRegionId: {}, segments: [], segmentOrder: null, tags: [], tagOrder: null, visitProgress: null, options: null },
    deletedIds: { regions: [], places: [], areas: [], segments: [], tags: [] }, warnings: [] };
};

// Exercise display/original separation through the mounted production prepare/apply/adoption owner.
for (const [label, cover, outcome] of [
  ['empty', '', 'success'], ['whitespace', ' ', 'success'], ['null', null, 'success'],
  ['Unicode whitespace', '\t\u0085', 'success'], ['padded URL', ' https://old.test/a.jpg ', 'success'],
  ['normalized original', ' ', 'normalized'], ['blank display object', '', 'display'], ['missing display', '', 'missing']
]) {
  test(`Region order cover ${label} preserves exact evidence and validates projection`, () => withGlobals(async () => {
    const initial = region({ displayOrder: 33, capabilities: orderCapabilities,
      coverImage: cover === null || /^\p{White_Space}*$/u.test(cover) ? null : { rawUrl: cover, proxiedUrl: '/image' } });
    initial.originalValues.coverImage = cover === null ? null : { rawUrl: cover };
    const owner = mount(initial);
    const originals = { regions: [{ id: initial.id, displayOrder: initial.displayOrder, name: initial.originalValues.name,
      notes: initial.originalValues.notesHtml, coverImageUrl: cover, center: initial.originalValues.center }], canonical: null };
    const calls = [];
    globalThis.regionConfirm = async () => true;
    globalThis.fetch = async (url, init) => {
      const request = JSON.parse(init.body); calls.push(request);
      if (url.endsWith('/prepare')) return new Response(JSON.stringify({ contract: 'bd2-1', operation: 'region-order', tripId: 'trip',
        requiresReview: true, conditionToken: 'conditions', ...request.expected }));
      const reply = orderReply(request);
      if (outcome === 'normalized') reply.affected.regions[0].originalValues.coverImage = null;
      if (outcome === 'display') reply.affected.regions[0].coverImage = { rawUrl: '', proxiedUrl: '/image' };
      if (outcome === 'missing') delete reply.affected.regions[0].coverImage;
      return new Response(JSON.stringify(reply));
    };
    try {
      await owner.editor.openEdit(initial);
      await owner.editor.reorderRegions([initial.id], [initial.id], originals);
      await nextTick();
      assert.equal(calls.length, 2);
      assert.deepEqual(calls.map(request => request.expected), [originals, originals]);
      const accepted = outcome === 'success';
      assert.equal(owner.applied.length, accepted ? 1 : 0);
      assert.equal(owner.editor.regionDraftState.orderUncertain.value, !accepted);
      assert.equal(owner.editor.regionDirty, !accepted);
      assert.deepEqual(owner.props.state.regionsById.region.originalValues.coverImage, initial.originalValues.coverImage);
      if (accepted) {
        assert.equal(owner.props.state.regionsById.region.displayOrder, 1);
        assert.match(owner.editor.statusText, /^Saved/);
      } else {
        assert.equal(owner.props.state.regionsById.region.displayOrder, 33);
        assert.doesNotMatch(owner.editor.statusText, /^Saved/);
        owner.editor.resetDraft();
        await owner.editor.reorderRegions([initial.id], [initial.id], originals);
        assert.equal(calls.length, 2, 'malformed replies retain the unknown hold without replay');
        assert.equal(owner.editor.regionDirty, true);
      }
    } finally { owner.dispose(); }
  }));
}

for (const outcome of ['success', 'newer', 'retired', 'network', 'wrong-scope', 'wrong-material', 'unrelated-effects', 'rejected']) {
  test(`Region order ${outcome}: pinned originals, guarded adoption and page-local unknown hold`, () => withGlobals(async () => {
    const owner = mount(region({ capabilities: orderCapabilities }));
    globalThis.regionConfirm = async () => true;
    const pending = deferred();
    const calls = [];
    try {
      owner.props.state = { ...owner.props.state, regionOrder: ['region', 'second', 'canonical'], regionsById: {
        region: owner.props.state.regionsById.region, second: region({ id: 'second', displayOrder: 8, capabilities: orderCapabilities }),
        canonical: region({ id: 'canonical', name: 'Unassigned Places', displayOrder: 17, isShadow: true }) } };
      await nextTick();
      await owner.editor.openEdit(owner.props.state.regionsById.region);
      globalThis.fetch = async (url, init) => {
        const request = JSON.parse(init.body); calls.push([url, request]);
        if (url.endsWith('/prepare')) return new Response(JSON.stringify({ contract: 'bd2-1', operation: 'region-order', tripId: 'trip',
          requiresReview: true, conditionToken: 'conditions', ...request.expected }));
        return pending.promise;
      };
      const originals = { regions: ['region', 'second'].map(id => {
        const r = owner.props.state.regionsById[id];
        return { id, displayOrder: r.displayOrder, name: r.originalValues.name, notes: r.originalValues.notesHtml,
          coverImageUrl: r.originalValues.coverImage?.rawUrl ?? null, center: r.originalValues.center };
      }), canonical: { id: 'canonical', displayOrder: 17 } };
      owner.update(region({ name: 'Refresh after gesture start', displayOrder: 41, capabilities: orderCapabilities }));
      await nextTick();
      const saving = owner.editor.reorderRegions(['second', 'region'], ['region', 'second'], originals);
      for (let turn = 0; calls.length < 2 && turn < 50; turn++) await new Promise(resolve => setImmediate(resolve));
      assert.equal(calls.length, 2);
      assert.equal(owner.editor.regionDirty, true, 'pending orders fence navigation');
      if (outcome === 'newer') owner.editor.draft.name = 'Newer scalar intent';
      if (outcome === 'retired') owner.editor.resetDraft();
      owner.update(region({ name: 'Refresh during apply', capabilities: orderCapabilities }));
      await nextTick();
      const result = orderReply(calls[1][1]);
      if (outcome === 'wrong-scope') result.data.tripId = 'foreign-trip';
      if (outcome === 'wrong-material') result.affected.regions[0].originalValues.notesHtml = 'Unexpected';
      if (outcome === 'unrelated-effects') result.affected.placeOrdersByRegionId.foreign = [];
      pending.resolve(outcome === 'network' ? Promise.reject(new Error('Lost committed reply')) : outcome === 'rejected'
        ? new Response(JSON.stringify({ title: 'Stale base', code: 'mutation-base-stale' }), { status: 409 }) : new Response(JSON.stringify(result)));
      await saving; await nextTick();
      assert.deepEqual(calls[1][1].expected.canonical, { id: 'canonical', displayOrder: 17 });
      assert.equal(calls[1][1].expected.regions.find(r => r.id === 'region').name, 'Original');
      assert.deepEqual(calls[1][1].regionIds, ['second', 'region']);
      const accepted = outcome === 'success' || outcome === 'newer';
      const unknown = ['network', 'wrong-scope', 'wrong-material', 'unrelated-effects'].includes(outcome);
      assert.equal(owner.applied.length, accepted ? 1 : 0);
      assert.equal(owner.editor.regionDraftState.orderUncertain.value, unknown);
      if (outcome === 'newer') { assert.equal(owner.editor.draft.name, 'Newer scalar intent'); assert.equal(owner.editor.regionDirty, true); }
      if (outcome === 'success') { assert.equal(owner.editor.regionDirty, false); assert.match(owner.editor.statusText, /^Saved/); }
      if (unknown) {
        assert.deepEqual(owner.editor.regionDraftState.orderIntent.value.regionIds, ['second', 'region']);
        assert.equal(owner.editor.regionDirty, true); assert.doesNotMatch(owner.editor.statusText, /^Saved/);
        owner.editor.resetDraft();
        await owner.editor.reorderRegions(['second', 'region'], ['region', 'second'], originals);
        assert.equal(calls.length, 2, 'no blind replay after reset or refresh');
        assert.equal(owner.editor.regionDirty, true);
      }
    } finally { owner.dispose(); }
  }));
}

for (const newer of [false, true]) test(`Region deletion preserves a ${newer ? 'superseding session' : 'newer draft'} after checked success`, () => withGlobals(async held => {
  const element = { dataset: { accountId: 'account', tripId: 'trip' } };
  globalThis.document.getElementById = () => element;
  globalThis.localStorage = { getItem: key => held.get(key) ?? null, setItem: (key, value) => held.set(key, value), removeItem: key => held.delete(key) };
  globalThis.window = { fetch: (...args) => globalThis.fetch(...args), dispatchEvent() {} };
  let dialog;
  globalThis.regionConfirm = async options => { dialog = options; return false; };
  const sample = () => ({ count: 0, ids: [], hasMore: false });
  const preview = { contractVersion: 1, tripId: 'trip', sourceRegionId: 'region', sourceRegionName: 'Original', places: 'keep', areas: 'keep',
    destination: { mode: 'not-required', regionId: null, name: null }, consequences: Object.fromEntries(['movedPlaces','deletedPlaces','movedAreas','deletedAreas','deletedSegments','updatedSegments','detachedWaypoints'].map(key => [key, sample()])),
    confirmation: { token: 'consent', expiresAt: new Date(Date.now() + 600000).toISOString() } };
  const result = { success: true, data: { tripId: 'trip', sourceRegionId: 'region', places: 'keep', areas: 'keep', destinationRegionId: null,
    movedPlaceIds: [], movedAreaIds: [], updatedSegmentIds: [], detachedWaypointAssociations: [] }, deletedIds: { regions: ['region'], places: [], areas: [], segments: [], tags: [] }, warnings: [],
    affected: { metadata: { id: 'trip', name: 'Trip', notesHtml: '', isPublic: false, shareProgressEnabled: false, center: null, zoom: null, coverImage: null, publicUrl: null, progressPublicUrl: null, updatedAt: '2026-10-25T00:00:00Z', originalValues: { name: 'Trip', notesHtml: null } }, regions: [], regionOrder: [], places: [], areas: [], segments: [], tags: [],
      placeOrdersByRegionId: {}, areaOrdersByRegionId: {}, segmentOrder: null, tagOrder: null, options: null, visitProgress: { totalPlaces: 0, visitedPlaces: 0, percentVisited: 0, placeSummariesByPlaceId: {}, historyRows: [] } } };
  const pending = deferred();
  globalThis.fetch = async url => url.endsWith('deletion-preview') ? new Response(JSON.stringify(preview)) : pending.promise;
  const owner = mount();
  try {
    await owner.editor.openEdit(owner.props.state.regionsById.region);
    await owner.editor.deleteDraftRegion();
    for (let tick = 0; tick < 5; tick++) await new Promise(done => setImmediate(done));
    const submitting = dialog.regionDeletion.submit();
    if (newer) owner.editor.regionDraftState.reset(region({ id: 'later' }));
    owner.editor.draft.name = 'Newer intent';
    pending.resolve(new Response(JSON.stringify(result)));
    assert.equal(await submitting, true);
    assert.equal(owner.editor.draft.name, 'Newer intent');
    assert.equal(owner.editor.draft.id, newer ? 'later' : 'region');
    assert.equal(owner.applied.length, 1); assert.equal(held.size, 0);
  } finally { owner.dispose(); }
}));
