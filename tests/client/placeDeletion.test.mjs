import assert from 'node:assert/strict';
import test from 'node:test';
import { nextTick } from 'vue';
import { withApp, place, envelope, deferred } from './placeEditorHost.mjs';

/** One complete no-dependent preview still requires a separate explicit activation. */
const preview = (overrides = {}) => ({ contractVersion: 1, tripId: 'trip', placeId: 'place', regionId: 'region', placeName: 'Original',
  deletedSegments: { count: 0, ids: [], hasMore: false }, updatedSegments: { count: 0, ids: [], hasMore: false },
  detachedWaypoints: { count: 0, ids: [], hasMore: false }, unlinkedVisits: 0, deletedCandidates: 0, placeOrderIds: [], segmentOrderIds: null,
  confirmation: { token: 'approval', expiresAt: new Date(Date.now() + 600000).toISOString() }, ...overrides });
const reply = () => {
  const result = envelope({ tripId: 'trip', placeId: 'place', regionId: 'region', updatedSegmentIds: [], detachedWaypointAssociations: [] });
  result.affected.places = [];
  result.affected.metadata = { id: 'trip', name: 'Trip', notesHtml: '', isPublic: false, shareProgressEnabled: false, center: null, zoom: null, coverImage: null, publicUrl: null, progressPublicUrl: null, updatedAt: '2026-10-25T12:00:00Z', originalValues: { name: 'Trip', notesHtml: null } };
  result.affected.placeOrdersByRegionId = { region: [] };
  result.affected.visitProgress = { totalPlaces: 0, visitedPlaces: 0, percentVisited: 0, placeSummariesByPlaceId: {}, historyRows: [] };
  result.deletedIds.places = ['place'];
  return result;
};
const response = (value, status = 200) => new Response(JSON.stringify(value), { status });

for (const approve of [false, true]) test(`no-dependent deletion previews and explicitly ${approve ? 'confirms' : 'cancels'} through the mounted owner`, () => withApp(async ({ parent, editor }) => {
  await editor.openPlaceEdit(parent.state.placesById.place);
  editor.placeDraft.notesHtml = 'Unsaved target draft';
  const calls = []; let confirmations = 0;
  globalThis.placeConfirm = async options => { confirmations++; assert.match(options.message, /Delete 0 endpoint Segment/); return approve; };
  globalThis.fetch = async (url, init) => { calls.push([url, init.method, JSON.parse(init.body)]); return response(url.endsWith('deletion-preview') ? preview() : reply()); };
  await editor.deleteDraftPlace(); await nextTick();
  assert.equal(confirmations, 1); assert.equal(calls.length, approve ? 2 : 1);
  assert.equal(calls[0][1], 'POST'); assert.ok(calls[0][0].endsWith('/deletion-preview'));
  if (approve) {
    assert.deepEqual(calls[1][2], { confirmationToken: 'approval' }); assert.equal(parent.state.placesById.place, undefined);
    assert.equal(editor.placeDraft.id, null);
  } else { assert.equal(editor.placeDraft.notesHtml, 'Unsaved target draft'); assert.ok(parent.state.placesById.place); }
}));

for (const outcome of ['network', '500', 'foreign target', 'wrong deletion', 'unrelated scope', 'invalid progress']) {
  test(`deletion ${outcome} holds unknown outcomes without adopting or replaying`, () => withApp(async ({ parent, editor }) => {
    await editor.openPlaceEdit(parent.state.placesById.place); editor.placeDraft.notesHtml = 'Retained draft';
    let dispatches = 0;
    globalThis.fetch = async (url) => {
      if (url.endsWith('deletion-preview')) return response(preview());
      dispatches++; if (outcome === 'network') throw new TypeError('Lost committed response');
      const result = reply();
      if (outcome === 'foreign target') result.data.tripId = 'foreign';
      if (outcome === 'wrong deletion') result.deletedIds.places.push('unapproved');
      if (outcome === 'unrelated scope') result.affected.areas.push({ id: 'foreign' });
      if (outcome === 'invalid progress') result.affected.visitProgress = {};
      return response(result, outcome === '500' ? 500 : 200);
    };
    await editor.deleteDraftPlace();
    assert.equal(editor.placeDraft.notesHtml, 'Retained draft'); assert.ok(parent.state.placesById.place);
    assert.match(editor.saveError, /unknown|held/i);
    await editor.deleteDraftPlace(); await editor.savePlaceDraft();
    assert.equal(dispatches, 1);
    editor.resetPlaceDraft(); await editor.deleteDraftPlace(); assert.equal(dispatches, 1, 'reset cannot prove noncommit');
  }));
}

for (const stage of ['preview', 'confirmation', 'dispatch']) {
  test(`newer intent during ${stage} remains intact`, () => withApp(async ({ parent, editor }) => {
    await editor.openPlaceEdit(parent.state.placesById.place);
    const pending = deferred(); let dispatches = 0;
    globalThis.placeConfirm = () => stage === 'confirmation' ? pending.promise : Promise.resolve(true);
    globalThis.fetch = async url => {
      if (url.endsWith('deletion-preview')) return stage === 'preview' ? pending.promise : response(preview());
      dispatches++; return pending.promise;
    };
    const deleting = editor.deleteDraftPlace();
    for (let tick = 0; tick < 6; tick++) await new Promise(done => setImmediate(done));
    editor.placeDraft.notesHtml = 'Newer intent';
    pending.resolve(stage === 'confirmation' ? true : response(stage === 'preview' ? preview() : reply()));
    await deleting; await nextTick();
    assert.equal(editor.placeDraft.notesHtml, 'Newer intent');
    assert.equal(dispatches, stage === 'dispatch' ? 1 : 0);
    if (stage === 'dispatch') assert.equal(parent.state.placesById.place, undefined);
  }));
}

test('definite stale rejection preserves draft, needs a fresh preview and another activation', () => withApp(async ({ parent, editor }) => {
  await editor.openPlaceEdit(parent.state.placesById.place); editor.placeDraft.name = 'My draft';
  let previews = 0; let confirmed = 0;
  globalThis.placeConfirm = async () => { confirmed++; return true; };
  globalThis.fetch = async url => url.endsWith('deletion-preview') ? (previews++, response(preview()))
    : response({ code: 'place-deletion-preview-stale', title: 'Changed' }, 409);
  await editor.deleteDraftPlace(); assert.equal(editor.placeDraft.name, 'My draft');
  assert.equal(previews, 1); assert.equal(confirmed, 1);
  await editor.deleteDraftPlace(); assert.equal(previews, 2); assert.equal(confirmed, 2);
}));

test('a superseding draft session fences late success and leaves its attempt held', () => withApp(async ({ parent, editor }) => {
  await editor.openPlaceEdit(parent.state.placesById.place);
  const pending = deferred(); let dispatches = 0;
  globalThis.fetch = async url => url.endsWith('deletion-preview') ? response(preview()) : (dispatches++, pending.promise);
  const deleting = editor.deleteDraftPlace();
  for (let tick = 0; tick < 6; tick++) await new Promise(done => setImmediate(done));
  editor.placeDraftState.reset(place({ id: 'new-target' }));
  editor.placeDraft.notesHtml = 'New target draft';
  pending.resolve(response(reply())); await deleting;
  assert.equal(editor.placeDraft.id, 'new-target'); assert.equal(editor.placeDraft.notesHtml, 'New target draft');
  assert.ok(parent.state.placesById.place); assert.equal(dispatches, 1);
}));
