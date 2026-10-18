import assert from 'node:assert/strict';
import test from 'node:test';
import { createRegionDeletionController, regionDeletionLabel, heldRegionDeletionKey } from '../../ClientApps/trip-editor/src/components/regionDeletionState.ts';

const preview = (places = 'keep', areas = 'keep', token = 'approval') => ({ contractVersion: 1, tripId: 'trip', sourceRegionId: 'source', sourceRegionName: 'Source', places, areas,
  destination: { mode: 'existing', regionId: 'dest', name: 'Unassigned Places' }, consequences: {}, confirmation: { token, expiresAt: new Date(Date.now() + 600000).toISOString() } });
const response = (body, status = 200) => new Response(JSON.stringify(body), { status });
const setup = (fetch) => {
  const values = new Map(); const applied = [];
  const storage = { getItem: k => values.get(k) ?? null, setItem: (k,v) => values.set(k,v), removeItem: k => values.delete(k) };
  let current = true;
  const options = { sourceId: 'source', tripId: 'trip', endpoint: '/editor', antiforgeryToken: 'csrf', storage,
    scopeKey: heldRegionDeletionKey('server','account','trip'), isCurrent: () => current, fetch, onSuccess: x => applied.push(x) };
  return { controller: createRegionDeletionController(options), storage, values, applied, options, switchAccount: () => { current = false; } };
};
const deferred = () => { let resolve; const promise = new Promise(r => { resolve = r; }); return { promise, resolve }; };

for (const places of ['keep','delete']) for (const areas of ['keep','delete']) {
  test(`explicit ${places}/${areas} choice obtains matching confirmation and contextual action`, async () => {
    const calls = [];
    const s = setup(async (url, init) => { calls.push([url, JSON.parse(init.body)]); return response(preview(JSON.parse(init.body).places, JSON.parse(init.body).areas)); });
    assert.equal(s.controller.state.places, 'keep'); assert.equal(s.controller.state.areas, 'keep');
    await s.controller.select('places', places); await s.controller.select('areas', areas);
    assert.equal(s.controller.state.preview.places, places); assert.equal(s.controller.state.preview.areas, areas);
    assert.match(regionDeletionLabel({ places, areas }), /^Delete Region/);
    assert.ok(calls.every(([url]) => url.endsWith('/deletion-preview'))); // Cancel requires no mutation dispatch, including empty previews.
    assert.equal(s.values.size, 0);
  });
}

test('out-of-order previews cannot restore old choice approval; final activation is still required', async () => {
  const first = deferred(); const second = deferred(); let calls = 0;
  const s = setup(() => (++calls === 1 ? first.promise : second.promise));
  const a = s.controller.refresh(); const b = s.controller.select('areas', 'delete');
  assert.equal(s.controller.state.preview, null); assert.equal(await s.controller.submit(), false);
  second.resolve(response(preview('keep','delete','new'))); await b;
  first.resolve(response(preview())); await a;
  assert.equal(s.controller.state.preview.confirmation.token, 'new'); assert.equal(s.values.size, 0);
});

test('definite stale rejection leaves drafts untouched and requires fresh preview and final activation', async () => {
  let count = 0;
  const s = setup(async url => url.endsWith('deletion-preview') ? response(preview()) : (count++, response({ code: 'region-deletion-preview-stale', title: 'Contents changed. Refresh preview.' }, 409)));
  const draft = { notes: 'unsaved' };
  await s.controller.refresh(); assert.equal(await s.controller.submit(), false);
  assert.equal(s.controller.state.preview, null); assert.equal(s.values.size, 0);
  assert.equal(await s.controller.submit(), false); assert.equal(count, 1); assert.equal(draft.notes, 'unsaved'); assert.deepEqual(s.applied, []);
});

test('double submit is suppressed and authoritative slices apply while the attempt is still held', async () => {
  const pending = deferred(); let dispatches = 0;
  const s = setup(async url => url.endsWith('deletion-preview') ? response(preview()) : (dispatches++, pending.promise));
  await s.controller.refresh(); const first = s.controller.submit(); assert.equal(await s.controller.submit(), false);
  assert.equal(s.values.size, 1);
  pending.resolve(response({ success: true, data: { tripId: 'trip', sourceRegionId: 'source', places: 'keep', areas: 'keep' }, affected: {}, deletedIds: {} }));
  assert.equal(await first, true); assert.equal(dispatches, 1); assert.equal(s.applied.length, 1); assert.equal(s.values.size, 0);
});

test('lost reply remains held across refresh; graph facts and new previews cannot release it', async () => {
  let calls = 0;
  const s = setup(async url => { calls++; if (url.endsWith('deletion-preview')) return response(preview()); throw new TypeError('lost reply'); });
  await s.controller.refresh(); await s.controller.submit();
  assert.equal(s.controller.state.held, true); assert.equal(s.values.size, 1);
  const retained = JSON.parse([...s.values.values()][0]); assert.deepEqual(Object.keys(retained).sort(), ['areas','attemptId','places','sourceId']);
  const refreshed = createRegionDeletionController(s.options);
  await refreshed.refresh(); await refreshed.select('places','delete'); assert.equal(await refreshed.submit(), false);
  assert.equal(calls, 2); assert.equal(refreshed.state.held, true); assert.deepEqual(s.applied, []);
});

test('late response from a former account/editor session cannot apply or clear held intent', async () => {
  const pending = deferred();
  const s = setup(async url => url.endsWith('deletion-preview') ? response(preview()) : pending.promise);
  await s.controller.refresh(); const dispatch = s.controller.submit(); s.switchAccount();
  pending.resolve(response({ success: true, data: { tripId: 'trip', sourceRegionId: 'source', places: 'keep', areas: 'keep' } }));
  assert.equal(await dispatch, false); assert.deepEqual(s.applied, []); assert.equal(s.values.size, 1);
});

test('refresh restores the original dispositions of a held destructive intent', async () => {
  const s = setup(async (url, init) => {
    if (!url.endsWith('deletion-preview')) throw new TypeError('lost reply');
    const choices = JSON.parse(init.body);
    return response(preview(choices.places, choices.areas));
  });
  await s.controller.select('places', 'delete');
  await s.controller.select('areas', 'delete');
  await s.controller.submit();
  const reopened = createRegionDeletionController(s.options);
  assert.equal(reopened.state.held, true);
  assert.equal(reopened.state.places, 'delete');
  assert.equal(reopened.state.areas, 'delete');
  await reopened.select('places', 'keep');
  assert.equal(reopened.state.places, 'delete');
  assert.equal(await reopened.submit(), false);
});
