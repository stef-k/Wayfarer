import assert from 'node:assert/strict';
import test from 'node:test';
import { computed, nextTick, reactive, watchEffect } from 'vue';
import { createSegmentRouteProposalStore } from '../../ClientApps/trip-editor/src/components/segmentRouteProposalState.ts';

test('successful completion reactively renders Preview, Accept, and Discard', async () => {
  const store = createSegmentRouteProposalStore();
  const states = reactive(store.states);
  const state = computed(() => states.first ??= store.get('first', 'walk'));
  let renderedActions = [];
  watchEffect(() => { renderedActions = state.value.proposal ? ['Preview', 'Accept', 'Discard'] : []; });
  const request = store.begin('first', 'walk', new AbortController());

  assert.equal(store.complete('first', request, proposalFor('first', 'completed')), true);
  await nextTick();

  assert.deepEqual(renderedActions, ['Preview', 'Accept', 'Discard']);
});

test('two Segments retain isolated proposal and progress state', () => {
  const store = createSegmentRouteProposalStore();
  const first = store.get('first', 'walk');
  const second = store.get('second', 'drive');
  first.generating = true;
  first.proposal = { proposalId: 'p1' };

  assert.equal(second.generating, false);
  assert.equal(second.proposal, null);
  assert.equal(store.get('first', 'walk').proposal.proposalId, 'p1');
});

test('profile change invalidates only that Segment without generating', () => {
  const store = createSegmentRouteProposalStore();
  store.get('first', 'walk').proposal = { proposalId: 'p1' };
  store.get('second', 'drive').proposal = { proposalId: 'p2' };

  assert.equal(store.invalidateProfile('first', 'bike'), true);
  assert.equal(store.get('first', 'bike').proposal, null);
  assert.equal(store.get('second', 'drive').proposal.proposalId, 'p2');
});

test('discard cancels one request and preserves the other Segment', () => {
  const store = createSegmentRouteProposalStore();
  const controller = new AbortController();
  store.get('first', 'walk').controller = controller;
  store.get('second', 'drive').proposal = { proposalId: 'p2' };

  store.discard('first');

  assert.equal(controller.signal.aborted, true);
  assert.equal(store.get('first', 'walk').proposal, null);
  assert.equal(store.get('second', 'drive').proposal.proposalId, 'p2');
});

test('successful response is retained for its Segment and can render preview', () => {
  const store = createSegmentRouteProposalStore();
  const controller = new AbortController();
  const request = store.begin('first', 'walk', controller);
  const proposal = {
    proposalId: 'proposal-1', segmentId: 'first', geometry: [{ longitude: 23.7, latitude: 37.9 }],
    waypointIndices: [0], protectedContext: 'context', expiresAt: '2026-08-18T22:00:00Z'
  };

  assert.equal(store.complete('first', request, proposal), true);
  assert.equal(store.get('first', 'walk').proposal, proposal);
});

test('cancelled request cannot publish after its response races cancellation', () => {
  const store = createSegmentRouteProposalStore();
  const request = store.begin('first', 'walk', new AbortController());
  store.discard('first');

  assert.equal(store.complete('first', request, proposalFor('first', 'cancelled')), false);
  assert.equal(store.get('first', 'walk').proposal, null);
});

test('Segment switching publishes completion only to the initiating Segment', () => {
  const store = createSegmentRouteProposalStore();
  const request = store.begin('first', 'walk', new AbortController());
  store.get('second', 'drive');

  assert.equal(store.complete('first', request, proposalFor('first', 'first-proposal')), true);
  assert.equal(store.get('first', 'walk').proposal.proposalId, 'first-proposal');
  assert.equal(store.get('second', 'drive').proposal, null);
});

test('older generation cannot overwrite a newer proposal', () => {
  const store = createSegmentRouteProposalStore();
  const older = store.begin('first', 'walk', new AbortController());
  const newer = store.begin('first', 'walk', new AbortController());

  assert.equal(store.complete('first', newer, proposalFor('first', 'newer')), true);
  assert.equal(store.complete('first', older, proposalFor('first', 'older')), false);
  assert.equal(store.get('first', 'walk').proposal.proposalId, 'newer');
});

test('disposal aborts requests and rejects later completion', () => {
  const store = createSegmentRouteProposalStore();
  const controller = new AbortController();
  const request = store.begin('first', 'walk', controller);
  store.dispose();

  assert.equal(controller.signal.aborted, true);
  assert.equal(store.complete('first', request, proposalFor('first', 'disposed')), false);
  assert.equal(store.get('first', 'walk').proposal, null);
});

const proposalFor = (segmentId, proposalId) => ({
  proposalId, segmentId, geometry: [{ longitude: 23.7, latitude: 37.9 }], waypointIndices: [0],
  protectedContext: 'context', expiresAt: '2026-08-18T22:00:00Z'
});
