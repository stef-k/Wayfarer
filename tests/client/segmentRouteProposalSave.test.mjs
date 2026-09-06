Error.stackTraceLimit = 2;
import assert from 'node:assert/strict';
import test from 'node:test';
import { build } from 'esbuild';
for (const name of ['HTMLParagraphElement', 'HTMLOListElement', 'HTMLUListElement']) globalThis[name] = class {};
globalThis.document = { createElement: () => ({ innerHTML: '', content: { querySelectorAll: () => [], lastChild: null } }) };
const bundled = await build({ bundle: true, entryPoints: ['ClientApps/trip-editor/src/components/segmentRouteProposalDraft.ts'], format: 'esm', platform: 'node', write: false });
const { createSegmentRouteProposalDraftController } = await import(`data:text/javascript;base64,${Buffer.from(bundled.outputFiles[0].text + '\n//# sourceURL=proposal-save-test-bundle.mjs').toString('base64')}`);

test('pending proposal alone is dirty and Save transports it without changing preview fields; discard retains edits', () => {
  const draft = { id: 'segment', fromPlaceId: '', toPlaceId: '', waypointRows: [], mode: '', route: null, notesHtml: '', estimatedDurationSource: 'Automatic', aggregateConcurrencyToken: 'aggregate', estimatedDistanceKm: '7', estimatedDurationMinutes: '8' };
  const controller = createSegmentRouteProposalDraftController(draft, () => 'segment', () => {});
  const before = structuredClone(draft);
  controller.preview({ proposalId: 'proposal', segmentId: 'segment', protectedContext: 'protected',
    geometry: [{ longitude: 1, latitude: 2 }, { longitude: 3, latitude: 4 }], waypointIndices: [0, 1],
    distanceMetres: 0, durationSeconds: 0 });
  assert.equal(controller.hasProposal.value, true);
  assert.deepEqual(draft, before);
  draft.notesHtml = 'edit during preview';
  const request = controller.buildRequest();
  assert.equal(request.proposal.protectedContext, 'protected');
  assert.deepEqual(request.route.coordinates, [[1, 2], [3, 4]]);
  assert.equal(request.notesHtml, 'edit during preview');
  assert.equal(request.estimatedDistanceKm, 0);
  assert.equal(request.estimatedDurationMinutes, 0);
  controller.preview(null);
  assert.equal(controller.hasProposal.value, false);
  assert.equal(draft.notesHtml, 'edit during preview');
  assert.equal(draft.estimatedDistanceKm, '7');
});
