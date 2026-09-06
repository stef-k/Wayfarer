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
  const baseline = structuredClone(draft);
  const previews = [];
  const controller = createSegmentRouteProposalDraftController(draft, () => 'segment', preview => previews.push(preview), () => baseline);
  const before = structuredClone(draft);
  controller.preview({ proposalId: 'proposal', segmentId: 'segment', protectedContext: 'protected',
    geometry: [{ longitude: 1, latitude: 2 }, { longitude: 3, latitude: 4 }], waypointIndices: [0, 1],
    distanceMetres: 0, durationSeconds: 0 });
  assert.equal(controller.hasProposal.value, true);
  assert.equal(controller.publishIfPresent(), true);
  assert.equal(previews[0].kind, 'proposal');
  assert.deepEqual(previews[0].route.coordinates, [[1, 2], [3, 4]]);
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
  assert.equal(controller.publishIfPresent(), false);
  assert.equal(draft.notesHtml, 'edit during preview');
  assert.equal(draft.estimatedDistanceKm, '7');
});

for (const [distance, duration] of [[1250, 360], [null, null], [undefined, undefined], [0, 0]]) {
  test(`proposal estimates ${distance}/${duration} remain transient and Manual edit wins`, () => {
    const draft = { id: 'segment', fromPlaceId: '', toPlaceId: '', waypointRows: [], mode: '', route: null,
      notesHtml: '', estimatedDurationSource: 'Automatic', estimatedDistanceKm: '7', estimatedDurationMinutes: '8' };
    const baseline = structuredClone(draft);
    const controller = createSegmentRouteProposalDraftController(draft, () => 'segment', () => {}, () => baseline);
    controller.preview({ proposalId: 'p', segmentId: 'segment', protectedContext: 'protected',
      geometry: [{ longitude: 1, latitude: 2 }, { longitude: 3, latitude: 4 }], waypointIndices: [0, 1],
      distanceMetres: distance, durationSeconds: duration });
    assert.equal(controller.buildRequest().estimatedDistanceKm, distance == null ? 7 : distance / 1000);
    assert.equal(controller.buildRequest().estimatedDurationMinutes, duration == null ? 8 : duration / 60);
    assert.equal(draft.estimatedDistanceKm, '7');
    assert.equal(draft.estimatedDurationMinutes, '8');
    draft.estimatedDurationSource = 'Manual';
    draft.estimatedDurationMinutes = '12';
    const request = controller.buildRequest();
    assert.equal(request.estimatedDurationMinutes, 12);
    assert.equal(request.estimatedDurationSource, 'Manual');
    assert.equal(request.proposal.manualDurationOverride, true);
    controller.preview(null);
    assert.equal(draft.estimatedDurationMinutes, '12');
    assert.equal(draft.estimatedDurationSource, 'Manual');
    assert.equal(controller.buildRequest().proposal, undefined);
  });
}

// A preview snapshot already contains unsaved edits made before generation.
test('Manual edit before generation survives a missing duration without overriding present estimates', () => {
  const draft = { id: 'segment', fromPlaceId: '', toPlaceId: '', waypointRows: [], mode: '', route: null,
    notesHtml: '', estimatedDurationSource: 'Automatic', estimatedDistanceKm: '7', estimatedDurationMinutes: '8' };
  const baseline = structuredClone(draft);
  const controller = createSegmentRouteProposalDraftController(draft, () => 'segment', () => {}, () => baseline);
  draft.estimatedDurationSource = 'Manual';
  draft.estimatedDurationMinutes = '12';
  const proposal = { proposalId: 'p', segmentId: 'segment', protectedContext: 'protected',
    geometry: [{ longitude: 1, latitude: 2 }, { longitude: 3, latitude: 4 }], waypointIndices: [0, 1],
    distanceMetres: 1250, durationSeconds: null };
  controller.preview(proposal);
  const request = controller.buildRequest();
  assert.equal(request.estimatedDurationMinutes, 12);
  assert.equal(request.estimatedDurationSource, 'Manual');
  assert.equal(request.proposal.manualDurationOverride, true);
  controller.preview({ ...proposal, durationSeconds: 0 });
  assert.equal(controller.buildRequest().estimatedDurationMinutes, 0);
  assert.equal(controller.buildRequest().proposal.manualDurationOverride, false);
  // An unchanged canonical Manual value is preservation, not an explicit override.
  baseline.estimatedDurationSource = 'Manual';
  baseline.estimatedDurationMinutes = '12';
  controller.preview(proposal);
  assert.equal(controller.buildRequest().proposal.manualDurationOverride, false);
});

test('Generate followed by ordinary Save makes no separate acceptance request and failed Save retains proposal', async () => {
  const apiBundle = await build({ bundle: true, entryPoints: ['ClientApps/trip-editor/src/api/tripEditorApi.ts'], format: 'esm', platform: 'node', write: false });
  const api = await import(`data:text/javascript;base64,${Buffer.from(apiBundle.outputFiles[0].text + '\n//# sourceURL=proposal-api-test-bundle.mjs').toString('base64')}`);
  const calls = [];
  const originalFetch = globalThis.fetch;
  const proposal = { proposalId: 'p', segmentId: 'segment', protectedContext: 'protected',
    geometry: [{ longitude: 1, latitude: 2 }, { longitude: 3, latitude: 4 }], waypointIndices: [0, 1] };
  globalThis.fetch = async (url, options) => {
    calls.push({ url, options });
    return new Response(JSON.stringify(options.method === 'POST' ? proposal : { title: 'Stale proposal', errors: { proposal: ['Regenerate explicitly.'] } }),
      { status: options.method === 'POST' ? 200 : 422, headers: { 'Content-Type': 'application/json' } });
  };
  try {
    const draft = { id: 'segment', fromPlaceId: '', toPlaceId: '', waypointRows: [], mode: '', route: null,
      notesHtml: '', estimatedDurationSource: 'Automatic', estimatedDistanceKm: '7', estimatedDurationMinutes: '8' };
    const baseline = structuredClone(draft);
    const controller = createSegmentRouteProposalDraftController(draft, () => 'segment', () => {}, () => baseline);
    controller.preview(await api.generateExternalRouteProposal('trip', 'segment', 'anti', 'aggregate', 'drive', new AbortController().signal));
    await assert.rejects(api.updateSegment('/editor', 'segment', 'anti', controller.buildRequest()));
    assert.equal(controller.hasProposal.value, true);
    assert.equal(draft.route, null);
    assert.deepEqual(calls.map(call => call.options.method), ['POST', 'PUT']);
    assert.equal(calls.some(call => call.url.endsWith('/accept')), false);
    assert.equal(JSON.parse(calls[1].options.body).proposal.protectedContext, 'protected');
  } finally { globalThis.fetch = originalFetch; }
});
