import assert from 'node:assert/strict';
import test from 'node:test';
import { build } from 'esbuild';

// The established in-memory TypeScript bundling seam runs the production Vue owner and HTTP adapter.
const bundle = await build({ stdin: { contents: `export * from './compositionState'; export * from './compositionApi';`,
  resolveDir: 'ClientApps/trip-editor/src/composition', loader: 'ts' }, bundle: true, write: false, platform: 'node', format: 'esm', external: ['vue'] });
const code = bundle.outputFiles[0].text.replaceAll('from "vue"', `from "${import.meta.resolve('vue')}"`);
const { createCompositionState, draftKey, compositionApi, editorUrl } = await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`);
const snapshot = id => ({ graph: { id, name: id, regions: [], segments: [], credits: [] }, regionOrder: [], placeOrder: {}, areaOrder: {} });
const workspace = () => ({ compositionId: 'workspace', tripId: 'C', sourceA: snapshot('A'), sourceB: snapshot('B'), current: snapshot('C'), mappings: [] });
const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
const setup = async () => {
  const store = createCompositionState('account', 'workspace'); await store.read(async () => workspace());
  store.state.view.selection = { side: 'b', kind: 'place', id: 'place' };
  const draft = { side: 'b', items: [{ name: 'Initial' }], baseline: {}, mappings: [] }; store.state.view.drafts['b/place/place'] = draft;
  return { store, draft: store.state.view.drafts['b/place/place'] };
};

test('submitted action is immutable; later typing survives acknowledgement and duplicate activation is disabled', async () => {
  const { store, draft } = await setup(); const wait = deferred(); let sent; let accepted = 0;
  const action = { source: 'b', items: [{ name: 'Submitted' }] };
  const attempt = store.submit(draft, action, body => { sent = body; return wait.promise; }, () => { accepted++; });
  action.items[0].name = 'Changed caller object'; draft.items[0].name = 'Later typing';
  await store.submit(draft, action, () => { throw new Error('Duplicate activation'); }, () => {});
  wait.resolve(workspace()); await attempt;
  assert.equal(sent.items[0].name, 'Submitted'); assert.equal(draft.items[0].name, 'Later typing'); assert.equal(accepted, 0);
  assert.match(store.state.status, /Newer choices remain unsaved/); assert.equal(store.state.pending, false);
});

test('same submitted input saves, while a different selection is never closed by the older reply', async () => {
  const { store, draft } = await setup(); const wait = deferred(); let accepted = 0;
  const attempt = store.submit(draft, { source: 'b', items: [] }, () => wait.promise, () => accepted++);
  store.state.view.selection.id = 'another'; wait.resolve(workspace()); await attempt;
  assert.equal(accepted, 0);
  store.state.view.selection.id = 'place';
  await store.submit(draft, { source: 'b', items: [] }, async () => workspace(), () => accepted++);
  assert.equal(accepted, 1); assert.equal(store.state.status, 'Saved to C.');
});

test('stale originals and uncertain communication retain all choices and require an explicit read before another action', async () => {
  const { store, draft } = await setup(); let saves = 0;
  await store.submit(draft, { source: 'b', items: [] }, async () => { saves++; throw new Error('C changed. Keep choices and review originals.'); }, () => {});
  assert.equal(draft.items[0].name, 'Initial'); assert.equal(store.state.needsInspection, true);
  await store.submit(draft, { source: 'b', items: [] }, async () => { saves++; return workspace(); }, () => {});
  assert.equal(saves, 1); await store.read(async () => workspace());
  assert.equal(store.state.needsInspection, false); assert.equal(store.state.view.drafts['b/place/place'].items[0].name, 'Initial');
});

test('read generations, response identity and retired context cannot replace another saved C', async () => {
  const { store, draft } = await setup(); const oldRead = deferred();
  const read = store.read(() => oldRead.promise);
  const newer = workspace(); newer.current.graph.name = 'Latest'; await store.read(async () => newer);
  oldRead.resolve(workspace()); await read; assert.equal(store.state.workspace.current.graph.name, 'Latest');
  await store.submit(draft, { source: 'b', items: [] }, async () => ({ ...workspace(), tripId: 'wrong' }), () => assert.fail('foreign acknowledgement'));
  assert.equal(store.state.workspace.tripId, 'C'); assert.match(store.state.error, /does not match/);
  await store.read(async () => workspace()); const late = deferred();
  const save = store.submit(draft, { source: 'b', items: [] }, () => late.promise, () => assert.fail('retired owner'));
  store.dispose(); late.resolve(newer); await save;
  assert.notEqual(store.state.workspace.current.graph.name, 'Latest');
});

test('ordinary edit/return restores drafts, selection, expansion and drawer only in the exact server/account/comparison partition', async () => {
  const { store, draft } = await setup(); draft.items[0].name = 'Retained draft'; store.state.view.expanded = ['b/region']; store.state.view.drawer = 60;
  const value = store.retain(); const restored = createCompositionState('account', 'workspace'); restored.restore(value);
  assert.equal(restored.state.view.drafts['b/place/place'].items[0].name, 'Retained draft'); assert.deepEqual([...restored.state.view.expanded], ['b/region']);
  assert.equal(restored.state.view.drawer, 60); assert.equal(restored.state.view.selection.id, 'place');
  assert.notEqual(draftKey('server', 'A', 'workspace'), draftKey('server', 'B', 'workspace'));
  assert.notEqual(draftKey('server', 'A', 'workspace'), draftKey('another', 'A', 'workspace'));
  assert.equal(value.includes('sourceA'), false); assert.equal(value.includes('conditionToken'), false);
  assert.equal(editorUrl('C', 'workspace'), '/User/Trip/Edit/C?composition=workspace');
});

test('a malformed success body keeps the saved graph and draft usable without claiming Saved', async () => {
  const { store, draft } = await setup();
  await store.submit(draft, { source: 'b', items: [] }, async () => ({ ...workspace(), current: null }), () => assert.fail('incomplete acknowledgement'));
  assert.equal(store.state.workspace.current.graph.id, 'C'); assert.equal(draft.items[0].name, 'Initial');
  assert.equal(store.state.needsInspection, true); assert.match(store.state.error, /incomplete/);
});

test('HTTP construction uses cookies/CSRF, never replays failures, and preserves uncertain-save wording', async () => {
  const calls = []; const api = compositionApi('csrf', async (url, init) => { calls.push({ url, init }); throw new Error('Connection broke'); });
  await assert.rejects(api.create({ sourceA: 'a', sourceB: 'b' }, false), /could not be confirmed.*Inspect or refresh/);
  assert.equal(calls.length, 1); assert.equal(calls[0].init.headers.RequestVerificationToken, 'csrf');
  assert.equal(calls[0].init.credentials, 'same-origin'); assert.equal(calls[0].init.redirect, 'error');
  assert.deepEqual(JSON.parse(calls[0].init.body), { sourceA: 'a', sourceB: 'b' });
});

test('coordinate preparation carries current parent/location originals and uses the domain preview token', async () => {
  const calls = []; const api = compositionApi('csrf', async (url, init) => {
    calls.push({ url, init }); return Response.json({ operation: 'coordinates', tripId: 'C', targetId: 'p', affectedSegmentIds: ['route'], conditionToken: 'domain-proof' });
  });
  const w = { ...workspace(), current: { graph: { regions: [{ id: 'region', places: [{ id: 'p' }] }] } }, sourceB: { graph: { regions: [{ id: 'source-region', places: [{ id: 'source', location: [121.1, 25.1] }] }] } } };
  const action = { source: 'b', items: [{ kind: 'place', sourceId: 'source', destinationId: 'p', expectedParentId: 'region', decision: 'reuse', fields: { location: { operation: 'useSource', expected: [121, 25] } } }] };
  assert.equal(await api.prepare(w, action, async message => { assert.match(message, /1 affected route/); return true; }), true);
  assert.equal(calls[0].url, '/api/trips/C/editor/places/p/coordinates/prepare');
  assert.deepEqual(JSON.parse(calls[0].init.body), { expected: { regionId: 'region', location: { latitude: 25, longitude: 121 } }, location: { latitude: 25.1, longitude: 121.1 } });
  assert.equal(action.items[0].conditionToken, 'domain-proof');
});
