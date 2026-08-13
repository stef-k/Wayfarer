import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { parse, compileScript } from '@vue/compiler-sfc';
import { build } from 'esbuild';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const componentPath = resolve(root, 'ClientApps/trip-editor/src/components/SegmentRoutePointEditor.vue');

test('empty longitude input does not move an anonymous route point to zero', async () => {
  const source = await readFile(componentPath, 'utf8');
  const descriptor = parse(source, { filename: componentPath }).descriptor;
  const script = compileScript(descriptor, { id: 'segment-route-point-editor-test' }).content;
  const bundled = await build({
    bundle: true,
    format: 'esm',
    platform: 'node',
    stdin: { contents: script, loader: 'ts', resolveDir: dirname(componentPath) },
    write: false
  });
  const component = (await import(`data:text/javascript;base64,${Buffer.from(bundled.outputFiles[0].text).toString('base64')}`)).default;
  const node = { kind: 'anonymous', key: 'anonymous:1', coordinate: [23.72, 37.98] };
  const moves = [];
  const bindings = component.setup({
    controller: {
      nodes: () => [node],
      insertAfter: () => null,
      move: (key, coordinate) => { moves.push([key, coordinate]); return true; },
      remove: () => false
    }
  }, { expose: () => {} });

  bindings.move(node, 0, { target: { value: '', valueAsNumber: Number.NaN } });

  assert.deepEqual(moves, []);
  assert.deepEqual(node.coordinate, [23.72, 37.98]);
});
