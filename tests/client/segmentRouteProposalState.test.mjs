import assert from 'node:assert/strict';
import test from 'node:test';
import { createSegmentRouteProposalStore } from '../../ClientApps/trip-editor/src/components/segmentRouteProposalState.ts';

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
