import assert from 'node:assert/strict';
import test from 'node:test';
import { build } from 'esbuild';

// The dialog owns the preview and local activation. Routed tests exercise the receipt transport and deletion effects.
const bundle = await build({ entryPoints: ['ClientApps/trip-editor/src/components/regionDeletionState.ts'], bundle: true,
  write: false, format: 'esm', platform: 'node', external: ['vue'], plugins: [{ name: 'confirmed-region-transport', setup(builder) {
    builder.onLoad({ filter: /structureMutation\.ts$/ }, () => ({ contents: `
      export class StructureRefused extends Error {}
      export const submitRegionDeletion = (...args) => globalThis.confirmedRegionTransport(...args, StructureRefused);
      export const sendStructureMutation = () => { throw new Error('Expected confirmed Region transport'); };
    ` }));
  } }] });
const code = bundle.outputFiles[0].text.replaceAll('from "vue"', `from "${import.meta.resolve('vue')}"`);
const { createRegionDeletionController, regionDeletionLabel, heldRegionDeletionKey } = await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`);

const contract = 'trip-region-place-area-receipt-1';
const preview = (places = 'keep', areas = 'keep', token = 'approval') => ({
  contractVersion: 2, contract, tripId: 'trip', sourceRegionId: 'source', sourceRegionName: 'Source', places, areas,
  destination: { mode: 'not-required', regionId: null, name: null },
  consequences: {
    ...Object.fromEntries(['movedPlaces','deletedPlaces','movedAreas','deletedAreas','deletedSegments','updatedSegments','detachedWaypoints']
      .map(key => [key, { count: 0, ids: [], hasMore: false }])), unlinkedVisits: 0, deletedCandidates: 0
  },
  confirmation: { token, expiresAt: new Date(Date.now() + 600000).toISOString() }
});
const success = () => ({ contract, success: true, data: { tripId: 'trip', sourceRegionId: 'source', places: 'keep', areas: 'keep' } });

const setup = (fetch = async () => Response.json(preview()), held = null) => {
  let current = true;
  const applied = [];
  const options = { sourceId: 'source', tripId: 'trip', endpoint: '/editor', antiforgeryToken: 'csrf',
    storage: { getItem: () => held }, scopeKey: heldRegionDeletionKey('server', 'account', 'trip'),
    isCurrent: () => current, fetch, onSuccess: result => applied.push(result) };
  return { controller: createRegionDeletionController(options), applied, switchAccount: () => { current = false; } };
};

for (const places of ['keep', 'delete']) for (const areas of ['keep', 'delete']) {
  test(`explicit ${places}/${areas} choice obtains matching version-two consent`, async () => {
    const calls = [];
    const { controller } = setup(async (url, init) => {
      const choices = JSON.parse(init.body);
      calls.push(url);
      return Response.json(preview(choices.places, choices.areas));
    });
    assert.equal(controller.state.places, 'keep');
    assert.equal(controller.state.areas, 'keep');
    await controller.select('places', places);
    await controller.select('areas', areas);
    assert.equal(controller.state.preview.places, places);
    assert.equal(controller.state.preview.areas, areas);
    assert.match(regionDeletionLabel({ places, areas }), /^Delete Region/);
    assert.ok(calls.every(url => url.endsWith('/deletion-preview?contract=' + contract)));
  });
}

test('out-of-order and old-server previews cannot authorize a deletion', async () => {
  const first = Promise.withResolvers();
  const second = Promise.withResolvers();
  let calls = 0;
  const { controller } = setup(() => (++calls === 1 ? first.promise : second.promise));
  const older = controller.refresh();
  const latest = controller.select('areas', 'delete');
  assert.equal(await controller.submit(), false);
  second.resolve(Response.json(preview('keep', 'delete', 'new')));
  await latest;
  first.resolve(Response.json(preview()));
  await older;
  assert.equal(controller.state.preview.confirmation.token, 'new');
  const old = setup(async () => Response.json({ ...preview(), contractVersion: 1 }));
  await old.controller.refresh();
  assert.equal(await old.controller.submit(), false);
});

test('duplicate activation is suppressed and former editor scope cannot publish the result', async () => {
  const delayed = Promise.withResolvers();
  const fixture = setup();
  let sends = 0;
  globalThis.confirmedRegionTransport = (...args) => {
    assert.deepEqual(args.slice(0, 5), ['/editor', 'trip', 'source', 'csrf', { places: 'keep', areas: 'keep', confirmationToken: 'approval' }]);
    sends++;
    return delayed.promise;
  };
  await fixture.controller.refresh();
  const pending = fixture.controller.submit();
  assert.equal(await fixture.controller.submit(), false);
  fixture.switchAccount();
  delayed.resolve(success());
  assert.equal(await pending, false);
  assert.equal(sends, 1);
  assert.deepEqual(fixture.applied, []);
});

test('terminal rejection requires another explicit preview, and lost transport holds the dialog', async () => {
  for (const terminal of [true, false]) {
    const fixture = setup();
    let sends = 0;
    globalThis.confirmedRegionTransport = async (...args) => {
      sends++;
      if (terminal) { const Refused = args.at(-1); throw new Refused('The preview changed'); }
      throw new Error('Lost response');
    };
    await fixture.controller.refresh();
    assert.equal(await fixture.controller.submit(), false);
    assert.equal(fixture.controller.state.held, !terminal);
    assert.equal(fixture.controller.state.preview, null);
    assert.equal(await fixture.controller.submit(), false);
    assert.equal(sends, 1);
    assert.deepEqual(fixture.applied, []);
  }
});

test('a legacy proofless hold restores choices and authorizes neither preview nor dispatch', async () => {
  const { controller } = setup(async () => assert.fail('Legacy hold must never authorize network work'),
    JSON.stringify({ sourceId: 'source', places: 'delete', areas: 'keep', attemptId: 'retained' }));
  assert.equal(controller.state.held, true);
  assert.equal(controller.state.places, 'delete');
  assert.equal(controller.state.areas, 'keep');
  await controller.select('places', 'keep');
  await controller.refresh();
  assert.equal(controller.state.places, 'delete');
  assert.equal(await controller.submit(), false);
});
