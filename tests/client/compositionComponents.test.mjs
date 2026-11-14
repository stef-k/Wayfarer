import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';
import { build } from 'esbuild';
import { parse, compileScript } from '@vue/compiler-sfc';
import { createSSRApp } from 'vue';
import { renderToString } from '@vue/server-renderer';

// Compile the actual hierarchy/choice components at the existing in-memory Vue seam.
const bundle = await build({ stdin: { contents: `export { default as SourceTree } from './SourceTree.vue'; export { default as ChoiceControls } from './ChoiceControls.vue'; export { default as CompositionStart } from './CompositionStart.vue';`,
  loader: 'ts', resolveDir: 'ClientApps/trip-editor/src/composition' }, bundle: true, write: false, format: 'esm', platform: 'node', external: ['vue'],
  plugins: [{ name: 'vue', setup(builder) {
    builder.onLoad({ filter: /\.vue$/ }, async args => {
      const { descriptor } = parse(await readFile(args.path, 'utf8'), { filename: args.path });
      if (args.path.endsWith('CompositionStart.vue')) {
        const script = compileScript(descriptor, { id: args.path, genDefaultAs: '__component' });
        return { loader: 'ts', contents: `${script.content}
          const setup = __component.setup;
          __component.setup = (props, context) => { const bindings = setup(props, context); globalThis.captureConstruction(bindings); return bindings; };
          __component.render = () => null; export default __component;` };
      }
      return { loader: 'ts', contents: compileScript(descriptor, { id: args.path, inlineTemplate: true }).content };
    });
  } }] });
const code = bundle.outputFiles[0].text.replaceAll('from "vue"', `from "${import.meta.resolve('vue')}"`);
const { SourceTree, ChoiceControls, CompositionStart } = await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`);
const source = { graph: { id: 'source', name: 'Source', regions: [
  { id: 'region', name: 'Segments', places: [{ id: 'tower', name: 'Taipei 101', location: [121, 25] }], areas: [] }
], segments: [{ id: 'route', mode: 'walk', fromPlaceId: 'tower', toPlaceId: null, waypoints: [] }], credits: [] },
  regionOrder: ['region'], placeOrder: { region: ['tower'] }, areaOrder: { region: [] } };

test('rendered foreign tree preserves Regions/Trip-level Segments and has no temporal labels, values or diagnostics', async () => {
  const html = await renderToString(createSSRApp(SourceTree, { snapshot: source, side: 'b', selection: null, mappings: [], expanded: [] }));
  assert.match(html, /Region · Segments/); assert.match(html, /Trip-level Segments/); assert.match(html, /Place · Taipei 101/);
  assert.doesNotMatch(html, /planning|planned|date|zone|diagnostic|Matched/i);
  assert.match(html, /Not reviewed/);
});

test('Segment copy shows complete anchor choices and Trip-level destination with no Add to Region control', async () => {
  const { newChoice, newReview } = await import('../../ClientApps/trip-editor/src/composition/compositionModel.ts');
  const current = structuredClone(source); current.graph.id = 'C';
  const workspace = { tripId: 'C', sourceA: source, sourceB: source, current, mappings: [] };
  const choice = newChoice(workspace, 'b', 'segment', 'route');
  const html = await renderToString(createSSRApp(ChoiceControls, { workspace, review: newReview(workspace, 'b'), choice }));
  assert.match(html, /C · Trip-level Segments/); assert.match(html, /From · Taipei 101/);
  assert.match(html, /Approve a separate Place with the captured location/); assert.match(html, /Missing Place destination Region/);
  assert.doesNotMatch(html, /Add to Region|>Destination Region|No planned date/);
});

test('creation component freezes the submitted pair/name and leaves newer form choices open after success', async () => {
  const priorFetch = globalThis.fetch; const priorWindow = globalThis.window;
  let bindings, resolveSave, started; const requestStarted = new Promise(resolve => { started = resolve; });
  const waiting = new Promise(resolve => { resolveSave = resolve; }); const navigations = []; const requests = [];
  globalThis.window = { location: { assign: url => navigations.push(url) } };
  globalThis.captureConstruction = value => { bindings = value; };
  globalThis.fetch = async (url, init) => {
    requests.push({ url, init });
    if (init.method === 'GET') return Response.json([]);
    started(); return waiting;
  };
  try {
    await renderToString(createSSRApp(CompositionStart, { token: 'csrf' }));
    bindings.sourceA.value = 'A'; bindings.sourceB.value = 'B'; bindings.name.value = 'Submitted C';
    const save = bindings.create(); await requestStarted;
    bindings.name.value = 'Later name'; bindings.sourceB.value = 'Later B';
    resolveSave(Response.json({ tripId: 'C', compositionId: 'comparison', mediaOmitted: false })); await save;
    assert.equal(navigations.length, 0); assert.equal(bindings.name.value, 'Later name'); assert.equal(bindings.sourceB.value, 'Later B');
    assert.equal(bindings.savedResultUrl.value, '/User/Trip/Compose/comparison'); assert.equal(bindings.pending.value, false);
    const submitted = requests.find(r => r.init.method === 'POST');
    assert.equal(JSON.parse(submitted.init.body).name, 'Submitted C'); assert.equal(JSON.parse(submitted.init.body).sourceB, 'B');
  } finally { globalThis.fetch = priorFetch; globalThis.window = priorWindow; delete globalThis.captureConstruction; }
});
