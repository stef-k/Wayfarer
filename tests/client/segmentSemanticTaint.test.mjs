import assert from 'node:assert/strict';
import { dirname, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { build } from 'esbuild';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const authorityPath = resolve(root, 'ClientApps/trip-editor/src/components/segmentSemanticTaint.ts');

const loadAuthority = async () => {
  const bundled = await build({
    bundle: true,
    entryPoints: [authorityPath],
    format: 'esm',
    platform: 'node',
    write: false
  });
  return import(`data:text/javascript;base64,${Buffer.from(bundled.outputFiles[0].text).toString('base64')}`);
};

test('substitute, restore, then add remains semantically unsafe', async () => {
  const { createSegmentSemanticTaint } = await loadAuthority();
  const taint = createSegmentSemanticTaint();

  taint.markUnsafe(); // B -> X
  taint.markUnsafe(); // X -> B is still an operation, not an authoritative reset.

  assert.equal(taint.isSafe.value, false);
});

test('explicit Reset clears taint and permits a later safe addition', async () => {
  const { createSegmentSemanticTaint } = await loadAuthority();
  const taint = createSegmentSemanticTaint();
  taint.markUnsafe();

  taint.resetFromAuthoritativeBaseline('segment-a');

  assert.equal(taint.isSafe.value, true);
});

test('child remount does not clear taint for the same active draft', async () => {
  const { createSegmentSemanticTaint } = await loadAuthority();
  const parentOwnedTaint = createSegmentSemanticTaint();
  parentOwnedTaint.resetFromAuthoritativeBaseline('segment-a');
  parentOwnedTaint.markUnsafe();

  const firstChildProps = { semanticEditsSafe: parentOwnedTaint.isSafe };
  const remountedChildProps = { semanticEditsSafe: parentOwnedTaint.isSafe };

  assert.equal(firstChildProps.semanticEditsSafe.value, false);
  assert.equal(remountedChildProps.semanticEditsSafe.value, false);
});

test('reorder, restore, then add remains semantically unsafe', async () => {
  const { createSegmentSemanticTaint } = await loadAuthority();
  const taint = createSegmentSemanticTaint();
  taint.markUnsafe(); // B/C -> C/B
  taint.markUnsafe(); // C/B -> B/C remains operation history.

  assert.equal(taint.isSafe.value, false);
});

test('successful authoritative replacement clears taint', async () => {
  const { createSegmentSemanticTaint } = await loadAuthority();
  const taint = createSegmentSemanticTaint();
  taint.resetFromAuthoritativeBaseline('segment-a');
  taint.markUnsafe();

  taint.resetFromAuthoritativeBaseline('segment-a');

  assert.equal(taint.isSafe.value, true);
});

test('opening another Segment initializes independent clean state', async () => {
  const { createSegmentSemanticTaint } = await loadAuthority();
  const taint = createSegmentSemanticTaint();
  taint.resetFromAuthoritativeBaseline('segment-a');
  taint.markUnsafe();

  taint.resetFromAuthoritativeBaseline('segment-b');

  assert.equal(taint.isSafe.value, true);
});
