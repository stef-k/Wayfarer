import assert from 'node:assert/strict';
import test from 'node:test';
import { build } from 'esbuild';
import { readFile } from 'node:fs/promises';
import { parse, compileScript, compileTemplate } from '@vue/compiler-sfc';
import { createSSRApp, proxyRefs } from 'vue';
import { renderToString } from '@vue/server-renderer';

// Exercise the real adapter and temporary layer, substituting only Leaflet and unrelated map owners.
const paths = [];
const group = () => ({ addTo() { return this; }, clearLayers() { paths.length = 0; } });
const map = { setView() { return this; }, on() {}, off() {}, remove() {},
  fitBounds(bounds) { this.bounds = bounds.points; },
  attributionControl: { setPrefix() {}, getContainer() { return null; } } };
globalThis.window = {};
globalThis.previewLeaflet = { map: () => map, layerGroup: group,
  latLngBounds: () => ({ points: [], extend(point) { this.points.push(point); return this; }, isValid() { return this.points.length > 0; } }),
  polyline: (coordinates, style) => ({ addTo() { paths.push({ coordinates, style }); return this; },
    getElement: () => ({ setAttribute() {} }) }) };
const bundle = await build({ stdin: { contents: "export { createTripEditorMap } from './ClientApps/trip-editor/src/map/leafletAdapter'; export { createSegmentRouteDraftPreviewLayer } from './ClientApps/trip-editor/src/map/segmentRouteDraftPreviewLayer';", resolveDir: process.cwd() },
  bundle: true, format: 'esm', platform: 'node', write: false, plugins: [{ name: 'map-boundaries', setup(builder) {
    builder.onResolve({ filter: /leaflet$/ }, () => ({ path: 'leaflet', namespace: 'mock' }));
    builder.onLoad({ filter: /.*/, namespace: 'mock' }, () => ({ contents: 'export default globalThis.previewLeaflet;' }));
    builder.onLoad({ filter: /\.css$/ }, () => ({ contents: '' }));
    builder.onLoad({ filter: /(?:areaPolygonWorkLayer|mapUtilitiesControl|placeDraftPreviewLayer|searchPreviewLayer|segmentRouteWorkLayer|segmentPresentationLayer|tileRetryLayer)\.ts$/ }, async args => {
      const { readFile } = await import('node:fs/promises');
      const source = await readFile(args.path, 'utf8');
      const exports = [...source.matchAll(/export const (create\w+)/g)].map(match => match[1]);
      return { contents: exports.map(name => `export const ${name} = () => ({ addTo() { return this; }, isActive: () => false, dispose() {}, remove() {} });`).join('\n') };
    });
  } }] });
const { createTripEditorMap, createSegmentRouteDraftPreviewLayer } = await import(`data:text/javascript;base64,${Buffer.from(bundle.outputFiles[0].text).toString('base64')}`);

test('adapter publishes proposal geometry and clears it without duplicating ordinary draft ownership', () => {
  const adapter = createTripEditorMap({}, '/controlled-tiles');
  const preview = { identity: 'segment-provider-proposal', kind: 'proposal', segmentId: 'segment',
    route: { type: 'LineString', coordinates: [[23, 37], [24, 38]] } };
  adapter.setSegmentDraftPreview({}, preview);
  assert.equal(paths.length, 1);
  assert.deepEqual(paths[0].coordinates, [[37, 23], [38, 24]]);
  assert.equal(paths[0].style.dashArray, '8 6');
  assert.equal(adapter.focusActiveEntity({}, { kind: 'segment', entityId: 'segment' }), 'moved');
  assert.deepEqual(map.bounds, [[37, 23], [38, 24]]);
  assert.equal(adapter.fitAllGeometry({ regionsById: {}, placesById: {}, areasById: {}, segmentsById: {} }), 'moved');
  assert.deepEqual(map.bounds, [[37, 23], [38, 24]]);
  adapter.setSegmentDraftPreview({}, { ...preview, kind: undefined });
  assert.equal(paths.length, 0);
  adapter.setSegmentDraftPreview({}, preview);
  adapter.setSegmentDraftPreview({}, null);
  assert.equal(paths.length, 0);
  adapter.setSegmentDraftPreview({}, preview);
  adapter.dispose();
  assert.equal(paths.length, 0);
});

test('temporary route yields to map work and hidden Segments, with no fallback or stale bounds', () => {
  const layer = createSegmentRouteDraftPreviewLayer(map);
  layer.set({ kind: 'proposal', segmentId: 'segment', route: { coordinates: [[23, 37], [24, 38]] } });
  layer.render({}, new Set(), false);
  assert.equal(paths.length, 1);
  for (const [hidden, work] of [[new Set(), true], [new Set(['segment']), false]]) {
    layer.render({}, hidden, work);
    assert.equal(paths.length, 0);
    assert.equal(layer.extendBounds(globalThis.previewLeaflet.latLngBounds()).isValid(), false);
  }
  layer.set({ kind: 'proposal', segmentId: 'segment', route: null });
  layer.render({}, new Set(), false);
  assert.equal(paths.length, 0);
  layer.dispose();
});

// Render the production proposal template with real proposal state and the existing SFC compiler.
test('pending template labels present, absent and zero estimates and explains Manual precedence', async () => {
  const filename = 'ClientApps/trip-editor/src/components/SegmentRouteProposal.vue';
  const { descriptor } = parse(await readFile(filename, 'utf8'), { filename });
  const script = compileScript(descriptor, { id: 'proposal-test' });
  const template = compileTemplate({ source: descriptor.template.content, filename, id: 'proposal-test',
    compilerOptions: { bindingMetadata: script.bindings } });
  const compiled = await build({ stdin: { contents: `${script.content}\n${template.code}`, loader: 'ts',
    resolveDir: 'ClientApps/trip-editor/src/components' }, bundle: true, write: false, format: 'esm', platform: 'node',
    external: ['vue'] });
  // Resolve Vue against this repository rather than a data URL.
  const code = compiled.outputFiles[0].text.replaceAll('from "vue"', `from "${import.meta.resolve('vue')}"`);
  const { default: component, render } = await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`);
  const props = { segment: { id: 'segment', mode: 'walk', externalRouting: { available: true, modes: [] } },
    draftMode: 'walk', draftContextKey: 'context', manualDurationOverride: true };
  for (const [distance, duration, expected] of [[1250, 360, ['1.25 km', '6 minutes']],
    [null, null, ['Unavailable', 'Unavailable']], [undefined, undefined, ['Unavailable', 'Unavailable']],
    [0, 0, ['0 km', '0 minutes']]]) {
    let bindings;
    const app = createSSRApp({ setup() {
      bindings = proxyRefs(component.setup(props, { expose() {}, emit() {} }));
      const request = bindings.proposalStore.begin('segment', 'walk', new AbortController());
      bindings.proposalStore.complete('segment', request, { segmentId: 'segment',
        geometry: [{ longitude: 1, latitude: 2 }, { longitude: 3, latitude: 4 }], distanceMetres: distance, durationSeconds: duration });
      return () => render({}, [], props, bindings, {}, {});
    } });
    const html = await renderToString(app);
    assert.match(html, /Proposed distance/);
    assert.match(html, /Estimated travel time/);
    for (const value of expected) assert.ok(html.includes(`<strong>${value}</strong>`), html);
    assert.match(html, /Save keeps your manual duration/);
    bindings.proposalStore.discard('segment');
    const discarded = await renderToString(createSSRApp({ render: () => render({}, [], props, bindings, {}, {}) }));
    assert.equal(discarded.includes('Proposed distance'), false);
  }
});
