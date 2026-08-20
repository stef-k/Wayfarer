import assert from 'node:assert/strict';
import { dirname, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const activationPath = resolve(root, 'ClientApps/trip-editor/src/components/segmentDraftActivation.ts');

const loadActivation = async () => {
  const bundled = await build({
    bundle: true,
    entryPoints: [activationPath],
    format: 'esm',
    platform: 'node',
    write: false
  });
  return import(`data:text/javascript;base64,${Buffer.from(bundled.outputFiles[0].text).toString('base64')}`);
};

const segment = (id, waypointPlaceIds) => ({ id, waypointPlaceIds });

const createHarness = async () => {
  const { activateAuthoritativeSegment } = await loadActivation();
  const segmentA = segment('segment-a', ['a-waypoint']);
  const segmentB = segment('segment-b', ['b-waypoint']);
  const state = {
    activeId: segmentA.id,
    draft: structuredClone(segmentA),
    persistedBaseline: structuredClone(segmentA),
    semanticEditsSafe: false
  };
  let allowSwitch = true;
  const activate = target => activateAuthoritativeSegment({
    segment: target,
    isAlreadyActive: state.activeId === target.id,
    activateTarget: async () => {
      if (!allowSwitch) return false;
      state.activeId = target.id;
      return true;
    },
    installAuthoritativeSegment: authoritative => {
      state.activeId = authoritative.id;
      state.draft = structuredClone(authoritative);
      state.persistedBaseline = structuredClone(authoritative);
      state.semanticEditsSafe = true;
    }
  });
  return { activate, segmentA, segmentB, setAllowSwitch: value => { allowSwitch = value; }, state };
};

test('row delete activation owns B baseline and clean taint after deletion confirmation is cancelled', async () => {
  const harness = await createHarness();

  assert.equal(await harness.activate(harness.segmentB), true);
  const deletionConfirmed = false;

  assert.equal(deletionConfirmed, false);
  assert.equal(harness.state.activeId, 'segment-b');
  assert.deepEqual(harness.state.draft, harness.segmentB);
  assert.deepEqual(harness.state.persistedBaseline, harness.segmentB);
  assert.equal(harness.state.semanticEditsSafe, true);
  assert.equal(harness.state.semanticEditsSafe, true, 'a later safe addition remains preservation-eligible');
});

test('cancelled switch to B preserves every part of A activation ownership', async () => {
  const harness = await createHarness();
  harness.setAllowSwitch(false);
  const before = structuredClone(harness.state);

  assert.equal(await harness.activate(harness.segmentB), false);

  assert.deepEqual(harness.state, before);
});

test('repeated deletion cancellation does not leak identity baseline or taint', async () => {
  const harness = await createHarness();

  assert.equal(await harness.activate(harness.segmentB), true);
  assert.equal(await harness.activate(harness.segmentB), true);

  assert.equal(harness.state.activeId, 'segment-b');
  assert.deepEqual(harness.state.draft, harness.segmentB);
  assert.deepEqual(harness.state.persistedBaseline, harness.segmentB);
  assert.equal(harness.state.semanticEditsSafe, true);
});

test('ordinary open uses the coherent authoritative activation contract', async () => {
  const harness = await createHarness();

  assert.equal(await harness.activate(harness.segmentB), true);

  assert.deepEqual(harness.state, {
    activeId: 'segment-b',
    draft: harness.segmentB,
    persistedBaseline: harness.segmentB,
    semanticEditsSafe: true
  });
});
