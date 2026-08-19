import assert from 'node:assert/strict';
import test from 'node:test';
import { computed, nextTick, watchEffect } from 'vue';
import { createSegmentRouteProposalStore } from '../../ClientApps/trip-editor/src/components/segmentRouteProposalState.ts';

test('successful completion reactively renders Preview, Accept, and Discard', async () => {
  const store = createSegmentRouteProposalStore();
  const states = store.states;
  const state = computed(() => states.first ??= store.get('first', 'walk'));
  let renderedActions = [];
  watchEffect(() => { renderedActions = state.value.proposal ? ['Preview', 'Accept', 'Discard'] : []; });
  const request = store.begin('first', 'walk', new AbortController());

  assert.equal(store.complete('first', request, proposalFor('first', 'completed')), true);
  await nextTick();

  assert.deepEqual(renderedActions, ['Preview', 'Accept', 'Discard']);
});

test('reactive visibility remains isolated and requires explicit Discard', async () => {
  const store = createSegmentRouteProposalStore();
  const first = observableActions(store, 'first', 'walk');
  const second = observableActions(store, 'second', 'drive');
  const request = store.begin('first', 'walk', new AbortController());

  assert.equal(store.complete('first', request, proposalFor('first', 'visible')), true);
  await nextTick();
  assert.deepEqual(first.actions(), ['Preview', 'Accept', 'Discard']);
  assert.deepEqual(second.actions(), []);
  await nextTick();
  assert.deepEqual(first.actions(), ['Preview', 'Accept', 'Discard']);

  store.discard('first');
  await nextTick();
  assert.deepEqual(first.actions(), []);
});

test('rejected completions never become reactively visible', async () => {
  const store = createSegmentRouteProposalStore();
  const first = observableActions(store, 'first', 'walk');
  const cancelled = store.begin('first', 'walk', new AbortController());
  store.discard('first');
  assert.equal(store.complete('first', cancelled, proposalFor('first', 'cancelled')), false);

  const stale = store.begin('first', 'walk', new AbortController());
  store.begin('first', 'walk', new AbortController());
  assert.equal(store.complete('first', stale, proposalFor('first', 'stale')), false);
  const wrongSegment = store.get('first', 'walk').requestId;
  assert.equal(store.complete('first', wrongSegment, proposalFor('second', 'wrong')), false);
  store.dispose();
  assert.equal(store.complete('first', wrongSegment, proposalFor('first', 'disposed')), false);
  await nextTick();

  assert.deepEqual(first.actions(), []);
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
  assert.deepEqual(store.get('first', 'walk').proposal, proposal);
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

test('acceptance completion is rejected after every lifecycle invalidation', () => {
  for (const reason of ['discard', 'segment-switch', 'disposal', 'proposal-replacement', 'profile-change',
    'anchor-change', 'clear', 'reset', 'cancel', 'manual-route-change']) {
    const store = createSegmentRouteProposalStore();
    const context = acceptanceContext();
    const controller = new AbortController();
    store.get('first', 'walk').proposal = proposalFor('first', 'proposal-1');
    const request = store.beginAcceptance('first', 'proposal-1', context, controller);

    store.invalidate('first', reason);

    assert.equal(controller.signal.aborted, true, reason);
    assert.equal(store.completeAcceptance('first', request, 'proposal-1', context), false, reason);
  }
});

test('duplicate acceptance is refused while the initiating request is pending', () => {
  const store = createSegmentRouteProposalStore();
  const context = acceptanceContext();
  store.get('first', 'walk').proposal = proposalFor('first', 'proposal-1');
  const first = store.beginAcceptance('first', 'proposal-1', context, new AbortController());

  assert.equal(typeof first, 'number');
  assert.equal(store.beginAcceptance('first', 'proposal-1', context, new AbortController()), null);
  assert.equal(store.get('first', 'walk').accepting, true);
});

test('acceptance requires the exact initiating proposal and complete draft context', () => {
  for (const changed of [
    { proposalId: 'proposal-2' },
    { transportProfileId: 'bike' },
    { anchorFingerprint: 'changed' },
    { routeFingerprint: 'changed' },
    { draftRevision: 2 }
  ]) {
    const store = createSegmentRouteProposalStore();
    const context = acceptanceContext();
    store.get('first', 'walk').proposal = proposalFor('first', 'proposal-1');
    const request = store.beginAcceptance('first', 'proposal-1', context, new AbortController());
    const proposalId = changed.proposalId ?? 'proposal-1';

    assert.equal(store.completeAcceptance('first', request, proposalId, { ...context, ...changed }), false);
  }
});

const acceptanceContext = () => ({
  segmentId: 'first', transportProfileId: 'walk', anchorFingerprint: 'from|via|to',
  routeFingerprint: 'draft-route', draftRevision: 1
});

const proposalFor = (segmentId, proposalId) => ({
  proposalId, segmentId, geometry: [{ longitude: 23.7, latitude: 37.9 }], waypointIndices: [0],
  protectedContext: 'context', expiresAt: '2026-08-18T22:00:00Z'
});

const observableActions = (store, segmentId, contextKey) => {
  const state = computed(() => store.states[segmentId] ??= store.get(segmentId, contextKey));
  let actions = [];
  watchEffect(() => { actions = state.value.proposal ? ['Preview', 'Accept', 'Discard'] : []; });
  return { actions: () => actions };
};
